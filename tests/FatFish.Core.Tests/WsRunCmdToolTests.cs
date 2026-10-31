using System.Text.Json;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// ws_run_cmd 的引号与临时脚本行为。
///
/// 这批测试几乎全部来自一次真实故障：`cmd /d /s /c "&lt;原文&gt;"` 在命令自身含双引号时
/// 会被 <c>CommandLineToArgvW</c> 按「引号成对」拆错，而**退出码仍然是 0**。
///
/// <code>
/// set ZZTEST=1 &amp; set ZZTEST          →  ZZTEST=1          ✔   （无内层引号，侥幸正确）
/// set "ZZTEST2=2" &amp; set ZZTEST2      →  环境变量未定义      ✘   （内层引号把参数拆了）
/// </code>
///
/// 所以这里第一条用例就是它 —— 不是因为这条命令有用，而是因为它是
/// 「静默少做半件事」的最小复现。后面几条分别守住修复方案的代价：
/// 临时 .bat 必须删干净、不能带回显噪音、多行命令要能用、带空格的路径不能被拆开。
/// </summary>
public class WsRunCmdToolTests : IDisposable
{
    private readonly string _root;
    private readonly PathGuard _guard;
    private readonly ToolContext _context;
    private readonly ToolRegistry _registry;

    public WsRunCmdToolTests()
    {
        // 目录名故意带空格：用来验证「路径经 ArgumentList 传递」不会被拆成两段。
        // 这也是旧实现容易出问题的地方 —— 自己拼 "/c \"" + path + "\"" 时，
        // 内层引号会和外层引号打架。
        _root = Path.Combine(Path.GetTempPath(), "fatfish cmd " + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_root);

        _guard = new PathGuard(_root);

        _context = new ToolContext
        {
            Guard = _guard,
            Tickets = new ReadTicketStore(_guard),
            Approval = new ApprovalPolicy(_guard),
        };

        _registry = new ToolRegistry(new ITool[] { new WsRunCmdTool() });
    }

