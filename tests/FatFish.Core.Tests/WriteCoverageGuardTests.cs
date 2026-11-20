using FatFish.Core.Safety;
using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// ws_write 全量覆盖的「未读部分没被动过」证明（2026-11-05，P4）。
/// </summary>
/// <remarks>
/// ★ 这条闸只拦一种情形：文件**部分读过**、然后要整份覆盖。
///   新建文件、从没读过、已完整读取，三种都照旧放行 —— 那三种是既有行为。
/// ★ 证明的方向必须是**保守的**：判不出来就拒绝。
///   宁可让模型多读一次，也不能放行一次静默丢内容。
/// </remarks>
public sealed class WriteCoverageGuardTests : IDisposable
{
    private readonly string _root;
    private readonly ReadTicketStore _tickets;

    public WriteCoverageGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_wg_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ---------------- Prove：未读块还在不在 ----------------

    [Fact]
    public void NoUnreadBlocks_Proven()
    {
        var result = WriteCoverageGuard.Prove("a\nb\n", "完全不同\n", Array.Empty<LineRange>());

        Assert.True(result.Proven);
    }

    [Fact]
    public void UnreadBlockPreserved_Proven()
    {
        const string oldText = "a\nb\nc\nd\n";

        // 未读 = 第 2-3 行（"b\nc"）；新内容里原样保留
        var result = WriteCoverageGuard.Prove(
            oldText, "a\nb\nc\nX\n", new[] { new LineRange(2, 3) });

        Assert.True(result.Proven);
    }

    [Fact]
    public void UnreadBlockDropped_Rejected_AndReportsBlock()
    {
        const string oldText = "a\nb\nc\nd\n";

        var result = WriteCoverageGuard.Prove(
            oldText, "a\nX\nY\nd\n", new[] { new LineRange(2, 3) });

        Assert.False(result.Proven);
        Assert.Equal(new LineRange(2, 3), result.LostBlock);
    }

    [Fact]
    public void ChangeInsideUnreadBlock_Rejected()
    {
        const string oldText = "a\nb\nc\nd\n";

        // 只改了未读块里的一个字节，也算丢内容
        var result = WriteCoverageGuard.Prove(
            oldText, "a\nb\nZ\nd\n", new[] { new LineRange(2, 3) });

        Assert.False(result.Proven);
    }

    [Fact]
    public void TwoBlocks_ReportsTheFirstLostOne()
    {
        const string oldText = "a\nb\nc\nd\n";

        // 未读 = 第 2 行、"第 4 行"。新内容保住了 b，丢了 d
        var result = WriteCoverageGuard.Prove(
            oldText, "a\nb\nc\nX\n", new[] { new LineRange(2, 2), new LineRange(4, 4) });

        Assert.False(result.Proven);
        Assert.Equal(new LineRange(4, 4), result.LostBlock);
    }

    [Fact]
    public void CrlfOldLfNew_Rejected()
    {
        // 已知的严格性：把 CRLF 改成 LF 会改动未读块的每一个字节，属于「真的改了」。
        // 判据拒绝它是对的；如果将来要放宽，必须先想清楚怎么区分「格式归一化」和「内容删除」。
        const string oldText = "a\r\nb\r\nc\r\n";

        var result = WriteCoverageGuard.Prove(
            oldText, "a\nb\nc\n", new[] { new LineRange(2, 2) });

        Assert.False(result.Proven);
    }

    // ---------------- UnreadBlocks：已读区间的补集 ----------------

    [Fact]
    public void UnreadBlocks_IsComplementOfReadRanges()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full, totalLines: 100, fromLine: 1, toLine: 40);
        _tickets.Issue(full, totalLines: 100, fromLine: 60, toLine: 100);

        var blocks = _tickets.UnreadBlocks(full);

        Assert.Single(blocks);
        Assert.Equal(new LineRange(41, 59), blocks[0]);
    }

    [Fact]
    public void UnreadBlocks_TailIsIncluded()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full, totalLines: 100, fromLine: 1, toLine: 40);

        var blocks = _tickets.UnreadBlocks(full);

        Assert.Single(blocks);
        Assert.Equal(new LineRange(41, 100), blocks[0]);
    }

    [Fact]
    public void UnreadBlocks_CompleteRead_IsEmpty()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full);   // 整份读取

        Assert.Empty(_tickets.UnreadBlocks(full));
    }

    [Fact]
    public void UnreadBlocks_NoTicket_IsEmpty()
    {
        var full = WriteFile("a.txt", "x");

        Assert.Empty(_tickets.UnreadBlocks(full));
    }
}
