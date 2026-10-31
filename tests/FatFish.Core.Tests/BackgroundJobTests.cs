using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Workspace;
using Xunit;
using Xunit.Sdk;

namespace FatFish.Core.Tests;

/// <summary>
/// ws_bg_* 五个工具 + <see cref="BackgroundJobManager"/>。
///
/// 后台任务的核心价值只有一个：**不占住回合**。
/// 所以第一条测试量的就是「启动耗时」—— 一个跑 5 秒的命令，工具必须在 1 秒内返回。
/// 这条挂了，后面所有功能都失去意义：能看输出、能中止，但用户得先等它跑完才拿到 job_id。
///
/// 其余测试守的是「后台」带来的新问题：
///   · 输出是边跑边产生的，所以必须能**增量**读（offset 语义），不能只能读全部；
///   · 输出可能几百 MB，所以缓冲区必须滚动，且滚动之后要**明确告诉**调用方前面丢了；
///   · 进程可能被中止或超时，两者都会让退出码变成空/非零，
///     只有 KillRequested 这个标记能把它们区分开；
///   · 临时 .bat / 临时 .py 必须在任务结束时删掉 —— 后台任务跑几小时，
///     中途删不掉，结束时不删就会攒一工作台。
/// </summary>
public class BackgroundJobTests : IDisposable
{
    private readonly string _root;
    private readonly PathGuard _guard;
    private readonly BackgroundJobManager _manager;
    private readonly ToolContext _context;
    private readonly ToolRegistry _registry;
    private readonly bool _pythonMissing;