    public void Dispose()
    {
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

    private Task<ToolResult> Call(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return _registry.InvokeAsync("ws_run_cmd", _context, doc.RootElement.Clone()).AsTask();
    }

    private static JsonElement Data(ToolResult result)
    {
        Assert.True(result.Ok, "工具应当成功，实际失败：" + result.Error);
        Assert.NotNull(result.Data);
        return result.Data!.Value;
    }

    private static string Stdout(ToolResult result)
        => Data(result).GetProperty("stdout").GetString()!;

    private static int ExitCode(ToolResult result)
        => Data(result).GetProperty("exit_code").GetInt32();

    // ==================================================================
    // 引号：这次的修复本身
    // ==================================================================

    /// <summary>
    /// 核心回归：命令里的双引号必须原样送达 cmd。
    /// 修复前这一条会失败（stdout 里没有 ZZ_QQ=7，退出码却还是 0）。
    /// </summary>
    [Fact]
    public async Task QuotedSetSyntax_IsParsedCorrectly()
    {
        var result = await Call("""{"command":"set \"ZZ_QQ=7\" & set ZZ_QQ"}""");

        Assert.Contains("ZZ_QQ=7", Stdout(result));
    }

    /// <summary>引号包裹的、含空格的字符串要原样保留引号。</summary>
    [Fact]
    public async Task EchoWithSpaces_KeepsQuotesIntact()
    {
        var result = await Call("""{"command":"echo \"a b c\""}""");

        Assert.Equal("\"a b c\"", Stdout(result).Trim());
    }

    /// <summary>
    /// 多行命令。旧写法把它塞进一对引号里，换行会变成命令内容的一部分；
    /// 现在它是批处理文件里的两行，语义完全等同于手写 .bat。
    ///
    /// 第二行能读到第一行设的变量，是「真的是两行」最直接的证据 ——
    /// 如果是同一行（用 &amp; 连接），cmd 会在执行前就展开 %Z9%，拿到空值。
    /// </summary>
    [Fact]
    public async Task MultilineCommand_BehavesLikeRealBatchFile()
    {
        var result = await Call(
            """{"command":"set \"Z9=肥鱼\"\necho %Z9%"}""");

        Assert.Contains("肥鱼", Stdout(result));
    }

    /// <summary>中文命令必须能写进 .bat 并被执行（OEM 编码那一档）。</summary>
    [Fact]
    public async Task ChineseCommand_SurvivesBatchEncoding()
    {
        var result = await Call("""{"command":"echo 肥鱼爱吃token"}""");

        Assert.Contains("肥鱼爱吃token", Stdout(result));
    }

    // ==================================================================
    // 临时 .bat 的副作用
    // ==================================================================

    /// <summary>
    /// 引入临时 .bat 的代价必须收干净：跑完不能在工作台里留下东西。
    /// 留一堆 cmd_*.bat，用户和文件树都会困惑。
    /// </summary>
    [Fact]
    public async Task TemporaryBatch_IsDeletedAfterRun()
    {
        await Call("""{"command":"echo hi"}""");

        var tmp = Path.Combine(_root, ".fatfish_tmp");

        if (!Directory.Exists(tmp))
            return;     // 连目录都没建，更不可能有残留

        var left = Directory.GetFiles(tmp, "cmd_*.bat");
        Assert.Empty(left);
    }

    /// <summary>
    /// 不能有命令回显。少了 <c>@echo off</c>，每条命令都会先原样打一遍到 stdout，
    /// 模型的上下文里会塞满 "E:\path&gt;echo hi" 这种噪音。
    /// </summary>
    [Fact]
    public async Task NoCommandEchoNoise()
    {
        var stdout = Stdout(await Call("""{"command":"echo only-this"}"""));

        Assert.Equal("only-this", stdout.Trim());
        Assert.DoesNotContain("@echo off", stdout);
        Assert.DoesNotContain(">echo", stdout);
    }

    // ==================================================================
    // 退出码与工作目录
    // ==================================================================

    /// <summary>批处理里的 exit /b 必须变成进程退出码。</summary>
    [Fact]
    public async Task ExitCode_IsPreserved()
    {
        var result = await Call("""{"command":"echo before & exit /b 3"}""");

        Assert.Equal(3, ExitCode(result));
        Assert.Contains("before", Stdout(result));
    }

    /// <summary>
    /// 工作台路径带空格时也要能跑 —— 这条同时验证了
    /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> 的引用是正确的。
    /// </summary>
    [Fact]
    public async Task WorkspacePathWithSpaces_StillRuns()
    {
        var result = await Call("""{"command":"cd"}""");

        Assert.Contains("fatfish cmd", Stdout(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cwd_IsHonored()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub folder"));

        var d = Data(await Call("""{"command":"cd","cwd":"sub folder"}"""));

        Assert.Equal("sub folder", d.GetProperty("cwd").GetString());
    }

    // ==================================================================
    // powershell 分支：不应被这次改动影响
    // ==================================================================

    [Fact]
    public async Task PowerShell_StillWorks()
    {
        var result = await Call(
            """{"command":"Write-Output 'ps ok'","shell":"powershell"}""");

        Assert.Contains("ps ok", Stdout(result));
    }

    /// <summary>未知 shell 要报错，而不是悄悄按 cmd 跑。</summary>
    [Fact]
    public async Task UnknownShell_IsRejected()
    {
        var result = await Call("""{"command":"echo hi","shell":"bash"}""");

        Assert.False(result.Ok);
        Assert.Contains("shell", result.Error);
    }

    // ==================================================================
    // 安全策略
    // ==================================================================

    [Fact]
    public void Policy_RequiresApproval_And_ForbidsOneKey()
    {
        var policy = new ApprovalPolicy(_guard);

        Assert.True(policy.NeedsApproval("ws_run_cmd").Need);
        Assert.True(policy.NeverAutoApproves("ws_run_cmd"));
    }
}
