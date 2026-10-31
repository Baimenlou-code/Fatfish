using System.Net;
using System.Text;
using FatFish.Core.Net;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 联网后端的解析与错误处理。
///
/// ★ 全部用假的 <see cref="HttpMessageHandler"/>，一个字节都不出网。
///   理由不只是「快」：真实接口的返回会变，而这里要锁的是**我们对已知形状的理解**。
///   哪天官方改了字段名，这组测试不会红（它测的是旧形状），
///   但它能保证「改代码时不会把已经处理好的东西改坏」——这是单元测试该干的活。
/// </summary>
public class NetProviderTests
{
    // ------------------------------------------------------------------
    // DeepSeek 官方 Search / Fetch
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeepSeek_ParsesResults()
    {
        const string json = """
        {
          "results": [
            {
              "title": "DeepSeek 官方文档",
              "url": "https://api-docs.deepseek.com/guides",
              "summary": "这里是摘要内容",
              "date": "2026-06-01T00:00:00+00:00",
              "rerank_score": 0.93,
              "site_name": "api-docs.deepseek.com"
            },
            {
              "title": "不该出现的",
              "url": "ftp://example.com/file",
              "summary": "非 http 链接"
            },
            {
              "title": "第二条",
              "url": "https://example.com/b",
              "summary": "s2",
              "rerank_score": 0.51
            }
          ]
        }
        """;

        var provider = BuildDeepSeek(_ => Json(HttpStatusCode.OK, json));

        var hits = await provider.SearchAsync(new NetQuery("deepseek 文档", 5));

        Assert.Equal(2, hits.Count);   // ftp 那条被丢掉

        Assert.Equal("DeepSeek 官方文档", hits[0].Title);
        Assert.Equal("2026-06-01", hits[0].Published);
        Assert.Equal(0.93, hits[0].Score);
        Assert.Equal("api-docs.deepseek.com", hits[0].Site);

        // 按 rerank_score 降序
        Assert.Equal("https://example.com/b", hits[1].Url);
    }

    [Fact]
    public async Task DeepSeek_CountIsClampedAndUrlHit()
    {
        Uri? called = null;

        var provider = BuildDeepSeek(req =>
        {
            called = req.RequestUri;
            return Json(HttpStatusCode.OK, """{"results":[]}""");
        });

        await provider.SearchAsync(new NetQuery("q", Count: 999));

        Assert.NotNull(called);
        Assert.EndsWith("/v1/tools/search", called!.AbsolutePath);
    }

    [Fact]
    public async Task DeepSeek_402SaysTopUp()
    {
        var provider = BuildDeepSeek(_ => Json(
            HttpStatusCode.PaymentRequired,
            """{"error":{"message":"Insufficient balance"}}"""));

        var ex = await Assert.ThrowsAsync<NetException>(
            () => provider.SearchAsync(new NetQuery("q")));

        Assert.Equal(402, ex.StatusCode);

        // ★ 关键断言：消息里必须出现「充值」。
        //   官方原文是 "Insufficient balance"，模型看到这个会去改 query；
        //   而真正要做的是充值。一句话的差别决定了模型会不会白烧钱重试。
        Assert.Contains("余额不足", ex.Message);
        Assert.Contains("充值", ex.Message);
    }

