using System.Diagnostics;
using System.Text.Json;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// ws_run_python 的行为测试。
///
/// 这批测试守的几件事，坏掉之后都不会立刻报错，而是「结果看起来对、其实不对」：
///   · 中文输出必须能原样回来（错了就是一屏乱码，模型据此得出错误结论）；
///   · 非零退出码不能吞掉 stdout / traceback（那是排查 Python 报错唯一的信息源）；
///   · 临时脚本无论成功失败都要删干净（否则 .fatfish_tmp 会越积越多）；
///   · 超时必须真的杀掉进程（否则一次 sleep(3600) 就把整个回合挂死）；
///   · filename 参数不能变成写入工作台之外的任意路径。
///
/// ★ 依赖本机 Python。找不到解释器时，依赖解释器的用例会**静默跳过**。
///   这是个取舍：CI 机器没装 Python 时不该红，但代价是「跳过」与「通过」看起来一样。
///   <see cref="Policy_RequiresApproval_And_ForbidsOneKey"/> 不依赖 Python，始终会跑。
/// </summary>
public class WsRunPythonToolTests : IDisposable
{
    private readonly string _root;
    private readonly PathGuard _guard;
    private readonly ToolContext _context;
    private readonly ToolRegistry _registry;
    private readonly bool _pythonMissing;

    public WsRunPythonToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_py_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        _guard = new PathGuard(_root);

        _context = new ToolContext
        {
            Guard = _guard,
            Tickets = new ReadTicketStore(_guard),
            Approval = new ApprovalPolicy(_guard),
        };

        _registry = new ToolRegistry(new ITool[] { new WsRunPythonTool() });

