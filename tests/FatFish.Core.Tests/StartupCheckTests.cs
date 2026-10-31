using FatFish.Core.Diagnostics;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 启动自检。
///
/// ★ 这组测试刻意**不去碰 API Key / 环境变量**：
///   那些是进程级全局状态，改了会影响并行跑的其它测试，
///   而一个「因为我设了 FAKE_KEY 所以别的测试红了」的问题要花很久才看得懂。
///   所以这里只测纯本地的几条：工作台、可写目录、输出格式。
/// </summary>
public class StartupCheckTests
{
    [Fact]
    public void MissingWorkspaceIsAFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "fatfish_does_not_exist_" + Guid.NewGuid().ToString("N"));

        var report = StartupCheck.Run(root);

        var item = report["工作台"];

        Assert.NotNull(item);
        Assert.Equal(CheckStatus.Fail, item!.Status);
        Assert.True(report.HasFailures);

        // 失败必须带可操作的建议，不能只说「失败」
        Assert.False(string.IsNullOrWhiteSpace(item.Hint));
    }

    [Fact]
    public void WritableDirsAreProbedNotAssumed()
    {
        var root = TempDir();

        try
        {
            var report = StartupCheck.Run(root);

            var item = report["可写目录"];

            Assert.NotNull(item);
            Assert.Equal(CheckStatus.Ok, item!.Status);

            // 探针文件必须被清掉，不能在用户的目录里留垃圾
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "logs", "sessions"), ".write_probe_*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReportTextHidesSkipByDefault()
    {
        var report = new StartupReport(
        [
            new CheckItem("A", CheckStatus.Ok, "fine"),
            new CheckItem("B", CheckStatus.Skip, "n/a"),
            new CheckItem("C", CheckStatus.Warn, "meh", "do something"),
        ]);

        var text = report.ToText();

        Assert.Contains("OK   A", text);
        Assert.DoesNotContain("B", text);
        Assert.Contains("WARN C", text);
        Assert.Contains("→ do something", text);
    }

    [Fact]
    public void ReportTextCanIncludeSkip()
    {
        var report = new StartupReport([new CheckItem("B", CheckStatus.Skip, "n/a")]);

        Assert.Contains("SKIP B", report.ToText(includeSkip: true));
    }

    [Fact]
    public void ReportFlagsAggregateCorrectly()
    {
        var clean = new StartupReport([new CheckItem("A", CheckStatus.Ok, "x")]);
        Assert.False(clean.HasFailures);
        Assert.False(clean.HasWarnings);

        var warned = new StartupReport([new CheckItem("A", CheckStatus.Warn, "x")]);
        Assert.False(warned.HasFailures);
        Assert.True(warned.HasWarnings);

        var failed = new StartupReport([new CheckItem("A", CheckStatus.Fail, "x")]);
        Assert.True(failed.HasFailures);
    }

    [Fact]
    public void UnknownItemReturnsNull()
    {
        Assert.Null(new StartupReport([])["没有这一项"]);
    }

    /// <summary>
    /// 自检必须能在一个**正常**工作台上跑到结束，不抛异常。
    /// </summary>
    /// <remarks>
    /// 这条看起来平淡，但它守着一个真实的坑：自检查询的东西包括磁盘信息、
    /// 控制台编码、Python 解释器 —— 这三样在 CI/无控制台/无 Python 的环境里
    /// 都可能抛异常。一旦自检自己崩了，用户看到的是「程序启动就闪退」，
    /// 而真正的原因只是「它在检查环境」。
    /// </remarks>
    [Fact]
    public void RunOnHealthyWorkspaceDoesNotThrow()
    {
        var root = TempDir();

        try
        {
            var report = StartupCheck.Run(root);

            Assert.NotEmpty(report.Items);
            Assert.True(report.Items.Count >= 8, $"只有 {report.Items.Count} 项，自检被裁过？");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fatfish_chk_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
