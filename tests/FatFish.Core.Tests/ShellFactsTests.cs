using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 方言预检测试 —— 移植自 Python 版 <c>system_facts.check_command</c>。
///
/// 这些断言的价值在于：审查员是**照着这份预检结果**判「命令能不能跑」的。
/// 预检误报，正确方案会被打回；预检漏报，必然失败的命令会被放行。
/// 两种错都比「没有预检」更难查 —— 因为审查员的结论看起来有理有据。
/// </summary>
public class ShellFactsTests
{
    private static readonly ShellFacts Facts = ShellFacts.Collect();

    /// <summary>cmd.exe 专用的断言：非 Windows 平台直接跳过（判据表本来就换了一套）。</summary>
    private static bool SkipIfNotWindows()
        => !Facts.IsWindows;

    // ==================================================================
    // cmd.exe 上必然失败的写法
    // ==================================================================

    [Theory]
    [InlineData("Get-ChildItem -Path .")]
    [InlineData("Remove-Item -Recurse build")]
    [InlineData("$env:PATH")]
    [InlineData("export FOO=1")]
    [InlineData("rm -rf build")]
    [InlineData("mkdir -p a\\b")]
    public void Cmd_ErrorsAreDetected(string command)
    {
        if (SkipIfNotWindows())
            return;

        var hits = Facts.CheckCommand(command);

        Assert.Contains(hits, h => h.Level == "error");
    }

    [Theory]
    [InlineData("grep -r foo .")]
    [InlineData("cat notes.txt")]
    [InlineData("which python")]
    [InlineData("ls -la")]
    [InlineData("run.ps1")]
    public void Cmd_WarningsAreDetected(string command)
    {
        if (SkipIfNotWindows())
            return;

        Assert.NotEmpty(Facts.CheckCommand(command));
    }

    // ==================================================================
    // 正确写法不该被误伤
    // ==================================================================

    [Theory]
    [InlineData("dir /b")]
    [InlineData("type notes.txt")]
    [InlineData("copy a.txt b.txt")]
    [InlineData("set X=1")]
    [InlineData("echo %PATH%")]
    [InlineData("findstr /c:\"foo\" *.txt")]
    [InlineData("where python")]
    // curl 在 Windows 10 1803+ 是系统自带的，写 curl 是**对的**
    [InlineData("curl https://example.com")]
    public void Cmd_CorrectFormsAreNotFlagged(string command)
    {
        if (SkipIfNotWindows())
            return;

        Assert.Empty(Facts.CheckCommand(command));
    }

    /// <summary>
    /// ★ 显式调用别的 shell 时必须豁免预检。
    ///
    /// 踩过的坑：预检曾把 <c>powershell -NoProfile -Command "Get-ChildItem"</c>
    /// 判为「必然失败」—— 可那正是推荐写法。自相矛盾的判据比没有判据更糟：
    /// 审查员会因此把正确方案打回，执行者按「要求」改完仍然被打回，死循环。
    /// </summary>
    [Theory]
    [InlineData("powershell -NoProfile -Command \"Get-ChildItem\"")]
    [InlineData("powershell -ExecutionPolicy Bypass -File build.ps1")]
    [InlineData("bash -c \"grep -r foo .\"")]
    [InlineData("wsl ls -la")]
    public void ForeignShell_IsExempt(string command)
    {
        if (SkipIfNotWindows())
            return;

        Assert.Empty(Facts.CheckCommand(command));
    }

    /// <summary>
    /// Python 内联代码里的单引号是合法的（那是 Python 的字符串），
    /// 不该被当成「cmd 的单引号陷阱」误报。
    /// </summary>
    [Fact]
    public void PythonInline_SingleQuoteWarningIsSuppressed()
    {
        if (SkipIfNotWindows())
            return;

        var hits = Facts.CheckCommand("python -c 'print(1)'");

        Assert.DoesNotContain(hits, h => h.Message.Contains("单引号", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyCommand_ReturnsNothing()
    {
        Assert.Empty(Facts.CheckCommand(""));
        Assert.Empty(Facts.CheckCommand("   "));
        Assert.Empty(Facts.CheckCommand(null));
    }

    [Fact]
    public void DuplicateMessages_AreDeduplicated()
    {
        var hits = Facts.CheckCommand("grep a; grep b");

        Assert.Equal(hits.Count, hits.Select(h => h.Message).Distinct().Count());
    }

    // ==================================================================
    // 送审材料渲染
    // ==================================================================

    [Fact]
    public void RenderFactsBlock_StatesShellExplicitly()
    {
        var text = Facts.RenderFactsBlock();

        Assert.Contains("运行环境事实", text);
        Assert.Contains(Facts.Shell, text);
    }

    [Fact]
    public void RenderPrecheck_CleanCommand_SaysNoIssue()
    {
        if (SkipIfNotWindows())
            return;

        var text = Facts.RenderPrecheckBlock(new[] { ("ws_run_cmd", "dir /b") });

        Assert.Contains("未发现", text);
        Assert.Contains("不代表业务逻辑正确", text);
    }

    [Fact]
    public void RenderPrecheck_TrappedCommand_ListsFix()
    {
        if (SkipIfNotWindows())
            return;

        var text = Facts.RenderPrecheckBlock(new[] { ("ws_run_cmd", "Get-ChildItem") });

        Assert.Contains("必然失败", text);
        Assert.Contains("建议", text);
    }

    [Fact]
    public void RenderPrecheck_IgnoresEmptyCommands()
    {
        var text = Facts.RenderPrecheckBlock(new[] { ("ws_run_cmd", "  ") });

        Assert.Contains("未发现", text);
    }

    [Fact]
    public void LevelText_IsHumanReadable()
    {
        Assert.Equal("必然失败", new ShellTrap("error", "m", "f").LevelText);
        Assert.Equal("多半失败", new ShellTrap("warn", "m", "f").LevelText);
        Assert.Equal("提示", new ShellTrap("info", "m", "f").LevelText);
    }
}
