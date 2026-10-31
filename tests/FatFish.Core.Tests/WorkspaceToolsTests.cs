using System.Text;
using System.Text.Json;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// ws_list / ws_search / ws_run_cmd 的行为测试。
///
/// 这批测试的重点不是「能跑通」，而是守住几个**一旦坏了很难发现**的性质：
///   · ws_search 绝不能把 .env 的内容带出来（否则密钥直接进模型上下文）；
///   · ws_run_cmd 的输出必须能从 GBK 字节正确解码（否则中文全是乱码）；
///   · ws_run_cmd 的输出超限时不能死锁（读一半就停会让子进程卡在 write 上）；
///   · 超时必须连同子进程一起杀掉。
/// </summary>
public class WorkspaceToolsTests : IDisposable
{
    private readonly string _root;
    private readonly PathGuard _guard;
    private readonly ToolContext _context;
    private readonly ToolRegistry _registry;

    public WorkspaceToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_tools_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        _guard = new PathGuard(_root);
        var tickets = new ReadTicketStore(_guard);
        var policy = new ApprovalPolicy(_guard);

        _context = new ToolContext
        {
            Guard = _guard,
            Tickets = tickets,
            Approval = policy,
        };

        _registry = new ToolRegistry(new ITool[]
        {
            new WsReadTool(),
            new WsListTool(),
            new WsSearchTool(),
            new WsWriteTool(),
            new WsAppendTool(),
            new WsReplaceTool(),
            new WsDeleteTool(),
            new WsRunCmdTool(),
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论（比如被占用）
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

    private string Write(string relPath, string content)
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    // ==================================================================
    // ws_list
    // ==================================================================

    [Fact]
    public async Task WsList_ListsDirectoriesAndFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Write("README.md", "# 说明");
        Write("src/main.cs", "class A {}");

        var result = await Call("ws_list", "{}");
        var data = Data(result);

        var listing = data.GetProperty("listing").GetString()!;

        // 目录带尾斜杠、文件不带
        Assert.Contains("src/", listing);
        Assert.Contains("README.md", listing);

        Assert.Equal(1, data.GetProperty("dirs").GetInt32());
        Assert.Equal(1, data.GetProperty("files").GetInt32());
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }

    /// <summary>
    /// depth 的语义 ＝ <c>tree -L N</c>：N 是「要列到第几层」，而不是「往下钻几次」。
    /// 把三层边界都断言一遍，避免以后有人改 Walk 的循环条件时悄悄差一层。
    /// </summary>
    [Fact]
    public async Task WsList_DepthFollowsTreeLSemantics()
    {
        Write("src/deep/inner.cs", "x");

        // depth=1 → 只有根的直接子项
        var d1 = Data(await Call("ws_list", "{\"depth\":1}")).GetProperty("listing").GetString()!;
        Assert.Contains("src/", d1);
        Assert.DoesNotContain("deep/", d1);

        // depth=2 → 能看到 deep/，但看不到 deep 里面的文件（与 tree -L 2 一致）
        var d2 = Data(await Call("ws_list", "{\"depth\":2}")).GetProperty("listing").GetString()!;
        Assert.Contains("deep/", d2);
        Assert.DoesNotContain("inner.cs", d2);

        // depth=3 → 才轮到 inner.cs
        var d3 = Data(await Call("ws_list", "{\"depth\":3}")).GetProperty("listing").GetString()!;
        Assert.Contains("inner.cs", d3);
    }

    [Fact]
    public async Task WsList_SkipsIgnoredDirectoriesByDefault()
    {
        Write("obj/junk.cs", "x");
        Write("keep.cs", "x");

        var listing = Data(await Call("ws_list", "{}")).GetProperty("listing").GetString()!;

        Assert.DoesNotContain("junk.cs", listing);
        Assert.Contains("keep.cs", listing);

        // 显式要求时应该能看到
        var withIgnored = Data(await Call("ws_list", "{\"include_ignored\":true}"))
            .GetProperty("listing").GetString()!;
        Assert.Contains("obj/", withIgnored);
    }

    [Fact]
    public async Task WsList_RespectsMaxEntries()
    {
        for (var i = 0; i < 20; i++)
            Write($"f{i:D2}.txt", "x");

        var data = Data(await Call("ws_list", "{\"max_entries\":5}"));

        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(5, data.GetProperty("files").GetInt32());
    }

    [Fact]
    public async Task WsList_OnFile_ReportsHelpfulError()
    {
        Write("a.txt", "x");

        var result = await Call("ws_list", "{\"path\":\"a.txt\"}");

        Assert.False(result.Ok);
        Assert.Contains("ws_read", result.Error);
    }

    [Fact]
    public async Task WsList_PathEscape_IsRejected()
    {
        var result = await Call("ws_list", "{\"path\":\"../\"}");

        Assert.False(result.Ok);
        Assert.Contains("越界", result.Error);
    }

    // ==================================================================
    // ws_search
    // ==================================================================

    [Fact]
    public async Task WsSearch_FindsMatchesWithLineNumbers()
    {
        Write("a.cs", "第一行\nvar foo = 1;\n第三行\n");

        var data = Data(await Call("ws_search", "{\"keyword\":\"foo\"}"));

        Assert.Equal(1, data.GetProperty("hits").GetInt32());

        var matches = data.GetProperty("matches").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Single(matches);
        Assert.Contains("a.cs:2:", matches[0]);
        Assert.Contains("var foo = 1;", matches[0]);
    }

    [Fact]
    public async Task WsSearch_IsCaseInsensitiveByDefault()
    {
        Write("a.txt", "Hello World\n");

        var loose = Data(await Call("ws_search", "{\"keyword\":\"hello\"}"));
        Assert.Equal(1, loose.GetProperty("hits").GetInt32());

        var strict = Data(await Call("ws_search", "{\"keyword\":\"hello\",\"case_sensitive\":true}"));
        Assert.Equal(0, strict.GetProperty("hits").GetInt32());
    }

    /// <summary>
    /// ★ 本组最重要的一条。
    ///
    /// 泄露路径是这样的：审批层只看参数里的 path，而 ws_search 的 path 是目录（或空串）。
    /// CheckSensitive("") 判为「干净」，闸门放行，于是检索结果把 .env 里的密钥
    /// 原样带进模型上下文 —— 这是一条完全绕过审批的读取通道。
    /// </summary>
    [Fact]
    public async Task WsSearch_NeverLeaksSensitiveFiles()
    {
        Write(".env", "DEEPSEEK_API_KEY=sk-very-secret-value-12345\n");
        Write("app.py", "DEEPSEEK_API_KEY=placeholder\n");

        var result = await Call("ws_search", "{\"keyword\":\"DEEPSEEK_API_KEY\"}");
        var data = Data(result);

        var matches = string.Join("\n",
            data.GetProperty("matches").EnumerateArray().Select(e => e.GetString()));

        // 普通文件命中，敏感文件没命中
        Assert.Contains("app.py", matches);
        Assert.DoesNotContain(".env", matches);
        Assert.DoesNotContain("sk-very-secret-value-12345", matches);

        // 而且要把「跳过几个」如实报出来，模型才知道自己没看全
        Assert.True(data.GetProperty("skipped_sensitive").GetInt32() >= 1);
        Assert.NotNull(result.Notice);
        Assert.Contains("敏感文件", result.Notice);
    }

    [Fact]
    public async Task WsSearch_ExplicitSensitiveFile_IsRefused()
    {
        Write(".env", "SECRET=1\n");

        var result = await Call("ws_search", "{\"keyword\":\"SECRET\",\"path\":\".env\"}");

        Assert.False(result.Ok);
        Assert.Contains("敏感", result.Error);
        Assert.Contains("ws_read", result.Error);
    }

    [Fact]
    public async Task WsSearch_SkipsBinaryFiles()
    {
        // 带 NUL 字节 = 二进制
        var bin = Path.Combine(_root, "blob.bin");
        File.WriteAllBytes(bin, new byte[] { 0x4D, 0x5A, 0x00, 0x00, 0x66, 0x6F, 0x6F });

        Write("text.txt", "foo\n");

        var data = Data(await Call("ws_search", "{\"keyword\":\"foo\"}"));

        Assert.Equal(1, data.GetProperty("hits").GetInt32());
        Assert.True(data.GetProperty("skipped_binary").GetInt32() >= 1);
    }

    [Fact]
    public async Task WsSearch_GlobFiltersFileNames()
    {
        Write("a.cs", "needle\n");
        Write("b.md", "needle\n");

        var data = Data(await Call("ws_search", "{\"keyword\":\"needle\",\"glob\":\"*.cs\"}"));

        Assert.Equal(1, data.GetProperty("hits").GetInt32());
        Assert.Contains("a.cs", data.GetProperty("matches")[0].GetString());
    }

    [Fact]
    public async Task WsSearch_NoHits_ReportsScannedCount()
    {
        Write("a.txt", "aaa\n");
        Write("b.txt", "bbb\n");

        var data = Data(await Call("ws_search", "{\"keyword\":\"不存在的词\"}"));

        Assert.Equal(0, data.GetProperty("hits").GetInt32());
        Assert.Equal(2, data.GetProperty("scanned").GetInt32());

        // 摘要必须写清「扫了几个文件」，避免模型把「没找到」说成「不存在」
        var summary = data.GetProperty("summary").GetString()!;
        Assert.Contains("已扫描 2 个文本文件", summary);
    }

    [Fact]
    public async Task WsSearch_RespectsMaxResults()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 30; i++)
            sb.Append("hit line ").Append(i).Append('\n');

