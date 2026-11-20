using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 读凭证的「已读区间 / 覆盖率」扩展（2026-11-05，P1）。
/// </summary>
/// <remarks>
/// ★ 为什么要有这批测试：
///   ws_read 开始支持 offset/limit 分页之后，「读过了」不再是一个布尔值 ——
///   只读第 1-100 行和通读 1200 行拿到的是同一张凭证，而全量覆盖会把没看过的一千多行抹掉。
///   这批用例钉住的是「区间合并」「完整性判定」「失效」三件事。
/// ★ 最要紧的一条是 <see cref="SkippingMiddle_StaysIncomplete"/>：
///   跳读看起来"读了不少行"，但中间有洞，绝不能算完整 ——
///   这种错误不会报错，只会静默地允许一次丢掉中间内容的全量覆盖。
/// </remarks>
public sealed class ReadTicketCoverageTests : IDisposable
{
    private readonly string _root;
    private readonly PathGuard _guard;
    private readonly ReadTicketStore _tickets;

    public ReadTicketCoverageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_rtc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _guard = new PathGuard(_root);
        _tickets = new ReadTicketStore(_guard);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteFile(string rel, string content)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void WholeFileIssue_IsComplete()
    {
        var full = WriteFile("a.txt", "x");

        var ticket = _tickets.Issue(full);

        Assert.True(ticket.Complete);
        Assert.True(_tickets.CheckForFullRewrite(full, out _));
    }

    [Fact]
    public void PartialIssue_IsIncomplete_AndReportsNumbers()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full, totalLines: 1200, fromLine: 1, toLine: 320);

        var coverage = _tickets.Coverage(full);

        Assert.NotNull(coverage);
        Assert.False(coverage!.Complete);
        Assert.Equal(320, coverage.ReadLines);
        Assert.Equal(1200, coverage.TotalLines);

        // 全量覆盖必须被拒，而且错误里要能读出「差多少」
        Assert.False(_tickets.CheckForFullRewrite(full, out var error));
        Assert.Contains("320/1200", error);
    }

    [Fact]
    public void PagingAll_BecomesComplete()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full, 1200, 1, 400);
        _tickets.Issue(full, 1200, 401, 800);
        _tickets.Issue(full, 1200, 801, 1200);

        Assert.True(_tickets.Coverage(full)!.Complete);
        Assert.True(_tickets.CheckForFullRewrite(full, out _));
    }

    [Fact]
    public void SkippingMiddle_StaysIncomplete()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full, 1200, 1, 100);
        _tickets.Issue(full, 1200, 200, 300);   // 101-199 没读

        var coverage = _tickets.Coverage(full)!;

        Assert.False(coverage.Complete);
        Assert.Equal(201, coverage.ReadLines);   // 100 + 101
    }

    [Fact]
    public void AdjacentRanges_MergeIntoOne()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full, 100, 1, 50);
        _tickets.Issue(full, 100, 51, 100);

        var ticket = _tickets.Coverage(full)!;
        Assert.True(ticket.Complete);
    }

    [Fact]
    public void OverlappingRanges_DoNotDoubleCount()
    {
        var full = WriteFile("a.txt", "x");

        _tickets.Issue(full, 100, 1, 60);
        _tickets.Issue(full, 100, 40, 100);

        var coverage = _tickets.Coverage(full)!;

        Assert.True(coverage.Complete);
        Assert.Equal(100, coverage.ReadLines);
    }

    [Fact]
    public void EmptyFile_IsComplete()
    {
        var full = WriteFile("empty.txt", string.Empty);

        _tickets.Issue(full, totalLines: 0, fromLine: 1, toLine: 0);

        Assert.True(_tickets.Coverage(full)!.Complete);
        Assert.True(_tickets.CheckForFullRewrite(full, out _));
    }

    [Fact]
    public void ExternalChange_InvalidatesAndBlocksFullRewrite()
    {
        var full = WriteFile("a.txt", "one");

        _tickets.Issue(full, 100, 1, 100);
        Assert.True(_tickets.CheckForFullRewrite(full, out _));

        // 外部改动 → 指纹变 → 旧区间作废
        WriteFile("a.txt", "two");

        Assert.False(_tickets.CheckForFullRewrite(full, out var error));
        Assert.Contains("已被外部改动", error);
    }

    [Fact]
    public void NoTicket_FullRewriteCheckFails()
    {
        var full = WriteFile("a.txt", "x");

        Assert.False(_tickets.CheckForFullRewrite(full, out var error));
        Assert.Contains("读凭证", error);
    }

    [Fact]
    public void ReReadingAfterExternalChange_RestartsCoverage()
    {
        var full = WriteFile("a.txt", "one");

        _tickets.Issue(full, 100, 1, 100);
        Assert.True(_tickets.Coverage(full)!.Complete);

        WriteFile("a.txt", "two");          // 内容变了

        _tickets.Issue(full, 100, 1, 10);   // 新版本只读了 10 行

        var coverage = _tickets.Coverage(full)!;

        Assert.False(coverage.Complete);    // 旧版本的 100 行不能算进来
        Assert.Equal(10, coverage.ReadLines);
    }

    [Fact]
    public void Describe_IncludesCoverage()
    {
        var full = WriteFile("a.txt", "x");
        _tickets.Issue(full, 1200, 1, 320);

        var lines = _tickets.Describe();

        Assert.Single(lines);
        Assert.Contains("320/1200", lines[0]);
    }
}