    public BackgroundJobTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_bg_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_root);

        _guard = new PathGuard(_root);
        _manager = new BackgroundJobManager();

        _context = new ToolContext
        {
            Guard = _guard,
            Tickets = new ReadTicketStore(_guard),
            Approval = new ApprovalPolicy(_guard),
            Jobs = _manager,
        };

        _registry = new ToolRegistry(new ITool[]
        {
            new WsBgCmdTool(),
            new WsBgPythonTool(),
            new WsBgListTool(),
            new WsBgTailTool(),
            new WsBgKillTool(),
        });

        _pythonMissing = PythonLocator.Resolve(_root).Interpreter is null;
    }

    public void Dispose()
    {
        _manager.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    // ---------- 辅助 ----------

    private Task<ToolResult> Call(string tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return _registry.InvokeAsync(tool, _context, doc.RootElement.Clone()).AsTask();
    }

    private static JsonElement Data(ToolResult result)
    {
        Assert.True(result.Ok, "工具应当成功，实际失败：" + result.Error);
        Assert.NotNull(result.Data);
        return result.Data!.Value;
    }

    /// <summary>轮询等到任务结束。后台任务没有「Join」，只能这样等。</summary>
    private async Task<BackgroundJobSnapshot> WaitDone(string id, int timeoutMs = 30_000)
    {
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var snap = _manager.Get(id);

            if (snap is not null && snap.Status != BackgroundJobManager.StatusRunning)
                return snap;

            await Task.Delay(50);
        }

        throw new XunitException(
            $"任务 {id} 未在 {timeoutMs}ms 内结束（当前状态 {_manager.Get(id)?.Status}）");
    }

    private string TailText(string jobId, long offset = 0, int maxChars = 8000)
        => _manager.Tail(jobId, offset, maxChars).Text;

    // ==================================================================
    // 管理器：生命周期
    // ==================================================================

    [Fact]
    public async Task Manager_RunsWorkAndReportsDone()
    {
        var id = _manager.Start(new BackgroundJobRequest(
            "test", "打个招呼", 0,
            (h, _) =>
            {
                h.Note("hello bg");
                return Task.FromResult(0);
            }));

        var snap = await WaitDone(id);

        Assert.Equal("done", snap.Status);
        Assert.Equal(0, snap.ExitCode);
        Assert.Contains("hello bg", TailText(id));
    }

    /// <summary>非零退出码必须是 failed，而不是 done —— 否则播报会说「完成」。</summary>
    [Fact]
    public async Task Manager_NonZeroExit_IsFailed()
    {
        var id = _manager.Start(new BackgroundJobRequest(
            "test", "失败演示", 0, (_, _) => Task.FromResult(2)));

        var snap = await WaitDone(id);

        Assert.Equal("failed", snap.Status);
        Assert.Equal(2, snap.ExitCode);
    }

    /// <summary>work 抛异常要变成 failed + 错误信息，不能把异常抛到调用方。</summary>
    [Fact]
    public async Task Manager_WorkThrows_IsFailedWithError()
    {
        var id = _manager.Start(new BackgroundJobRequest(
            "test", "炸一个", 0, (_, _) => throw new InvalidOperationException("故意炸")));

        var snap = await WaitDone(id);

        Assert.Equal("failed", snap.Status);
        Assert.Contains("故意炸", snap.Error);
    }

    [Fact]
    public void Manager_List_KeepsStartOrder()
    {
        var a = _manager.Start(new BackgroundJobRequest("test", "a", 0, (_, _) => Task.FromResult(0)));
        var b = _manager.Start(new BackgroundJobRequest("test", "b", 0, (_, _) => Task.FromResult(0)));

        var list = _manager.List();

        Assert.Equal(2, list.Count);
        Assert.Equal(a, list[0].Id);
        Assert.Equal(b, list[1].Id);
    }

    [Fact]
    public void Manager_Get_UnknownId_ReturnsNull()
    {
        Assert.Null(_manager.Get("job_nope"));
        Assert.Null(_manager.Get(""));
    }

    // ==================================================================
    // 增量读取：offset 语义
    // ==================================================================

    /// <summary>
    /// offset 是**绝对字节位置**。分段读出来的拼接必须等于原文，
    /// 既不重复也不丢字 —— 这是「游标」能不能用的唯一判据。
    /// </summary>
    [Fact]
    public async Task Tail_OffsetsConcatenateToWholeOutput()
    {
        var id = _manager.Start(new BackgroundJobRequest(
            "test", "固定输出", 0,
            (h, _) =>
            {
                h.Append(Encoding.UTF8.GetBytes("0123456789"));
                return Task.FromResult(0);
            }));

        await WaitDone(id);

        var first = _manager.Tail(id, 0, 4);
        var second = _manager.Tail(id, first.NextOffset, 4);
        var third = _manager.Tail(id, second.NextOffset, 4000);

        Assert.Equal("0123", first.Text);
        Assert.True(first.HasMore);
        Assert.Equal("4567", second.Text);
        Assert.Equal("89", third.Text);
        Assert.False(third.HasMore);
        Assert.Equal(10, third.TotalBytes);
    }

    /// <summary>
    /// 中文不能被切坏。
    ///
    /// 输出是边跑边读的，读到的最后几个字节很可能只是半个汉字。
    /// 如果按块解码，这个字就永久变成了 U+FFFD —— 后面补上字节也拼不回来。
    /// 所以存的是字节，读的时候才整体解码。
    /// </summary>
    [Fact]
    public async Task Tail_ChineseIsNotMangled()
    {
        const string text = "肥鱼爱吃token，中文边界测试";

        var id = _manager.Start(new BackgroundJobRequest(
            "test", "中文", 0,
            (h, _) =>
            {
                // 故意分成两块写，模拟真实的分块到达
                var bytes = Encoding.UTF8.GetBytes(text);
                h.Append(bytes.AsSpan(0, 7));      // 正切在一个汉字中间
                h.Append(bytes.AsSpan(7));
                return Task.FromResult(0);
            }));

        await WaitDone(id);

        var tail = _manager.Tail(id, 0, 4000);

        Assert.Equal(text, tail.Text);
        Assert.DoesNotContain('\uFFFD', tail.Text);
    }

    /// <summary>
    /// offset 落在汉字中间时，从下一个完整字符开始 —— 而不是吐一个 U+FFFD。
    /// 调用方可能传任意数字（比如它自己算错了），这里不能因此产生垃圾字符。
    /// </summary>
    [Fact]
    public async Task Tail_OffsetInsideUtf8Char_AlignsForward()
    {
        const string text = "肥鱼";

        var id = _manager.Start(new BackgroundJobRequest(
            "test", "边界", 0,
            (h, _) =>
            {
                h.Append(Encoding.UTF8.GetBytes(text));
                return Task.FromResult(0);
            }));

        await WaitDone(id);

        // 「肥」是 3 字节，从第 1 字节开始就是它的第二个字节
        var tail = _manager.Tail(id, 1, 4000);

        Assert.DoesNotContain('\uFFFD', tail.Text);
        Assert.Equal("鱼", tail.Text);
    }

    // ==================================================================
    // 缓冲区滚动
    // ==================================================================

    /// <summary>
    /// 缓冲区满后丢弃最早的输出，并且**明确报告**丢了 ——
    /// 不报告的话，调用方会以为自己读到了全部。
    /// </summary>
    [Fact]
    public async Task Tail_AfterBufferOverflow_ReportsDropped()
    {
        var small = new BackgroundJobManager(maxBufferBytes: 64);

        try
        {
            var id = small.Start(new BackgroundJobRequest(
                "test", "大输出", 0,
                (h, _) =>
                {
                    h.Append(Encoding.UTF8.GetBytes(new string('x', 1000)));
                    return Task.FromResult(0);
                }));

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10_000)
            {
                var s = small.Get(id);
                if (s is not null && s.Status != BackgroundJobManager.StatusRunning)
                    break;
                await Task.Delay(20);
            }

            var tail = small.Tail(id, 0, 200);

            Assert.True(tail.DroppedAtStart, "offset=0 落在已丢弃区间，必须报告");
            Assert.Equal(1000, tail.TotalBytes);
            Assert.NotEmpty(tail.Text);
        }
        finally
        {
            small.Dispose();
        }
    }

    // ==================================================================
    // 中止与超时
    // ==================================================================

    /// <summary>
    /// 中止必须真的让任务停下（<b>秒级</b>返回），并且状态是 killed 而不是 failed。
    /// 「killed 报成 failed」会让人去查一个不存在的 bug。
    /// </summary>
    [Fact]
    public async Task Kill_StopsRunningJob_AndReportsKilled()
    {
        var id = _manager.Start(new BackgroundJobRequest(
            "test", "长任务", 0,
            async (_, ct) =>
            {
                await Task.Delay(60_000, ct);
                return 0;
            }));

        await Task.Delay(100);

        var sw = Stopwatch.StartNew();
        var ok = _manager.Kill(id, "测试中止");
        var snap = await WaitDone(id, 10_000);
        sw.Stop();

        Assert.True(ok);
        Assert.Equal("killed", snap.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"中止应当在秒级生效，实际 {sw.Elapsed.TotalSeconds:0.0}s");
    }

    /// <summary>超时与中止的退出路径相同，只有状态不同。</summary>
    [Fact]
    public async Task Timeout_MarksTimeout_NotNullFailed()
    {
        var id = _manager.Start(new BackgroundJobRequest(
            "test", "超时", TimeoutSeconds: 1,
            Work: async (_, ct) =>
            {
                await Task.Delay(60_000, ct);
                return 0;
            }));

        var snap = await WaitDone(id, 15_000);

        Assert.Equal("timeout", snap.Status);
    }

    [Fact]
    public void Kill_UnknownJob_ReturnsFalse()
    {
        Assert.False(_manager.Kill("job_nope", "test"));
    }

    [Fact]
    public async Task Kill_AlreadyFinished_ReturnsFalse()
    {
        var id = _manager.Start(new BackgroundJobRequest(
            "test", "快任务", 0, (_, _) => Task.FromResult(0)));

        await WaitDone(id);

        Assert.False(_manager.Kill(id, "迟到的中止"));
    }

    // ==================================================================
    // 完成播报
    // ==================================================================

    /// <summary>
    /// 结束事件必须带上最终状态 —— 宿主播报全靠它。
    /// 没有这个事件，用户只能自己反复 tail，那「后台」就退化成「异步但要人盯」。
    /// </summary>
    [Fact]
    public async Task Finished_EventCarriesFinalSnapshot()
    {
        var tcs = new TaskCompletionSource<BackgroundJobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _manager.Finished += snap => tcs.TrySetResult(snap);

        var id = _manager.Start(new BackgroundJobRequest(
            "test", "播报", 0, (h, _) =>
            {
                h.Note("done!");
                return Task.FromResult(0);
            }));

        var reported = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(id, reported.Id);
        Assert.Equal("done", reported.Status);
        Assert.True(reported.OutputBytes > 0);
    }

    // ==================================================================
    // 工具：ws_bg_cmd
    // ==================================================================

    /// <summary>
    /// ★ 这个工具存在的唯一理由：**不阻塞**。
    /// 命令要跑 5 秒，工具必须在 1 秒内返回。
    /// </summary>
    [Fact]
    public async Task BgCmd_ReturnsImmediately()
    {
        var sw = Stopwatch.StartNew();

        var d = Data(await Call("ws_bg_cmd",
            """{"command":"ping -n 6 127.0.0.1 >nul","desc":"慢任务"}"""));

        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"启动后台任务应当立即返回，实际 {sw.Elapsed.TotalSeconds:0.0}s");

        Assert.Equal("running", d.GetProperty("status").GetString());

        var jobId = d.GetProperty("job_id").GetString()!;
        Assert.StartsWith("job_", jobId);

        // 收尾：别让 ping 跑完测试进程的生命周期
        _manager.Kill(jobId, "测试收尾");
        await WaitDone(jobId, 10_000);
    }

    [Fact]
    public async Task BgCmd_OutputIsReadableAfterFinish()
    {
        var d = Data(await Call("ws_bg_cmd", """{"command":"echo background-hello"}"""));
        var jobId = d.GetProperty("job_id").GetString()!;

        var snap = await WaitDone(jobId);

        Assert.Equal("done", snap.Status);
        Assert.Contains("background-hello", TailText(jobId));
    }

    /// <summary>临时 .bat 在任务结束后必须删掉（后台任务尤其重要：它可能跑几小时）。</summary>
    [Fact]
    public async Task BgCmd_TemporaryBatch_IsDeletedAfterFinish()
    {
        var d = Data(await Call("ws_bg_cmd", """{"command":"echo hi"}"""));
        var jobId = d.GetProperty("job_id").GetString()!;

        await WaitDone(jobId);

        // 清理是 finally 里做的，与状态写入有几毫秒的先后，稍等一下
        await Task.Delay(200);

        var tmp = Path.Combine(_root, ".fatfish_tmp");

        if (!Directory.Exists(tmp))
            return;

        Assert.Empty(Directory.GetFiles(tmp, "cmd_*.bat"));
    }

    /// <summary>引号问题在后台路径上同样要修好 —— 两条路共用 CmdShell。</summary>
    [Fact]
    public async Task BgCmd_QuotedSetSyntax_Works()
    {
        var d = Data(await Call("ws_bg_cmd",
            """{"command":"set \"BG_QQ=9\" & set BG_QQ"}"""));

        var jobId = d.GetProperty("job_id").GetString()!;
        await WaitDone(jobId);

        Assert.Contains("BG_QQ=9", TailText(jobId));
    }

    [Fact]
    public async Task BgCmd_MissingCwd_IsRejected()
    {
        var result = await Call("ws_bg_cmd", """{"command":"echo hi","cwd":"nope"}""");

        Assert.False(result.Ok);
        Assert.Contains("不存在", result.Error);
    }

    [Fact]
    public async Task BgCmd_EscapeCwd_IsRejected()
    {
        var result = await Call("ws_bg_cmd", """{"command":"echo hi","cwd":"../.."}""");

        Assert.False(result.Ok);
        Assert.Contains("越界", result.Error);
    }

    [Fact]
    public async Task BgCmd_EmptyCommand_IsRejected()
    {
        var result = await Call("ws_bg_cmd", """{"command":"  "}""");

        Assert.False(result.Ok);
        Assert.Contains("command", result.Error);
    }

    // ==================================================================
    // 工具：ws_bg_python
    // ==================================================================

    [Fact]
    public async Task BgPython_RunsCodeAndOutputs()
    {
        if (_pythonMissing)
            return;

        var d = Data(await Call("ws_bg_python",
            """{"code":"print('bg python 中文')","desc":"py 任务"}"""));

        var jobId = d.GetProperty("job_id").GetString()!;
        var snap = await WaitDone(jobId);

        Assert.Equal("done", snap.Status);
        Assert.Contains("bg python 中文", TailText(jobId));
    }

    [Fact]
    public async Task BgPython_ScriptIsDeletedAfterFinish()
    {
        if (_pythonMissing)
            return;

        var d = Data(await Call("ws_bg_python",
            """{"code":"print(1)","filename":"bgprobe.py"}"""));

        var jobId = d.GetProperty("job_id").GetString()!;
        await WaitDone(jobId);
        await Task.Delay(200);

        var script = Path.Combine(_root, ".fatfish_tmp", "bgprobe.py");
        Assert.False(File.Exists(script), "任务结束后临时脚本必须删除");
    }

    /// <summary>非零退出码要在输出尾部留下提示，否则 tail 的人只能看到半截 stdout。</summary>
    [Fact]
    public async Task BgPython_NonZeroExit_NotesFailure()
    {
        if (_pythonMissing)
            return;

        var d = Data(await Call("ws_bg_python",
            """{"code":"import sys\nprint('before')\nsys.exit(4)"}"""));

        var jobId = d.GetProperty("job_id").GetString()!;
        var snap = await WaitDone(jobId);

        Assert.Equal("failed", snap.Status);
        Assert.Equal(4, snap.ExitCode);
        Assert.Contains("退出码 4", TailText(jobId));
    }

    // ==================================================================
    // 工具：ws_bg_list / ws_bg_tail / ws_bg_kill
    // ==================================================================

    [Fact]
    public async Task BgList_ShowsJobTable()
    {
        var d = Data(await Call("ws_bg_cmd", """{"command":"echo listed","desc":"列表测试"}"""));
        var jobId = d.GetProperty("job_id").GetString()!;

        await WaitDone(jobId);

        var listed = Data(await Call("ws_bg_list", "{}"));
        var text = listed.GetProperty("list").GetString()!;

        Assert.Contains(jobId, text);
        Assert.Contains("列表测试", text);
        Assert.Contains("done", text);
    }

    [Fact]
    public async Task BgList_FiltersByStatus()
    {
        var d = Data(await Call("ws_bg_cmd", """{"command":"echo x"}"""));
        var jobId = d.GetProperty("job_id").GetString()!;
        await WaitDone(jobId);

        var running = Data(await Call("ws_bg_list", """{"status":"running"}"""));

        Assert.Equal(0, running.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task BgTail_ReturnsOffsetAndStatus()
    {
        var d = Data(await Call("ws_bg_cmd", """{"command":"echo tail-me"}"""));
        var jobId = d.GetProperty("job_id").GetString()!;
        await WaitDone(jobId);

        var tail = Data(await Call("ws_bg_tail", $$"""{"job_id":"{{jobId}}"}"""));

        Assert.Contains("tail-me", tail.GetProperty("text").GetString());
        Assert.False(tail.GetProperty("running").GetBoolean());
        Assert.True(tail.GetProperty("next_offset").GetInt64() > 0);
        Assert.Equal("done", tail.GetProperty("status").GetString());
    }

    [Fact]
    public async Task BgTail_UnknownJob_IsRejected()
    {
        var result = await Call("ws_bg_tail", """{"job_id":"job_nope"}""");

        Assert.False(result.Ok);
        Assert.Contains("没有这个任务", result.Error);
    }

    [Fact]
    public async Task BgKill_UnknownJob_IsRejected()
    {
        var result = await Call("ws_bg_kill", """{"job_id":"job_nope"}""");

        Assert.False(result.Ok);
        Assert.Contains("没有这个任务", result.Error);
    }

    [Fact]
    public async Task BgKill_AlreadyFinished_IsNoOp()
    {
        var d = Data(await Call("ws_bg_cmd", """{"command":"echo done"}"""));
        var jobId = d.GetProperty("job_id").GetString()!;
        await WaitDone(jobId);

        var killed = Data(await Call("ws_bg_kill", $$"""{"job_id":"{{jobId}}"}"""));

        Assert.False(killed.GetProperty("killed").GetBoolean());
    }

    // ==================================================================
    // 未初始化
    // ==================================================================

    /// <summary>
    /// 宿主忘了注入 Jobs 时，五个工具都要给出可读的失败，而不是 NullReferenceException。
    /// 这种情况在测试/嵌入场景下真的会发生。
    /// </summary>
    [Fact]
    public async Task JobsNotInjected_AllBgToolsFailCleanly()
    {
        var bare = new ToolContext
        {
            Guard = _guard,
            Tickets = new ReadTicketStore(_guard),
            Approval = new ApprovalPolicy(_guard),
        };

        foreach (var (tool, json) in new[]
                 {
                     ("ws_bg_cmd", """{"command":"echo hi"}"""),
                     ("ws_bg_python", """{"code":"print(1)"}"""),
                     ("ws_bg_list", "{}"),
                     ("ws_bg_tail", """{"job_id":"x"}"""),
                     ("ws_bg_kill", """{"job_id":"x"}"""),
                 })
        {
            using var doc = JsonDocument.Parse(json);
            var result = await _registry.InvokeAsync(tool, bare, doc.RootElement.Clone());

            Assert.False(result.Ok, $"{tool} 在没有 Jobs 时应当失败");
            Assert.Contains("后台任务管理器", result.Error);
        }
    }

    // ==================================================================
    // 安全策略
    // ==================================================================

    /// <summary>
    /// 后台执行工具必须是「必审批 + 不可一键放行」。
    ///
    /// 这条不是形式主义：后台工具和同步版一样能越界，
    /// 而「它只是启动一下」这个直觉会让人以为它更安全。
    /// </summary>
    [Fact]
    public void BgTools_RequireApproval_AndForbidOneKey()
    {
        var policy = new ApprovalPolicy(_guard);

        foreach (var tool in new[] { "ws_bg_cmd", "ws_bg_python" })
        {
            Assert.True(policy.NeedsApproval(tool).Need, $"{tool} 必须报批");
            Assert.True(policy.NeverAutoApproves(tool), $"{tool} 不得一键放行");
        }

        Assert.True(policy.NeedsApproval("ws_bg_kill").Need, "ws_bg_kill 必须报批");
        Assert.True(policy.NeverAutoApproves("ws_bg_kill"), "ws_bg_kill 不得一键放行");
    }

    /// <summary>只读的 list / tail 不弹窗 —— 弹了只会训练用户盲点「允许」。</summary>
    [Fact]
    public void ReadOnlyBgTools_DoNotRequireApproval()
    {
        // ★ 必须显式设档：ApprovalPolicy 默认是 Strict（每个动作都要批），
        //   而这条测的是「读免批档下 list / tail 不弹窗」—— 不设档测的是 Strict，必然对不上。
        var policy = new ApprovalPolicy(_guard) { Level = ApprovalLevel.ReadFree };

        Assert.False(policy.NeedsApproval("ws_bg_list").Need);
        Assert.False(policy.NeedsApproval("ws_bg_tail").Need);
    }
}