        Write("many.txt", sb.ToString());

        var data = Data(await Call("ws_search", "{\"keyword\":\"hit\",\"max_results\":5}"));

        Assert.Equal(5, data.GetProperty("hits").GetInt32());
        Assert.True(data.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task WsSearch_SingleFileScope_Works()
    {
        Write("a.txt", "target\n");
        Write("b.txt", "target\n");

        var data = Data(await Call("ws_search", "{\"keyword\":\"target\",\"path\":\"a.txt\"}"));

        Assert.Equal(1, data.GetProperty("hits").GetInt32());
        Assert.Contains("a.txt", data.GetProperty("matches")[0].GetString());
    }

    // ==================================================================
    // ws_run_cmd
    // ==================================================================

    [Fact]
    public async Task WsRunCmd_EchoCapturesStdout()
    {
        var data = Data(await Call("ws_run_cmd", "{\"command\":\"echo hello-fish\"}"));

        Assert.Equal(0, data.GetProperty("exit_code").GetInt32());
        Assert.Contains("hello-fish", data.GetProperty("stdout").GetString());
        Assert.False(data.GetProperty("timed_out").GetBoolean());
    }

    /// <summary>
    /// ★ 中文输出的端到端验证。
    ///
    /// cmd 把文本按 OEM 代码页（简中 936 / GBK）写成字节，
    /// 若按 UTF-8 解会得到乱码。这条测试走完整链路：
    /// 命令行传中文 → cmd 输出 GBK 字节 → ProcessEncoding 自动识别 → 文本正确。
    /// </summary>
    [Fact]
    public async Task WsRunCmd_DecodesChineseOutput()
    {
        var data = Data(await Call("ws_run_cmd", "{\"command\":\"echo 肥鱼爱吃token\"}"));

        Assert.Contains("肥鱼爱吃token", data.GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task WsRunCmd_NonZeroExitCodeIsReported()
    {
        var data = Data(await Call("ws_run_cmd", "{\"command\":\"exit 3\"}"));

        Assert.Equal(3, data.GetProperty("exit_code").GetInt32());

        // 非零退出码不等于工具失败 —— 工具成功执行了，只是命令返回了非零
        Assert.False(data.GetProperty("timed_out").GetBoolean());
    }

    [Fact]
    public async Task WsRunCmd_StderrIsSeparated()
    {
        var data = Data(await Call("ws_run_cmd",
            "{\"command\":\"dir C:\\\\no-such-dir-fatfish-xyz\"}"));

        Assert.Equal(1, data.GetProperty("exit_code").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("stderr").GetString()));
    }

    [Fact]
    public async Task WsRunCmd_Cwd_IsHonored()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Write("sub/marker.txt", "x");

        var data = Data(await Call("ws_run_cmd",
            "{\"command\":\"dir /b\",\"cwd\":\"sub\"}"));

        Assert.Contains("marker.txt", data.GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task WsRunCmd_CwdEscape_IsRejected()
    {
        var result = await Call("ws_run_cmd", "{\"command\":\"dir\",\"cwd\":\"..\"}");

        Assert.False(result.Ok);
        Assert.Contains("越界", result.Error);
    }

    [Fact]
    public async Task WsRunCmd_MissingCommand_IsRejected()
    {
        var result = await Call("ws_run_cmd", "{}");

        Assert.False(result.Ok);
        Assert.Contains("command", result.Error);
    }

    [Fact]
    public async Task WsRunCmd_UnknownShell_IsRejected()
    {
        var result = await Call("ws_run_cmd", "{\"command\":\"echo x\",\"shell\":\"bash\"}");

        Assert.False(result.Ok);
        Assert.Contains("cmd", result.Error);
    }

    /// <summary>
    /// ★ 超时必须杀掉整棵进程树。
    ///
    /// 只杀 cmd 本身、留下子进程握着输出管道的话，
    /// 读线程永远等不到 EOF，整个工具调用会挂死在这里。
    /// 所以除了断言 timed_out，还要断言「真的很快返回了」。
    /// </summary>
    [Fact]
    public async Task WsRunCmd_TimeoutKillsProcessTree()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var data = Data(await Call("ws_run_cmd",
            "{\"command\":\"ping -n 6 127.0.0.1\",\"timeout_seconds\":1}"));

        sw.Stop();

        Assert.True(data.GetProperty("timed_out").GetBoolean());

        // 5 秒的 ping 被 1 秒掐断；给足余量但明显远小于 5 秒
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"超时后应在数秒内返回，实际用了 {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// ★ 输出超限不能死锁。
    ///
    /// 实现里「读够上限就停手」是错的：子进程写满管道缓冲区后会阻塞在 write 上，
    /// 于是既不退出、也不产出，工具永远等不到 EOF。
    /// 正确做法是「保留有上限，但一直读到 EOF」。
    /// 这条测试用 100KB 输出来验证没有卡死。
    /// </summary>
    [Fact]
    public async Task WsRunCmd_LargeOutput_DoesNotDeadlock()
    {
        // 100KB，远超 24KB 的保留上限
        Write("big.txt", new string('x', 100 * 1024));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var data = Data(await Call("ws_run_cmd",
            "{\"command\":\"type big.txt\",\"timeout_seconds\":30}"));

        sw.Stop();

        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.True(sw.ElapsedMilliseconds < 20_000,
            $"大输出不该卡住，实际用了 {sw.ElapsedMilliseconds}ms");

        // 至少保留了接近上限的内容
        Assert.True(data.GetProperty("stdout").GetString()!.Length > 10_000);
    }

    [Fact]
    public async Task WsRunCmd_PowerShellShell_Works()
    {
        var data = Data(await Call("ws_run_cmd",
            "{\"command\":\"Write-Output ('a' + 'b')\",\"shell\":\"powershell\"}"));

        Assert.Contains("ab", data.GetProperty("stdout").GetString());
    }

    /// <summary>
    /// PowerShell 走 -EncodedCommand，引号与中文都不该出问题。
    /// 这是「命令里带引号」这个经典坑的守门测试。
    /// </summary>
    [Fact]
    public async Task WsRunCmd_PowerShellHandlesQuotesAndChinese()
    {
        var data = Data(await Call("ws_run_cmd",
            "{\"command\":\"Write-Output \\\"带 引号 和 中文\\\"\",\"shell\":\"powershell\"}"));

        Assert.Contains("带 引号 和 中文", data.GetProperty("stdout").GetString());
    }

    /// <summary>
    /// PowerShell 的进度流不该混进 stderr。
    ///
    /// 输出被重定向时，PowerShell 会把进度信息序列化成 CLIXML 写进 stderr
    /// （实测 596 字节的 "#&lt; CLIXML ... progress"），模型会当成错误来读。
    /// 实现里用 $ProgressPreference='SilentlyContinue' 关掉了它，这条测试守住结果。
    /// </summary>
    [Fact]
    public async Task WsRunCmd_PowerShellStderrHasNoClixmlNoise()
    {
        var data = Data(await Call("ws_run_cmd",
            "{\"command\":\"Get-ChildItem -Name\",\"shell\":\"powershell\"}"));

        var stderr = data.GetProperty("stderr").GetString();
        Assert.True(string.IsNullOrEmpty(stderr), "stderr 不该有内容，实际：" + stderr);
        Assert.DoesNotContain("CLIXML", data.GetProperty("stdout").GetString());
    }

    // ==================================================================
    // ws_append
    // ==================================================================

    /// <summary>
    /// 已有文件必须凭读凭证才能追加 —— 且被拒绝时断言文件「物理上没变」。
    /// </summary>
    [Fact]
    public async Task WsAppend_RequiresTicket_AndDoesNotTouchFile()
    {
        Write("note.txt", "第一行\n");

        var denied = await Call("ws_append", "{\"path\":\"note.txt\",\"content\":\"第二行\\n\"}");

        Assert.False(denied.Ok);
        Assert.Contains("读凭证", denied.Error);
        Assert.Equal("第一行\n", File.ReadAllText(Path.Combine(_root, "note.txt")));

        Data(await Call("ws_read", "{\"path\":\"note.txt\"}"));

        var data = Data(await Call("ws_append", "{\"path\":\"note.txt\",\"content\":\"第二行\\n\"}"));

        Assert.Equal("第一行\n第二行\n", File.ReadAllText(Path.Combine(_root, "note.txt")));
        Assert.False(data.GetProperty("created").GetBoolean());
        Assert.Equal(4, data.GetProperty("appended_chars").GetInt32());
    }

    /// <summary>
    /// 文件不存在时「追加」=「新建」，无需凭证 —— 没有可覆盖的数据。
    /// 与 Python 版语义一致（_check_ticket 对不存在的路径直接返回 True）。
    /// </summary>
    [Fact]
    public async Task WsAppend_MissingFile_IsCreatedWithoutTicket()
    {
        var data = Data(await Call("ws_append", "{\"path\":\"new.txt\",\"content\":\"hello\"}"));

        Assert.True(data.GetProperty("created").GetBoolean());
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "new.txt")));
    }

