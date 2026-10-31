using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// PathGuard 的测试。
///
/// 这些用例直接对应 Python 版 workspace.py 的行为，迁移后必须逐条通过，
/// 否则说明安全边界发生了漂移。
/// </summary>
public sealed class PathGuardTests : IDisposable
{
    private readonly string _root;
    private readonly PathGuard _guard;

    public PathGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_pg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));
        _guard = new PathGuard(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理失败不影响结论 */ }
    }

    [Fact]
    public void Resolve_EmptyPath_ReturnsRoot()
    {
        Assert.Equal(_root, _guard.Resolve(""));
        Assert.Equal(_root, _guard.Resolve(null));
    }

    [Fact]
    public void Resolve_SimpleRelative_Works()
    {
        var full = _guard.Resolve("workspace/a.txt");
        Assert.Equal(Path.Combine(_root, "workspace", "a.txt"), full, ignoreCase: true);
    }

    [Fact]
    public void Resolve_ForwardSlash_Normalized()
    {
        var full = _guard.Resolve("workspace/sub/b.txt");
        Assert.StartsWith(_root, full, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sub", full);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil.txt")]
    [InlineData("workspace/../../evil.txt")]
    [InlineData("..\\..\\Windows\\system32\\cmd.exe")]
    public void Resolve_EscapeAttempt_Throws(string input)
    {
        Assert.Throws<PathEscapeException>(() => _guard.Resolve(input));
    }

    [Theory]
    [InlineData(@"C:\Windows\system32")]
    [InlineData(@"D:\anything")]
    [InlineData(@"\\server\share")]
    public void Resolve_AbsolutePath_Throws(string input)
    {
        Assert.Throws<PathEscapeException>(() => _guard.Resolve(input));
    }

    [Fact]
    public void Resolve_PrefixLookalike_NotConsideredInside()
    {
        // 关键回归：'C:\root2' 不能被当成在 'C:\root' 内部
        var sibling = _root + "2";
        Assert.False(_guard.IsInside(Path.Combine(sibling, "x.txt")));
    }

    [Theory]
    [InlineData(".env", true)]
    [InlineData(".env.local", true)]
    [InlineData("prod.env", true)]
    [InlineData("server.key", true)]
    [InlineData("cert.pem", true)]
    [InlineData("id_rsa", true)]
    [InlineData("normal.txt", false)]
    [InlineData("src/main.cs", false)]
    [InlineData("environment.md", false)]
    public void CheckSensitive_MatchesExpected(string path, bool expected)
    {
        var verdict = _guard.CheckSensitive(path);
        Assert.Equal(expected, verdict.IsSensitive);
    }

    [Theory]
    [InlineData("_backup/.env", true)]
    [InlineData("_ssh_setup/id_ed25519", true)]
    [InlineData(".git/config", true)]
    [InlineData("workspace/.gitignore", false)]
    public void CheckSensitive_DirectoryPrefixes(string path, bool expected)
    {
        Assert.Equal(expected, _guard.CheckSensitive(path).IsSensitive);
    }

    [Theory]
    [InlineData("notes.txt", true)]        // 根一级：无分隔符
    [InlineData("sub/notes.txt", false)]   // 有子目录，不是根一级
    [InlineData("/notes.txt", true)]       // 前导 '/' 被 Trim，仍是根一级
    [InlineData("a/b/c.txt", false)]
    [InlineData("", false)]                // 空路径不算文件
    public void IsRootLevelFile_DetectsNesting(string path, bool expectedIsRootLevel)
    {
        // 语义：返回 true 表示「是根一级文件」（覆盖前要备份的那一类）
        Assert.Equal(expectedIsRootLevel, _guard.IsRootLevelFile(path));
    }

    [Theory]
    [InlineData("CON", "保留设备名")]
    [InlineData("nul.txt", "保留设备名")]
    [InlineData("bad|name.txt", "非法字符")]
    [InlineData("trailing.", "点或空格结尾")]
    public void ValidateFileName_RejectsInvalid(string name, string expectedFragment)
    {
        var reason = PathGuard.ValidateFileName(name);
        Assert.NotNull(reason);
        Assert.Contains(expectedFragment, reason);
    }

    [Theory]
    [InlineData("normal.txt")]
    [InlineData("报告_v2.md")]
    public void ValidateFileName_AcceptsValid(string name)
    {
        Assert.Null(PathGuard.ValidateFileName(name));
    }

    [Fact]
    public void ToRelative_RoundTrips()
    {
        var full = _guard.Resolve("workspace/deep/file.txt");
        Assert.Equal("workspace/deep/file.txt", _guard.ToRelative(full));
    }
}
