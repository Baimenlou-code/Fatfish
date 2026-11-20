using FatFish.Core.Safety;
using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// ws_replace 的「改动必须落在已读区间」判据（2026-11-05，P3）。
/// </summary>
/// <remarks>
/// ★ 这条判据是**最小必要**的：只看「这次实际会被改到的行」，
///   不要求通读全文。读全文时恒通过，所以老路径无感。
/// ★ 最容易错的是「偏移量 → 行号」那一步：错一个字符，
///   判据就会放行一次本该拒绝的改动，而且完全不报错 ——
///   只会安静地改到没读过的内容上。所以那一半单独抽成纯函数来测。
/// </remarks>
public sealed class WsReplaceGateTests : IDisposable
{
    private readonly string _root;
    private readonly ReadTicketStore _tickets;

    public WsReplaceGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_rg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _tickets = new ReadTicketStore(new PathGuard(_root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteFile(string rel, string content)
    {
        var full = Path.Combine(_root, rel);
        File.WriteAllText(full, content);
        return full;
    }

    // ---------------- AffectedLines：偏移量 → 行号 ----------------

    [Fact]
    public void AffectedLines_ReportsEveryOccurrenceLine()
    {
        const string content = "one\ntwo\none\n";

        var lines = WsReplaceTool.AffectedLines(content, "one", effective: 2);

        Assert.Equal(new[] { 1, 3 }, lines);
    }

    [Fact]
    public void AffectedLines_RespectsEffectiveCount()
    {
        const string content = "one\ntwo\none\n";

        var lines = WsReplaceTool.AffectedLines(content, "one", effective: 1);

        Assert.Equal(new[] { 1 }, lines);
    }

    [Fact]
    public void AffectedLines_CountsLinesAcrossCrlf()
    {
        const string content = "a\r\nb\r\nX\r\n";

        var lines = WsReplaceTool.AffectedLines(content, "X", effective: 1);

        Assert.Equal(new[] { 3 }, lines);
    }

    [Fact]
    public void AffectedLines_MultiLineOldText_ReportsEveryLineItTouches()
    {
        const string content = "l1\nl2\nl3\nl4\n";

        // old 跨两行 → 两行都要报。只报起点会让闸门少检查一行（2026-11-06 修）。
        var lines = WsReplaceTool.AffectedLines(content, "l3\nl4", effective: 1);

        Assert.Equal(new[] { 3, 4 }, lines);
    }

    [Fact]
    public void AffectedLines_MultiLineOldText_ThreeLines()
    {
        const string content = "l1\nl2\nl3\nl4\nl5\n";

        var lines = WsReplaceTool.AffectedLines(content, "l2\nl3\nl4", effective: 1);

        Assert.Equal(new[] { 2, 3, 4 }, lines);
    }

    [Fact]
    public void AffectedLines_TrailingNewline_DoesNotClaimNextLine()
    {
        const string content = "l1\nl2\nl3\n";

        // old 以 \n 结尾时，那个换行是第 3 行的行首分隔符 —— 不该把第 3 行算进来，
        // 否则每次「整行替换」都会多要一次读取。
        var lines = WsReplaceTool.AffectedLines(content, "l2\n", effective: 1);

        Assert.Equal(new[] { 2 }, lines);
    }

    [Fact]
    public void AffectedLines_OnlyNewlines_ClaimsBothJoinedLines()
    {
        const string content = "a\nb\n";

        // 把两行合并成一行：第 1、2 行都被改到
        var lines = WsReplaceTool.AffectedLines(content, "\n", effective: 1);

        Assert.Equal(new[] { 1, 2 }, lines);
    }

    [Fact]
    public void AffectedLines_TwoMatchesOnSameLine_NoDuplicates()
    {
        const string content = "aXbXc\n";

        var lines = WsReplaceTool.AffectedLines(content, "X", effective: 2);

        Assert.Equal(new[] { 1 }, lines);
    }

    [Fact]
    public void AffectedLines_CrlfMultiLine_ReportsBothLines()
    {
        const string content = "a\r\nb\r\nc\r\n";

        var lines = WsReplaceTool.AffectedLines(content, "b\r\nc", effective: 1);

        Assert.Equal(new[] { 2, 3 }, lines);
    }

    [Fact]
    public void AffectedLines_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(WsReplaceTool.AffectedLines("abc", "zzz", 1));
    }

    [Fact]
    public void AffectedLines_EmptyOldText_ReturnsEmpty()
    {
        Assert.Empty(WsReplaceTool.AffectedLines("abc", string.Empty, 1));
    }

    // ---------------- AreLinesCovered：区间判定 ----------------

    [Fact]
    public void CompleteRead_AlwaysCovered()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full);   // 整份读取

        Assert.True(_tickets.AreLinesCovered(full, new[] { 1, 500, 9999 }, out var firstUnread));
        Assert.Equal(0, firstUnread);
    }

    [Fact]
    public void PartialRead_RejectsLineOutsideRange()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full, totalLines: 100, fromLine: 1, toLine: 50);

        // 第 70 行在 1-50 之外 → 必须拒绝
        Assert.False(_tickets.AreLinesCovered(full, new[] { 70 }, out var firstUnread));
        Assert.Equal(70, firstUnread);
    }

    [Fact]
    public void PartialRead_UnreadLineIsReported()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full, totalLines: 100, fromLine: 1, toLine: 50);

        Assert.False(_tickets.AreLinesCovered(full, new[] { 10, 70 }, out var firstUnread));

        // 关键：报出的是**第一个**越界行，模型据此决定从哪儿补读
        Assert.Equal(70, firstUnread);
    }

    [Fact]
    public void PartialRead_AcceptsLinesInsideRange()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full, totalLines: 100, fromLine: 40, toLine: 60);

        Assert.True(_tickets.AreLinesCovered(full, new[] { 40, 50, 60 }, out var firstUnread));
        Assert.Equal(0, firstUnread);
    }

    [Fact]
    public void PagedRead_CoversWhenAllPagesRead()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full, 100, 1, 50);
        _tickets.Issue(full, 100, 51, 100);

        Assert.True(_tickets.AreLinesCovered(full, new[] { 1, 75, 100 }, out _));
    }

    [Fact]
    public void NoTicket_NotCovered()
    {
        var full = WriteFile("a.txt", "x");

        Assert.False(_tickets.AreLinesCovered(full, new[] { 1 }, out var firstUnread));
        Assert.Equal(1, firstUnread);
    }

    [Fact]
    public void EmptyLineList_IsCovered()
    {
        var full = WriteFile("a.txt", "x");

        Assert.True(_tickets.AreLinesCovered(full, Array.Empty<int>(), out _));
    }
}
