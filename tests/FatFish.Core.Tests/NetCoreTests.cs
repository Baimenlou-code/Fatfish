using FatFish.Core.Net;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 联网层的纯逻辑部分：URL 归一、RRF 融合、路由、限速、台账。
///
/// 这一组测试**全部不联网**。理由：
///   联网测试慢、不稳定、还要花钱，跑一次 CI 就烧掉几毛钱；
///   而这层里真正容易错的东西（归一规则、融合权值、月度归属）都是纯函数，
///   用假数据测比真机测更准、更快、更全面。
/// </summary>
public class NetCoreTests
{
    // ------------------------------------------------------------------
    // URL 归一
    // ------------------------------------------------------------------

    [Fact]
    public void NetUrl_DropsTrackingParams()
    {
        var a = NetUrl.Key("https://Example.COM/a/b/?utm_source=x&utm_medium=y&id=7");
        var b = NetUrl.Key("https://example.com/a/b?id=7");

        Assert.Equal(b, a);
    }

    [Fact]
    public void NetUrl_KeepsMeaningfulQuery()
    {
        // 只削跟踪参数。像 id / page 这种会影响内容，必须保留 ——
        // 削过头会把两篇不同的文章合并成一条，而这种错误在日志里看不出来。
        var a = NetUrl.Key("https://x.com/p?id=1");
        var b = NetUrl.Key("https://x.com/p?id=2");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NetUrl_NormalizesTrailingSlashAndHostCase()
    {
        Assert.Equal(
            NetUrl.Key("https://example.com/path"),
            NetUrl.Key("HTTPS://EXAMPLE.COM/path/"));
    }

    [Fact]
    public void NetUrl_RootPathBecomesSlash()
    {
        Assert.Equal("example.com/", NetUrl.Key("https://example.com"));
    }

    [Theory]
    [InlineData("https://a.com/x", true)]
    [InlineData("http://a.com", true)]
    [InlineData("ftp://a.com/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///E:/secret.txt", false)]
    [InlineData("a.com/x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void NetUrl_AcceptsOnlyHttp(string? url, bool expected)
    {
        Assert.Equal(expected, NetUrl.IsHttpUrl(url));
    }

    // ------------------------------------------------------------------
    // RRF 融合
    // ------------------------------------------------------------------

    private static SearchHit Hit(string url, string snippet = "s", double? score = null)
        => new(url, url, snippet, Score: score);

    [Fact]
    public void Merge_RrfPrefersHitsFoundByBothBackends()
    {
        var a = new Dictionary<string, IReadOnlyList<SearchHit>>
        {
            ["deepseek"] = [Hit("https://x.com/1"), Hit("https://x.com/2"), Hit("https://x.com/3")],
        };

        var b = new Dictionary<string, IReadOnlyList<SearchHit>>
        {
            ["tavily"] = [Hit("https://x.com/3"), Hit("https://x.com/4")],
        };

        var all = new Dictionary<string, IReadOnlyList<SearchHit>>(StringComparer.Ordinal);
        foreach (var (k, v) in a) all[k] = v;
        foreach (var (k, v) in b) all[k] = v;

        var merged = NetRouter.Merge(all, "rrf", limit: 10);

        // x.com/3 在两个后端里都出现（名次 3 与 1）：
        //   1/(60+3) + 1/(60+1) ≈ 0.0323
        // x.com/1 只在 deepseek 排第 1：
        //   1/(60+1) ≈ 0.0164
        // 所以「两边都有人提」的结果必须排在「只有一个后端的第一名」前面。
        Assert.Equal("https://x.com/3", merged[0].Url);
        Assert.Equal(4, merged.Count);
    }

    [Fact]
    public void Merge_DeduplicatesByUrl()
    {
        var all = new Dictionary<string, IReadOnlyList<SearchHit>>(StringComparer.Ordinal)
        {
            ["deepseek"] = [Hit("https://x.com/a?utm_source=q")],
            ["tavily"] = [Hit("https://x.com/a")],
        };

        var merged = NetRouter.Merge(all, "rrf", limit: 10);

        Assert.Single(merged);
    }

    [Fact]
    public void Merge_KeepsLongerSnippet()
    {
        var all = new Dictionary<string, IReadOnlyList<SearchHit>>(StringComparer.Ordinal)
        {
            ["deepseek"] = [Hit("https://x.com/a", "短")],
            ["tavily"] = [Hit("https://x.com/a", "长得多的一段摘要内容")],
        };

        var merged = NetRouter.Merge(all, "rrf", limit: 10);

        Assert.Equal("长得多的一段摘要内容", merged[0].Snippet);
    }

    [Fact]
    public void Merge_NoneMode_KeepsBackendOrder()
    {
        var all = new Dictionary<string, IReadOnlyList<SearchHit>>(StringComparer.Ordinal)
        {
            ["deepseek"] = [Hit("https://x.com/1")],
            ["tavily"] = [Hit("https://x.com/2")],
        };

        var merged = NetRouter.Merge(all, "none", limit: 10);

        Assert.Equal(2, merged.Count);
        Assert.Equal("https://x.com/1", merged[0].Url);
        Assert.Equal("https://x.com/2", merged[1].Url);
    }

    [Fact]
    public void Merge_RespectsLimit()
    {
        var all = new Dictionary<string, IReadOnlyList<SearchHit>>(StringComparer.Ordinal)
        {
            ["deepseek"] = Enumerable.Range(1, 10).Select(i => Hit($"https://x.com/{i}")).ToList(),
        };

        Assert.Equal(3, NetRouter.Merge(all, "rrf", limit: 3).Count);
    }

    // ------------------------------------------------------------------
    // 路由
    // ------------------------------------------------------------------

    [Fact]
    public void Router_ChineseQuestionFavorsDeepSeek()
    {
        var router = new NetRouter();

        var decision = router.Route("阿里云百炼的接口怎么调用", ["deepseek", "tavily"], maxN: 1);

        Assert.Equal("deepseek", Assert.Single(decision.Backends));
        Assert.Contains("中文场景", decision.Why);
    }

    [Fact]
    public void Router_EnglishTechQuestionFavorsTavily()
    {
        var router = new NetRouter();

        var decision = router.Route(
            "kubernetes ingress github issue release notes", ["deepseek", "tavily"], maxN: 1);

        Assert.Equal("tavily", Assert.Single(decision.Backends));
    }

    [Fact]
    public void Router_NeverReturnsMoreThanMaxN()
    {
        var router = new NetRouter();

        var decision = router.Route("随便什么问题", ["deepseek", "tavily", "bocha"], maxN: 2);

        Assert.Equal(2, decision.Backends.Count);
    }

    [Fact]
    public void Router_EmptyCandidatesIsEmptyDecision()
    {
        var decision = new NetRouter().Route("q", Array.Empty<string>());

        Assert.True(decision.IsEmpty);
        Assert.Equal("没有可用后端", decision.Why);
    }

    /// <summary>
    /// 台账里连续失败的后端会被降权 —— 这是「记账反哺路由」的核心行为。
    /// </summary>
    [Fact]
    public void Router_DownweightsBackendWithPoorHistory()
    {
        var root = TempDir();

        try
        {
            var ledger = UsageLedger.Open(root);

            // deepseek 连挂 4 次（样本 ≥ 3 才生效，1 次失败不该打死一个后端）
            for (var i = 0; i < 4; i++)
                ledger.Record("search", "deepseek", "q", ok: false, n: 0, units: 0, ms: 10, error: "boom");

            var router = new NetRouter(ledger);

            // 中文问题本来偏向 deepseek（+0.35）；扣掉 0.4 之后应该输给 tavily。
            // maxN=2 让两个后端的理由都进 Why —— 只取 1 个的话，被淘汰那个的
            // 降权理由根本不会出现在决策文本里，也就没法验证「为什么它输了」。
            var decision = router.Route("阿里云百炼怎么用", ["deepseek", "tavily"], maxN: 2);

            Assert.Equal("tavily", decision.Backends[0]);
            Assert.Contains("历史成功率", decision.Why);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Router_SingleFailureDoesNotDownweight()
    {
        var root = TempDir();

        try
        {
            var ledger = UsageLedger.Open(root);
            ledger.Record("search", "deepseek", "q", ok: false, n: 0, units: 0, ms: 10);

            var decision = new NetRouter(ledger).Route("阿里云怎么用", ["deepseek", "tavily"], maxN: 1);

            // 只失败过一次 → 不降权，中文场景仍然选 deepseek
            Assert.Equal("deepseek", Assert.Single(decision.Backends));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // 限速
    // ------------------------------------------------------------------

    [Fact]
    public async Task TokenBucket_AllowsBurstUpToCapacityThenThrottles()
    {
        // qps=2 → 容量 = min(2,10) = 2
        var bucket = new TokenBucket(2);

        Assert.True(await bucket.AcquireAsync(TimeSpan.FromSeconds(1)));
        Assert.True(await bucket.AcquireAsync(TimeSpan.FromSeconds(1)));

        // 第 3 个在 0.2 秒内拿不到（每秒补 2 个 → 需 0.5 秒）
        Assert.False(await bucket.AcquireAsync(TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task TokenBucket_RefillsOverTime()
    {
        var bucket = new TokenBucket(20);

        // capacity = min(20,10) = 10，先把这 10 个掏空
        for (var i = 0; i < 10; i++)
            Assert.True(await bucket.AcquireAsync(TimeSpan.FromSeconds(1)));

        Assert.False(await bucket.AcquireAsync(TimeSpan.FromMilliseconds(5)));

        await Task.Delay(120);

        Assert.True(bucket.AvailableTokens > 0.5, $"补了 {bucket.AvailableTokens:0.00} 个令牌");
    }

    // ------------------------------------------------------------------
    // 台账
    // ------------------------------------------------------------------

    [Fact]
    public void Ledger_SummarizeCountsCallsAndUnits()
    {
        var root = TempDir();

        try
        {
            var ledger = UsageLedger.Open(root);

            ledger.Record("search", "deepseek", "q1", ok: true, n: 5, units: 1, ms: 100);
            ledger.Record("search", "deepseek", "q2", ok: true, n: 3, units: 1, ms: 120);
            ledger.Record("search", "tavily", "q3", ok: false, n: 0, units: 0, ms: 50);
            ledger.Record("fetch", "deepseek", "https://x.com", ok: true, n: 2, units: 2, ms: 900);

            var s = ledger.Summarize();

            Assert.Equal(4, s.TotalCalls);
            Assert.Equal(3, s.TotalOk);

            var ds = s.Backends.Single(b => b.Backend == "deepseek");
            Assert.Equal(3, ds.Calls);

            // 2 次 search（各 1 单位）+ 1 次 fetch（2 页 = 2 单位）= 4 单位
            // Fetch 按**页面**计费，不是按调用 —— 一次抓 2 页就是 2 次请求。
            Assert.Equal(4, ds.Units);

            // 官方口径 5 元 / 1000 次 → 3 次 ≈ 0.015 元（四舍五入到分）
            Assert.InRange(ds.Cost, 0.0, 0.03);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Ledger_SkipsCorruptLines()
    {
        var root = TempDir();

        try
        {
            var ledger = UsageLedger.Open(root);
            ledger.Record("search", "deepseek", "good", ok: true, n: 1, units: 1, ms: 10);

            // 模拟崩溃留下的半行
            File.AppendAllText(ledger.FilePath, "{\"ts\":\"2026-10-29T00:00:00Z\",\"kin");

            ledger.Record("search", "deepseek", "after-crash", ok: true, n: 1, units: 1, ms: 10);

            var all = ledger.All();

            Assert.Equal(2, all.Count);
            Assert.Equal("after-crash", all[1].Query);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Ledger_RecordNeverThrows_WhenDirectoryDisappears()
    {
        // 台账目录被删（或盘被拔掉）后继续记账：必须静默失败。
        // 否则「搜到了、但记账写不进去」会让整次搜索报错 —— 而用户明明拿到了结果。
        var root = TempDir();
        var ledger = UsageLedger.Open(root);

        Directory.Delete(root, recursive: true);

        var ex = Record.Exception(() =>
            ledger.Record("search", "deepseek", "q", ok: true, n: 1, units: 1, ms: 10));

        Assert.Null(ex);
        Assert.Empty(ledger.All());
    }

    [Fact]
    public void Ledger_BackendStatsComputesOkRate()
    {
        var root = TempDir();

        try
        {
            var ledger = UsageLedger.Open(root);

            for (var i = 0; i < 3; i++)
                ledger.Record("search", "tavily", "q", ok: true, n: 1, units: 1, ms: 5);

            ledger.Record("search", "tavily", "q", ok: false, n: 0, units: 0, ms: 5);

            var stats = ledger.BackendStats("tavily");

            Assert.Equal(4, stats.Calls);
            Assert.Equal(0.75, stats.OkRate, precision: 3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Ledger_UnknownBackendHasNeutralOkRate()
    {
        var root = TempDir();

        try
        {
            // 没跑过的后端 OkRate 记 1.0 —— 不能因为「没数据」就惩罚它
            Assert.Equal(1.0, UsageLedger.Open(root).BackendStats("bocha").OkRate);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fatfish_net_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