    [Fact]
    public async Task DeepSeek_401IsNotRetried()
    {
        var calls = 0;

        var provider = BuildDeepSeek(_ =>
        {
            calls++;
            return Json(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}""");
        });

        await Assert.ThrowsAsync<NetException>(() => provider.SearchAsync(new NetQuery("q")));

        // 401 重试一百次也还是 401 —— 必须只打一次
        Assert.Equal(1, calls);
    }

    /// <summary>429 会被重试（这条测试要等一次退避，约 1.5 秒）。</summary>
    [Fact]
    public async Task DeepSeek_429IsRetried()
    {
        var calls = 0;

        var provider = BuildDeepSeek(_ =>
        {
            calls++;

            return calls == 1
                ? Json((HttpStatusCode)429, """{"error":{"message":"rate limited"}}""")
                : Json(HttpStatusCode.OK, """
                  {"results":[{"title":"ok","url":"https://x.com/1","summary":"s"}]}
                  """);
        });

        var hits = await provider.SearchAsync(new NetQuery("q"));

        Assert.Equal(2, calls);
        Assert.Single(hits);
    }

    [Fact]
    public async Task DeepSeek_FetchReportsPerPageErrors()
    {
        var provider = BuildDeepSeek(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;

            // 第二个 URL 报 404，第一个正常
            return body.Contains("/bad", StringComparison.Ordinal)
                ? Json(HttpStatusCode.NotFound, """{"error":{"message":"not found"}}""")
                : Json(HttpStatusCode.OK, """{"title":"T","content":"正文内容"}""");
        });

        var hits = await provider.FetchAsync(["https://x.com/good", "https://x.com/bad"], 1000);

        Assert.Equal(2, hits.Count);
        Assert.Null(hits.Single(h => h.Url.EndsWith("good", StringComparison.Ordinal)).Error);
        Assert.NotNull(hits.Single(h => h.Url.EndsWith("bad", StringComparison.Ordinal)).Error);
    }

    [Fact]
    public async Task DeepSeek_FetchThrowsOnlyWhenEveryPageFails()
    {
        var provider = BuildDeepSeek(_ =>
            Json(HttpStatusCode.NotFound, """{"error":{"message":"nope"}}"""));

        var ex = await Assert.ThrowsAsync<NetException>(
            () => provider.FetchAsync(["https://x.com/a", "https://x.com/b"], 1000));

        Assert.Contains("都没抓到正文", ex.Message);
    }

    [Fact]
    public void DeepSeek_DateFilterIsAppendedOnce()
    {
        var q = DeepSeekNetProvider.ApplyDateFilter("rust 1.80", 30);

        Assert.StartsWith("rust 1.80 after:", q, StringComparison.Ordinal);

        // 用户自己写了时间过滤就不动（否则会出现两个 after:）
        Assert.Equal("rust after:2025-01-01", DeepSeekNetProvider.ApplyDateFilter("rust after:2025-01-01", 30));

        // 没指定天数就不动
        Assert.Equal("rust", DeepSeekNetProvider.ApplyDateFilter("rust", null));
    }

    [Theory]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/chat/completions", "https://api.deepseek.com")]
    [InlineData("  https://custom.example.com/api  ", "https://custom.example.com/api")]
    [InlineData(null, "https://api.deepseek.com")]
    [InlineData("", "https://api.deepseek.com")]
    public void DeepSeek_NormalizeBaseHandlesThreeShapes(string? input, string expected)
    {
        Assert.Equal(expected, DeepSeekNetProvider.NormalizeBase(input));
    }

    // ------------------------------------------------------------------
    // Tavily
    // ------------------------------------------------------------------

    [Fact]
    public async Task Tavily_AttachesAnswerToFirstHit()
    {
        const string json = """
        {
          "answer": "一句话答案",
          "results": [
            {"title": "A", "url": "https://x.com/a", "content": "正文A", "score": 0.8},
            {"title": "B", "url": "https://x.com/b", "content": "正文B", "score": 0.5}
          ]
        }
        """;

        var provider = BuildTavily(_ => Json(HttpStatusCode.OK, json));

        var hits = await provider.SearchAsync(new NetQuery("q"));

        Assert.Equal(2, hits.Count);
        Assert.StartsWith("【摘要】一句话答案", hits[0].Snippet);
        Assert.Contains("正文A", hits[0].Snippet);
    }

    [Fact]
    public async Task Tavily_401HasActionableMessage()
    {
        var provider = BuildTavily(_ =>
            Json(HttpStatusCode.Unauthorized, """{"detail":"unauthorized"}"""));

        var ex = await Assert.ThrowsAsync<NetException>(() => provider.SearchAsync(new NetQuery("q")));

        Assert.Contains("TAVILY_API_KEY", ex.Message);
    }

    [Fact]
    public async Task Tavily_FailedResultsBecomeErrors()
    {
        const string json = """
        {
          "results": [{"url": "https://x.com/a", "raw_content": "正文"}],
          "failed_results": [{"url": "https://x.com/b", "error": "blocked"}]
        }
        """;

        var provider = BuildTavily(_ => Json(HttpStatusCode.OK, json));

        var hits = await provider.FetchAsync(["https://x.com/a", "https://x.com/b"], 1000);

        Assert.Equal(2, hits.Count);
        Assert.Null(hits[0].Error);
        Assert.Equal("blocked", hits[1].Error);
    }

    // ------------------------------------------------------------------
    // 余额
    // ------------------------------------------------------------------

    [Fact]
    public async Task Balance_PicksCnyBalance()
    {
        const string json = """
        {
          "is_available": true,
          "balance_infos": [
            {"currency": "USD", "total_balance": "1.00"},
            {"currency": "CNY", "total_balance": "60.99", "granted_balance": "0.99", "topped_up_balance": "60.00"}
          ]
        }
        """;

        var svc = new BalanceService(new HttpClient(new FakeHandler(_ =>
            Json(HttpStatusCode.OK, json)), disposeHandler: false));

        var b = await svc.FetchDeepSeekAsync();

        Assert.True(b.Ok);
        Assert.Equal(60.99, b.Total, precision: 2);
        Assert.Equal(60.00, b.ToppedUp, precision: 2);
        Assert.Equal("CNY", b.Currency);
    }

    [Fact]
    public async Task Balance_TavilyTakesMinOfPlanAndKey()
    {
        // 计划池还剩 700，但这把 key 只剩 200 → 实际可用 200
        const string json = """
        {
          "key": {"limit": 1000, "usage": 800},
          "account": {"current_plan": "dev", "plan_limit": 1000, "plan_usage": 300,
                      "search_usage": 200, "extract_usage": 100}
        }
        """;

        var svc = new BalanceService(new HttpClient(new FakeHandler(_ =>
            Json(HttpStatusCode.OK, json)), disposeHandler: false));

        var t = await svc.FetchTavilyAsync();

        Assert.True(t.Ok);
        Assert.Equal(200, t.Left);
        Assert.Equal(70.0, t.LeftPct);
        Assert.Equal("dev", t.Plan);
    }

    [Theory]
    [InlineData(0.0, BalanceLevel.Dead)]
    [InlineData(3.0, BalanceLevel.Crit)]
    [InlineData(12.0, BalanceLevel.Warn)]
    [InlineData(80.0, BalanceLevel.Ok)]
    public void Balance_GradesDeepSeekByThreshold(double total, BalanceLevel expected)
    {
        var b = new DeepSeekBalance(true, "CNY", total, 0, total, true, "***", null, default);

        Assert.Equal(expected, BalanceService.GradeDeepSeek(b, warn: 20, crit: 5));
    }

    [Fact]
    public void Balance_UnknownWhenQueryFailed()
    {
        var b = new DeepSeekBalance(false, "CNY", 0, 0, 0, false, "***", "boom", default);

        Assert.Equal(BalanceLevel.Unknown, BalanceService.GradeDeepSeek(b));
    }

    [Fact]
    public void Balance_TavilyGradesByPercent()
    {
        var t = new TavilyUsage(true, "dev", 1000, 990, 0, 0, 10, 1.0, 0, 0, "***", null, default);

        Assert.Equal(BalanceLevel.Crit, BalanceService.GradeTavily(t));
    }

    [Fact]
    public void Balance_OneLineNeverThrowsOnFailures()
    {
        var snap = new BalanceSnapshot(
            new DeepSeekBalance(false, "CNY", 0, 0, 0, false, "***", "err", default),
            new TavilyUsage(false, "?", 0, 0, 0, 0, null, null, 0, 0, "***", "err", default),
            default);

        var line = BalanceService.OneLine(snap);

        Assert.Contains("DS ?", line);
        Assert.Contains("TV ?", line);
    }

    // ------------------------------------------------------------------

    private static DeepSeekNetProvider BuildDeepSeek(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var options = new NetOptions
        {
            DeepSeekApiKey = "sk-test",
            DeepSeekBaseUrl = "https://api.deepseek.com",
            Qps = 1000,          // 测试里不需要限速
            TimeoutSeconds = 5,
        };

        return new DeepSeekNetProvider(
            options,
            new HttpClient(new FakeHandler(respond), disposeHandler: false));
    }

    private static TavilyNetProvider BuildTavily(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var options = new NetOptions
        {
            TavilyApiKey = "tvly-test",
            Qps = 1000,
            TimeoutSeconds = 5,
        };

        return new TavilyNetProvider(
            options,
            new HttpClient(new FakeHandler(respond), disposeHandler: false));
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