    /// <summary>
    /// ★ 空白内容与「参数缺失」是两件事，不能混为一谈。
    ///
    /// 追加一个换行是合法操作（写日志、给文件收尾加空行全靠它），
    /// 所以不能用 IsNullOrWhiteSpace 判空；但空串必须拒绝 ——
    /// 追加 0 字节却返回成功，模型会以为自己写了一行，之后就再也查不出问题。
    /// </summary>
    [Fact]
    public async Task WsAppend_BlankAllowed_ButEmptyRejected()
    {
        Write("log.txt", "a");
        Data(await Call("ws_read", "{\"path\":\"log.txt\"}"));

        Data(await Call("ws_append", "{\"path\":\"log.txt\",\"content\":\"\\n\"}"));
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(_root, "log.txt")));

        Write("b.txt", "a");
        Data(await Call("ws_read", "{\"path\":\"b.txt\"}"));

        var empty = await Call("ws_append", "{\"path\":\"b.txt\",\"content\":\"\"}");
        Assert.False(empty.Ok);
        Assert.Contains("空串", empty.Error);

        var missing = await Call("ws_append", "{\"path\":\"b.txt\"}");
        Assert.False(missing.Ok);
        Assert.Contains("content", missing.Error);
    }

    /// <summary>
    /// 根一级文件追加前必须留下原件 —— 追加也可能把 README 写成半截夹生。
    /// </summary>
    [Fact]
    public async Task WsAppend_RootLevelFile_IsBackedUp()
    {
        Write("README.md", "旧内容");
        Data(await Call("ws_read", "{\"path\":\"README.md\"}"));

        var data = Data(await Call("ws_append", "{\"path\":\"README.md\",\"content\":\"新内容\"}"));

        Assert.Equal("旧内容新内容", File.ReadAllText(Path.Combine(_root, "README.md")));

        var backup = data.GetProperty("backup").GetString();
        Assert.NotNull(backup);
        Assert.Contains("_backup/", backup);
        Assert.Equal("旧内容", File.ReadAllText(Path.Combine(_root, backup!)));
    }

    [Fact]
    public async Task WsAppend_ToDirectory_IsRejected()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var result = await Call("ws_append", "{\"path\":\"sub\",\"content\":\"x\"}");

        Assert.False(result.Ok);
        Assert.Contains("目录", result.Error);
    }

    /// <summary>
    /// 读完之后文件被外部改动 → 凭证失效，追加必须被拒绝。
    /// 这是「读了旧版、改出新冲突」的守门测试。
    /// </summary>
    [Fact]
    public async Task WsAppend_ExternalChange_InvalidatesTicket()
    {
        Write("t.txt", "v1");
        Data(await Call("ws_read", "{\"path\":\"t.txt\"}"));

        File.WriteAllText(Path.Combine(_root, "t.txt"), "v2-changed-externally");

        var result = await Call("ws_append", "{\"path\":\"t.txt\",\"content\":\"x\"}");

        Assert.False(result.Ok);
        Assert.Contains("凭证失效", result.Error);
        Assert.Equal("v2-changed-externally", File.ReadAllText(Path.Combine(_root, "t.txt")));
    }

    // ==================================================================
    // ws_delete
    // ==================================================================

    /// <summary>
    /// ★ 没读过的文件删不掉，并且断言「文件确实还在」——
    /// 守的不是「返回了错误」，而是「物理上没有动过」。
    /// </summary>
    [Fact]
    public async Task WsDelete_RequiresTicket_FileSurvives()
    {
        Write("keep.txt", "重要内容");

        var denied = await Call("ws_delete", "{\"path\":\"keep.txt\"}");

        Assert.False(denied.Ok);
        Assert.Contains("读凭证", denied.Error);
        Assert.True(File.Exists(Path.Combine(_root, "keep.txt")));
        Assert.Equal("重要内容", File.ReadAllText(Path.Combine(_root, "keep.txt")));
    }

    [Fact]
    public async Task WsDelete_File_IsRemoved_AndBackedUp()
    {
        Write("doomed.txt", "要删的内容");
        Data(await Call("ws_read", "{\"path\":\"doomed.txt\"}"));

        var data = Data(await Call("ws_delete", "{\"path\":\"doomed.txt\"}"));

        Assert.False(File.Exists(Path.Combine(_root, "doomed.txt")));
        Assert.Equal("file", data.GetProperty("kind").GetString());

        // 最后一道后悔药：删掉的根一级文件必须能在 _backup/ 里找回来
        var backup = data.GetProperty("backup").GetString();
        Assert.NotNull(backup);
        Assert.Equal("要删的内容", File.ReadAllText(Path.Combine(_root, backup!)));
    }

    /// <summary>
    /// 空目录：不需要凭证也能删。
    ///
    /// 这是对 Python 版的一处有意修正：那边对目录也调 _check_ticket，
    /// 但没有任何工具能为目录签发凭证（ws_read 读到目录直接报错），
    /// 于是「删除空目录」这条路在 Python 版里实际永远走不通。
    /// </summary>
    [Fact]
    public async Task WsDelete_EmptyDirectory_NeedsNoTicket()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        var data = Data(await Call("ws_delete", "{\"path\":\"empty\"}"));

        Assert.Equal("dir", data.GetProperty("kind").GetString());
        Assert.False(Directory.Exists(Path.Combine(_root, "empty")));
    }

    /// <summary>
    /// ★ 非空目录必须拒绝 —— 本工具永不递归删除。
    /// 递归删整棵树是唯一「一次点击毁掉一整天」的操作，不给这个开关。
    /// </summary>
    [Fact]
    public async Task WsDelete_NonEmptyDirectory_IsRefused()
    {
        Write("tree/inner.txt", "x");

        var result = await Call("ws_delete", "{\"path\":\"tree\"}");

        Assert.False(result.Ok);
        Assert.Contains("空目录", result.Error);
        Assert.True(File.Exists(Path.Combine(_root, "tree", "inner.txt")));
    }

    /// <summary>
    /// .git / _backup / _ssh_setup 是「删掉就回不来」的三个根一级目录：版本历史、
    /// 回滚保险、私钥。锁死它们比事后解释便宜得多。
    /// </summary>
    [Fact]
    public async Task WsDelete_ProtectedDirectory_IsRefused()
    {
        foreach (var name in new[] { ".git", "_backup", "_ssh_setup" })
        {
            Directory.CreateDirectory(Path.Combine(_root, name));

            var result = await Call("ws_delete", $"{{\"path\":\"{name}\"}}");

            Assert.False(result.Ok);
            Assert.Contains("受保护", result.Error);
            Assert.True(Directory.Exists(Path.Combine(_root, name)));
        }
    }

    [Fact]
    public async Task WsDelete_RootItself_IsRefused()
    {
        var result = await Call("ws_delete", "{\"path\":\".\"}");

        Assert.False(result.Ok);
        Assert.Contains("根目录", result.Error);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task WsDelete_Missing_ReportsNotFound()
    {
        var result = await Call("ws_delete", "{\"path\":\"ghost.txt\"}");

        Assert.False(result.Ok);
        Assert.Contains("不存在", result.Error);
    }

    [Fact]
    public async Task WsDelete_PathEscape_IsRejected()
    {
        var result = await Call("ws_delete", "{\"path\":\"../outside.txt\"}");

        Assert.False(result.Ok);
        Assert.Contains("越界", result.Error);
    }

    // ==================================================================
    // ProcessEncoding 单元测试
    // ==================================================================

    [Fact]
    public void ProcessEncoding_DecodesGbk()
    {
        // GBK 的「工作台」—— 简中 Windows 上 cmd 内建命令就是这种字节
        var gbk = ProcessEncoding.Oem;
        var bytes = gbk.GetBytes("工作台 中文");

        Assert.Equal("工作台 中文", ProcessEncoding.Decode(bytes));
        Assert.Equal("工作台 中文", ProcessEncoding.Decode(bytes, "gbk"));
    }

    [Fact]
    public void ProcessEncoding_PrefersUtf8WhenValid()
    {
        var bytes = new UTF8Encoding(false).GetBytes("肥鱼 token");

        Assert.Equal("肥鱼 token", ProcessEncoding.Decode(bytes));
        Assert.Equal("肥鱼 token", ProcessEncoding.Decode(bytes, "utf-8"));
    }

    [Fact]
    public void ProcessEncoding_DetectsBinary()
    {
        Assert.True(ProcessEncoding.LooksBinary(new byte[] { 0x4D, 0x5A, 0x00 }));
        Assert.False(ProcessEncoding.LooksBinary(new UTF8Encoding(false).GetBytes("纯文本")));
    }
}
