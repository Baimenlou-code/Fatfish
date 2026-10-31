using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// ReadTicketStore 的测试。
///
/// 核心不变式：
///   1. 没读过 → 无凭证；
///   2. 读过 → 有凭证；
///   3. 文件被外部改动 → 凭证失效（这是比 Python 版更严的地方）；
///   4. 明确 Forget 之后 → 无凭证。
/// </summary>
public sealed class ReadTicketTests : IDisposable
{
    private readonly string _root;
    private readonly PathGuard _guard;
    private readonly ReadTicketStore _tickets;

    public ReadTicketTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_rt_" + Guid.NewGuid().ToString("N")[..8]);
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
    public void NoTicket_CheckFails()
    {
        var full = WriteFile("a.txt", "hello");
        Assert.False(_tickets.Check(full, out var error));
        Assert.Contains("读凭证", error);
    }

    [Fact]
    public void AfterIssue_TicketValid()
    {
        var full = WriteFile("a.txt", "hello");
        _tickets.Issue(full);

        Assert.True(_tickets.Check(full, out _));
        Assert.True(_tickets.IsValid(full));
    }

    [Fact]
    public void ExternalModification_InvalidatesTicket()
    {
        var full = WriteFile("a.txt", "hello");
        _tickets.Issue(full);

        // 模拟外部进程修改文件
        File.WriteAllText(full, "hello world");

        Assert.False(_tickets.IsValid(full));
        Assert.False(_tickets.Check(full, out var error));
        Assert.Contains("已被外部改动", error);
    }

    [Fact]
    public void SameContent_KeepsTicketValid()
    {
        var full = WriteFile("a.txt", "stable");
        _tickets.Issue(full);

        // 写入相同内容：指纹不变，凭证仍有效
        File.WriteAllText(full, "stable");

        Assert.True(_tickets.IsValid(full));
    }

    [Fact]
    public void Forget_RemovesTicket()
    {
        var full = WriteFile("a.txt", "hello");
        _tickets.Issue(full);
        Assert.True(_tickets.IsValid(full));

        _tickets.Forget(full);
        Assert.False(_tickets.IsValid(full));
    }

    [Fact]
    public void NonExistentFile_TicketAllowsCreation()
    {
        var full = Path.Combine(_root, "new.txt");
        Assert.False(File.Exists(full));

        _tickets.Issue(full);

        // 文件本来就不存在 → 指纹为 null → 仍可视为有效（允许后续创建）
        Assert.True(_tickets.IsValid(full));
    }

    [Fact]
    public void CaseInsensitivePath_SharesTicket()
    {
        var full = WriteFile("Case.txt", "x");
        _tickets.Issue(full);

        var upper = full.ToUpperInvariant();
        Assert.True(_tickets.IsValid(upper));
    }

    [Fact]
    public void Issue_OutsideRoot_Throws()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside_" + Guid.NewGuid().ToString("N"));
        Assert.Throws<PathEscapeException>(() => _tickets.Issue(outside));
    }
}
