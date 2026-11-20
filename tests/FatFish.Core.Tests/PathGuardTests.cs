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

/// <summary>
/// 盘符根回归测试。
///
/// 2026-11-13 实际踩到：<c>ws_cd E:\</c> 之后 <c>PathGuard.Root</c> 变成 <c>"E:"</c>
/// （<c>TrimEnd(Sep)</c> 把盘符根削掉了），于是 <c>Resolve("")</c> 返回 <c>"E:"</c> ——
/// 这在 Windows 上是「驱动器相对路径」，不是完全限定路径，
/// 随后每个 <c>Directory.*</c> 都抛 "Basepath argument is not fully qualified"，
/// 整个工作台当场不可用（连 ws_list 都做不了）。
///
/// 所以：盘符根的尾分隔符必须保留。相对路径的尾分隔符才该去掉。
/// </summary>
public sealed class PathGuardDriveRootTests
{
    [Fact]
    public void DriveRoot_KeepsTrailingSeparator()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;   // 形如 "C:\"
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), driveRoot,
            StringComparison.Ordinal);

        var guard = new PathGuard(driveRoot);

        Assert.Equal(driveRoot, guard.Root);
        Assert.Equal(driveRoot, guard.Resolve(""));
        Assert.Equal(driveRoot, guard.Resolve(null));
    }

    [Fact]
    public void DriveRoot_StillTrapsEscape()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var guard = new PathGuard(driveRoot);

        // 盘符根之下什么都在「里面」，但绝对路径本身仍要被拒绝
        Assert.Throws<PathEscapeException>(() => guard.Resolve(@"\\server\share\evil.txt"));
    }

    [Fact]
    public void NonRootPath_HasNoTrailingSeparator()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fatfish_dr_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var guard = new PathGuard(dir + Path.DirectorySeparatorChar);
            Assert.Equal(dir.TrimEnd(Path.DirectorySeparatorChar), guard.Root);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
