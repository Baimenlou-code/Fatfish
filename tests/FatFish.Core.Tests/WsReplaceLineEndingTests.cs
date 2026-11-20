using System.Text.Json;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// ws_replace 的换行符对齐（2026-11-06）。
/// </summary>
/// <remarks>
/// ★ 这是实测撞出来的真 bug：Windows 仓库里 .cs / .xaml 几乎都是 CRLF，
///   而模型习惯给 LF 的 old —— 精确匹配恒为 0 命中，报错只说「请确认逐字符一致」，
///   于是现象是「这个文件改不动，而且说不出为什么」。
/// ★ 一半用例钉纯函数（行尾归一），另一半钉**工具级接缝** ——
///   归一逻辑写对了但忘了在 ExecuteAsync 里调用，是完全一样的失败，而且不报错。
/// </remarks>
public sealed class WsReplaceLineEndingTests : IDisposable
{
    private readonly string _root;
    private readonly ToolContext _ctx;

    public WsReplaceLineEndingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_crlf_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        var guard = new PathGuard(_root);

        _ctx = new ToolContext
        {
            Guard = guard,
            Tickets = new ReadTicketStore(guard),
            Approval = new ApprovalPolicy(guard),
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<ToolResult> Run(string rel, string oldText, string newText)
    {
        var args = JsonSerializer.SerializeToElement(new { path = rel, old = oldText, @new = newText });
        return await new WsReplaceTool().ExecuteAsync(_ctx, args, CancellationToken.None);
    }

    private static int CountLoneLf(string text)
    {
        var lone = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r'))
                lone++;
        }

        return lone;
    }

    // ---------------- 风格判定 ----------------

    [Fact]
    public void UsesCrLf_WindowsFile()
    {
        Assert.True(WsReplaceTool.UsesCrLf("a\r\nb\r\nc\r\n"));
    }

    [Fact]
    public void UsesCrLf_UnixFile()
    {
        Assert.False(WsReplaceTool.UsesCrLf("a\nb\nc\n"));
    }

    // ---------------- 纯函数：行尾归一 ----------------

    [Fact]
    public void Align_LfOldOnCrlfFile_BecomesCrlf()
    {
        WsReplaceTool.AlignLineEndings(
            "one\r\ntwo\r\n", "one\ntwo", "one\nNEW", out var oldText, out var newText);

        Assert.Equal("one\r\ntwo", oldText);
        Assert.Equal("one\r\nNEW", newText);
    }

    [Fact]
    public void Align_CrlfOldOnLfFile_BecomesLf()
    {
        WsReplaceTool.AlignLineEndings(
            "one\ntwo\n", "one\r\ntwo", "one\r\nNEW", out var oldText, out var newText);

        Assert.Equal("one\ntwo", oldText);
        Assert.Equal("one\nNEW", newText);
    }

    [Fact]
    public void Align_SingleLineOld_IsLeftAlone()
    {
        WsReplaceTool.AlignLineEndings(
            "one\r\ntwo\r\n", "one", "ONE", out var oldText, out var newText);

        Assert.Equal("one", oldText);
        Assert.Equal("ONE", newText);
    }

    // ---------------- 工具级：这才是接缝 ----------------

    [Fact]
    public async Task Tool_CrlfFile_LfOld_ReplacesAndKeepsCrlf()
    {
        var full = Path.Combine(_root, "a.cs");
        File.WriteAllText(full, "line1\r\nline2\r\nline3\r\n");
        _ctx.Tickets.Issue(full);

        var result = await Run("a.cs", "line1\nline2", "line1\nCHANGED");

        Assert.True(result.Ok, "LF 的 old 应当能匹配 CRLF 文件");

        var after = File.ReadAllText(full);

        Assert.Equal("line1\r\nCHANGED\r\nline3\r\n", after);
        Assert.Equal(0, CountLoneLf(after));
    }

    [Fact]
    public async Task Tool_LfFile_CrlfOld_ReplacesAndKeepsLf()
    {
        var full = Path.Combine(_root, "b.cs");
        File.WriteAllText(full, "line1\nline2\nline3\n");
        _ctx.Tickets.Issue(full);

        var result = await Run("b.cs", "line1\r\nline2", "line1\r\nCHANGED");

        Assert.True(result.Ok, "CRLF 的 old 应当能匹配 LF 文件");

        var after = File.ReadAllText(full);

        Assert.Equal("line1\nCHANGED\nline3\n", after);
        Assert.DoesNotContain('\r', after);
    }

    [Fact]
    public async Task Tool_StillNotFound_MessageNamesCrlf()
    {
        var full = Path.Combine(_root, "c.cs");
        File.WriteAllText(full, "line1\r\nline2\r\n");
        _ctx.Tickets.Issue(full);

        var result = await Run("c.cs", "no\nsuch", "x");

        Assert.False(result.Ok);
        Assert.Contains("CRLF", result.Error ?? string.Empty);
    }
}