        _pythonMissing = PythonLocator.Resolve(_root).Interpreter is null;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 被占用就算了，临时目录不影响结论
        }

        GC.SuppressFinalize(this);
    }

    // ---------- 辅助 ----------

    private Task<ToolResult> Call(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return _registry.InvokeAsync("ws_run_python", _context, doc.RootElement.Clone()).AsTask();
    }

    private static JsonElement Data(ToolResult result)
    {
        Assert.True(result.Ok, "工具应当成功，实际失败：" + result.Error);
        Assert.NotNull(result.Data);
        return result.Data!.Value;
    }

    private static string Stdout(ToolResult result) => Data(result).GetProperty("stdout").GetString()!;

    private static string Stderr(ToolResult result) => Data(result).GetProperty("stderr").GetString()!;

    // ==================================================================
    // 执行
    // ==================================================================

    [Fact]
    public async Task RunsCode_AndReturnsStdout()
    {
        if (_pythonMissing)
            return;

        var d = Data(await Call("""{"code":"print('hello from python')"}"""));

        Assert.Equal("hello from python", d.GetProperty("stdout").GetString());
        Assert.Equal(0, d.GetProperty("exit_code").GetInt32());
        Assert.False(d.GetProperty("timed_out").GetBoolean());
    }

    /// <summary>
    /// 中文必须原样回来。
    ///
    /// ★ 这条测试的价值不在于「会不会乱码」，而在于它同时验证了
    ///   子进程被强制成 UTF-8 输出（PYTHONIOENCODING）与解码路径的配合。
    ///   少了任何一半，中文在多字节边界上就可能被拆坏。
    /// </summary>
    [Fact]
    public async Task CapturesChineseOutput_WithoutMojibake()
    {
        if (_pythonMissing)
            return;

        var d = Data(await Call("""{"code":"print('中文输出测试 abc 123')"}"""));
        var text = d.GetProperty("stdout").GetString();

        Assert.Equal("中文输出测试 abc 123", text);
    }

    [Fact]
    public async Task ScriptRunsInRequestedCwd()
    {
        if (_pythonMissing)
            return;

        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var d = Data(await Call("""{"code":"import os\nprint(os.getcwd())","cwd":"sub"}"""));

        Assert.Contains("sub", d.GetProperty("stdout").GetString()!);
        Assert.Equal("sub", d.GetProperty("cwd").GetString());
    }

    /// <summary>
    /// 子进程的 stdio 编码被强制为 UTF-8。
    /// 这条断言的是「我们主动设定的约定」，不是 Python 的默认行为。
    /// </summary>
    [Fact]
    public async Task ChildProcessUsesUtf8Stdio()
    {
        if (_pythonMissing)
            return;

        var d = Data(await Call(
            """{"code":"import os\nprint(os.environ.get('PYTHONIOENCODING', ''))"}"""));

        Assert.Equal("utf-8", d.GetProperty("stdout").GetString());
    }

    // ==================================================================
    // 失败路径
    // ==================================================================

    /// <summary>
    /// 非零退出码仍然算「工具调用成功」，但 stdout / stderr 必须完整带回。
    ///
    /// 这是与 Python 版唯一一处语义偏离（那边返回 ok=False）。
    /// 理由写在 WsRunPythonTool 的类注释里：返回 Failure 会让 AgentLoop 只看到错误文本，
    /// traceback 与 stdout 全丢，而排查 Python 报错恰恰全靠这两样。
    /// </summary>
    [Fact]
    public async Task NonZeroExit_KeepsStdoutAndStderr()
    {
        if (_pythonMissing)
            return;

        var result = await Call(
            """{"code":"import sys\nprint('before')\nprint('boom', file=sys.stderr)\nsys.exit(3)"}""");

        var d = Data(result);

        Assert.Equal(3, d.GetProperty("exit_code").GetInt32());
        Assert.Equal("before", d.GetProperty("stdout").GetString());
        Assert.Contains("boom", d.GetProperty("stderr").GetString()!);

        // 必须有人告诉模型「traceback 里的路径已经不存在了」，
        // 否则它会试图去读那个文件，白烧一轮。
        Assert.NotNull(result.Notice);
        Assert.Contains("退出码 3", result.Notice!);
    }

    [Fact]
    public async Task RealException_ProducesTracebackInStderr()
    {
        if (_pythonMissing)
            return;

        var result = await Call("""{"code":"raise ValueError('故意炸一个')"}""");

        var d = Data(result);

        Assert.NotEqual(0, d.GetProperty("exit_code").GetInt32());
        Assert.Contains("ValueError", Stderr(result));
        Assert.Contains("故意炸一个", Stderr(result));
    }

    // ==================================================================
    // 超时
    // ==================================================================

    /// <summary>
    /// 超时必须真的杀掉进程，而不是「等它跑完再报告超时」。
    /// 断言耗时远小于 sleep 的长度，才说明是「被杀掉」而不是「跑完了」。
    /// </summary>
    [Fact]
    public async Task TimedOut_ProcessIsKilled()
    {
        if (_pythonMissing)
            return;

        var sw = Stopwatch.StartNew();

        var result = await Call(
            """{"code":"import time\ntime.sleep(30)","timeout_seconds":1}""");

        sw.Stop();

        var d = Data(result);

        Assert.True(d.GetProperty("timed_out").GetBoolean());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"超时后应当在秒级返回，实际用了 {sw.Elapsed.TotalSeconds:0.0}s");

        Assert.NotNull(result.Notice);
        Assert.Contains("强制终止", result.Notice!);
    }

    // ==================================================================
    // 临时脚本
    // ==================================================================

    [Fact]
    public async Task TemporaryScript_IsDeletedAfterRun()
    {
        if (_pythonMissing)
            return;

        var d = Data(await Call("""{"code":"print(1)","filename":"probe.py"}"""));

        Assert.Equal(".fatfish_tmp/probe.py", d.GetProperty("script").GetString());

        var full = Path.Combine(_root, ".fatfish_tmp", "probe.py");
        Assert.False(File.Exists(full), "跑完临时脚本必须被删除");
    }

    /// <summary>
    /// filename 里带目录穿越时，只取最后一段文件名 —— 绝不跟着穿越出去。
    /// 断言落点是 .fatfish_tmp/evil.py，而不是工作台的上一级。
    /// </summary>
    [Fact]
    public async Task FilenameWithTraversal_IsReducedToLeafName()
    {
        if (_pythonMissing)
            return;

        var d = Data(await Call("""{"code":"print(1)","filename":"../../evil.py"}"""));

        Assert.Equal(".fatfish_tmp/evil.py", d.GetProperty("script").GetString());
    }

    [Fact]
    public async Task FilenameUsesWindowsReservedName_IsRejected()
    {
        if (_pythonMissing)
            return;

        var result = await Call("""{"code":"print(1)","filename":"CON.py"}""");

        Assert.False(result.Ok);
        Assert.Contains("filename 不合法", result.Error);
    }

    [Fact]
    public async Task FilenameWithoutPyExtension_GetsIt()
    {
        if (_pythonMissing)
            return;

        var d = Data(await Call("""{"code":"print(1)","filename":"explore_prices"}"""));

        Assert.Equal(".fatfish_tmp/explore_prices.py", d.GetProperty("script").GetString());
    }

    // ==================================================================
    // 参数校验
    // ==================================================================

    [Fact]
    public async Task EmptyCode_IsRejected()
    {
        var result = await Call("""{"code":"   \n  "}""");

        Assert.False(result.Ok);
        Assert.Contains("code", result.Error);
    }

    [Fact]
    public async Task MissingCwd_IsRejected()
    {
        var result = await Call("""{"code":"print(1)","cwd":"nope"}""");

        Assert.False(result.Ok);
        Assert.Contains("不存在", result.Error);
    }

    [Fact]
    public async Task EscapeCwd_IsRejected()
    {
        var result = await Call("""{"code":"print(1)","cwd":"../.."}""");

        Assert.False(result.Ok);
        Assert.Contains("越界", result.Error);
    }

    // ==================================================================
    // 安全策略
    // ==================================================================

    /// <summary>
    /// 这个工具能做的事和 ws_run_cmd 一样多，所以两道闸门都必须生效：
    /// 一律报批，且不允许「本会话一键放行」。
    /// 不依赖 Python，因此始终会跑。
    /// </summary>
    [Fact]
    public void Policy_RequiresApproval_And_ForbidsOneKey()
    {
        var policy = new ApprovalPolicy(_guard);

        Assert.True(policy.NeedsApproval("ws_run_python").Need,
            "执行任意代码必须人工审批");

        Assert.True(policy.NeverAutoApproves("ws_run_python"),
            "执行任意代码不得一键放行");
    }
}
