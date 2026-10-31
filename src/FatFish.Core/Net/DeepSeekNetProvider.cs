using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FatFish.Core.Net;

/// <summary>
/// DeepSeek 官方 Search / Fetch 后端。
///
/// 端点（2026 官方文档）：
///   POST {base}/v1/tools/search   {"query","count","need_content","ori_question","localization"}
///   POST {base}/v1/tools/fetch    {"url"}
///
/// 计费口径（决定 <see cref="NetService"/> 怎么记账）：
///   Search：HTTP 200 **且 results 非空** 才算一次成功请求（5 元 / 1000 次）。
///   Fetch ：每个成功取到正文的页面各算一次。
///   所以「搜到 0 条」是不花钱的 —— 这一点很重要：网络抖动导致的空结果不该出现在账单上。
///
/// 限速：Search / Fetch 各 5 QPS（账号级）。本地用令牌桶压住，
/// 撞 429 时按 1.5s / 3s 退避重试最多 3 次。
/// </summary>
public sealed class DeepSeekNetProvider : INetProvider
{
    private const string SearchPath = "/v1/tools/search";
    private const string FetchPath = "/v1/tools/fetch";

    /// <summary>网络抖动 / 限速类错误：等一会儿重试有意义。</summary>
    private static readonly HashSet<int> Retryable = [408, 429];

    private readonly NetOptions _options;
    private readonly HttpClient _http;

    private readonly TokenBucket _searchBucket;
    private readonly TokenBucket _fetchBucket;

    public DeepSeekNetProvider(NetOptions options, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = http ?? new HttpClient();

        // Search / Fetch 各自一个桶：它们是两个独立的 QPS 配额，
        // 共用一个桶会让「一边抓 5 页一边搜」变成两个都卡。
        _searchBucket = new TokenBucket(options.Qps);
        _fetchBucket = new TokenBucket(options.Qps);
    }

    public string Name => "deepseek";

    public bool SupportsFetch => true;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.DeepSeekApiKey);

    /// <summary>官方 API 的根地址。允许用户把 <c>/v1</c> 写进配置。</summary>
    public string BaseUrl => NormalizeBase(_options.DeepSeekBaseUrl);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(NetQuery query, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new NetException("未配置 DeepSeek API Key（FATFISH_API_KEY / DEEPSEEK_API_KEY）", Name);

        var q = ApplyDateFilter((query.Query ?? string.Empty).Trim(), query.Days);

        if (q.Length == 0)
            throw new NetException("query 不能为空（1~128 个字符）", Name);

        // 官方硬限制 128 字符。★ 先截断再拼 after:，否则日期后缀会把最后几个字挤掉，
        // 而「被截断的检索词」看起来是完整的 —— 搜出来的东西不对也没人知道为什么。
        if (q.Length > 128)
            q = q[..128];

        var count = Math.Clamp(query.Count, 1, 50);

        var body = new Dictionary<string, object?>
        {
            ["query"] = q,
            ["count"] = count,
            ["need_content"] = query.NeedContent,
        };

        if (!string.IsNullOrWhiteSpace(query.OriginalQuestion))
            body["ori_question"] = query.OriginalQuestion!.Length > 128
                ? query.OriginalQuestion[..128]
                : query.OriginalQuestion;

        var localization = Environment.GetEnvironmentVariable("DEEPSEEK_SEARCH_LOCALIZATION");
        if (!string.IsNullOrWhiteSpace(localization))
            body["localization"] = localization.Trim();

        using var doc = await PostAsync(SearchPath, body, "search", _searchBucket, ct).ConfigureAwait(false);

        var hits = new List<SearchHit>();

        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("results", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var hit = ToHit(item, query.NeedContent);
                if (hit is not null)
                    hits.Add(hit);
            }
        }

        // 官方已排序；再排一次是为了防网关改序，score 缺失的排最后。
        hits.Sort((a, b) =>
        {
            var sa = a.Score ?? double.MinValue;
            var sb2 = b.Score ?? double.MinValue;
            return sb2.CompareTo(sa);
        });

        // min_rerank 过滤：全被滤掉就不过滤（宁可给差结果的，也别给空列表）
        var minRerank = GetDouble("DEEPSEEK_SEARCH_MIN_RERANK", 0);
        if (minRerank > 0)
        {
            var good = hits.Where(h => (h.Score ?? 0) >= minRerank).ToList();
            if (good.Count > 0)
                hits = good;
        }

        return hits.Count > count ? hits.GetRange(0, count) : hits;
    }

    public async Task<IReadOnlyList<FetchHit>> FetchAsync(
        IReadOnlyList<string> urls,
        int maxChars,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new NetException("未配置 DeepSeek API Key（FATFISH_API_KEY / DEEPSEEK_API_KEY）", Name);

        var results = new List<FetchHit>();

        // 去重 + 只留 http(s)
        var clean = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var u in urls ?? Array.Empty<string>())
        {
            if (!NetUrl.IsHttpUrl(u))
                continue;

            if (seen.Add(NetUrl.Key(u)))
                clean.Add(u.Trim());
        }

        // Fetch 有域名级 QPS 限制，一页一页来。★ 并行抓同一站点更容易触发风控，
        // 而且失败时完全看不出是哪一页被拦了。
        foreach (var url in clean.Take(5))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var doc = await PostAsync(
                    FetchPath, new { url }, "fetch", _fetchBucket, ct).ConfigureAwait(false);

                var content = GetString(doc.RootElement, "content") ?? string.Empty;
                var title = GetString(doc.RootElement, "title") ?? url;

                if (content.Length == 0)
                {
                    results.Add(new FetchHit(url, title, string.Empty, "正文为空"));
                    continue;
                }

                if (content.Length > maxChars)
                    content = content[..maxChars] + "\n…（已截断）";

                results.Add(new FetchHit(url, title, content));
            }
            catch (NetException ex)
            {
                // 单页失败不影响其它页 —— 抓 5 个链接时有 1 个 404 是常态
                results.Add(new FetchHit(url, url, string.Empty, ex.Message));
            }
        }

        if (results.Count > 0 && results.All(r => r.Error is not null))
            throw new NetException(
                "所有页面都没抓到正文：" + string.Join("；", results.Take(2).Select(r => r.Error)),
                Name);

        return results;
    }

    // ------------------------------------------------------------------
    // HTTP
    // ------------------------------------------------------------------

    private async Task<JsonDocument> PostAsync(
        string path,
        object payload,
        string api,
        TokenBucket bucket,
        CancellationToken ct)
    {
        var url = BaseUrl + path;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        string lastMessage = "未知失败";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (!await bucket.AcquireAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
                throw new NetException($"本地限速等待超时（QPS {_options.Qps}），请稍后重试", Name);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            HttpResponseMessage resp;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(payload, options: Tools.ToolJson.DefaultOptions),
                };

                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _options.DeepSeekApiKey);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                req.Headers.TryAddWithoutValidation("User-Agent", "FatFish-Net/1.0");

                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 自己的超时（不是用户取消）
                lastMessage = $"DeepSeek {api} 请求超时（{timeout.TotalSeconds:0}s）";

                if (attempt == 3)
                    throw new NetException(lastMessage, Name, 408);

                await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                lastMessage = $"网络异常（{url}）：{ex.Message}";

                if (attempt == 3)
                    throw new NetException(lastMessage, Name, null, ex);

                await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                continue;
            }

            using (resp)
            {
                var code = (int)resp.StatusCode;

                if (code == 200)
                {
                    var raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                    try
                    {
                        return JsonDocument.Parse(raw);
                    }
                    catch (JsonException)
                    {
                        throw new NetException(
                            $"返回不是 JSON：{(raw.Length > 300 ? raw[..300] : raw)}", Name, code);
                    }
                }

                var msg = await ReadErrorMessageAsync(resp, ct).ConfigureAwait(false);
                lastMessage = Hint(code, api, msg);

                if (Retryable.Contains(code) || code >= 500)
                {
                    if (attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(8.0, 1.5 * attempt)), ct)
                            .ConfigureAwait(false);
                        continue;
                    }

                    throw new NetException(lastMessage, Name, code);
                }

                throw new NetException(lastMessage, Name, code);
            }
        }

        throw new NetException(lastMessage, Name);
    }

    /// <summary>官方错误体是 OpenAI 兼容格式：{"error":{"message":...}}。</summary>
    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text))
                return resp.ReasonPhrase ?? string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.Object)
                    {
                        var m = GetString(err, "message") ?? GetString(err, "code");
                        if (m is not null) return m;
                    }

                    return err.ToString();
                }

                var plain = GetString(root, "message");
                if (plain is not null) return plain;
            }
            catch (JsonException)
            {
                // 不是 JSON：退回原文
            }

            return text.Length > 300 ? text[..300] : text;
        }
        catch (Exception)
        {
            return resp.ReasonPhrase ?? string.Empty;
        }
    }

    /// <summary>
    /// 把 HTTP 状态码翻成「用户能据此行动」的提示。
    ///
    /// ★ 这张表的价值在于：官方返回的只是一句英文 message，
    ///   而模型看到「HTTP 402」会继续换参数重试 —— 改任何参数都没用，得充值。
    /// </summary>
    private static string Hint(int code, string api, string msg)
    {
        var hint = code switch
        {
            400 => "请求不合法（query 长度 / count 范围 / JSON 格式）",
            401 => "API Key 缺失或无效",
            402 => "账号余额不足 —— 请先充值，改参数重试没有用",
            403 => "Fetch 被安全策略拒绝（目标站点/URL 不允许抓取）",
            404 => "Fetch 未取到可用正文（URL 不可用 / 被站点拦截 / 类型不支持）",
            408 => "读取请求体超时",
            413 => "请求体超过大小限制",
            429 => "超过 QPS 限制（Search/Fetch 各 5 QPS，账号级）",
            500 => "服务器内部错误",
            504 => "下游请求超时",
            _ => "请求失败",
        };

        return $"DeepSeek {api} HTTP {code} {hint}：{msg}";
    }

    // ------------------------------------------------------------------
    // 解析助手
    // ------------------------------------------------------------------

    private SearchHit? ToHit(JsonElement item, bool wantContent)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var url = GetString(item, "url") ?? string.Empty;

        // 只收 http(s)：官方偶尔会返回站内跳转链接，那种东西既抓不到也没法引用
        if (!NetUrl.IsHttpUrl(url))
            return null;

        var title = GetString(item, "title") ?? url;
        var summary = GetString(item, "summary") ?? GetString(item, "snippet") ?? string.Empty;

        if (summary.Length > _options.SnippetLimit)
            summary = summary[.._options.SnippetLimit] + "…";

        var date = ToIsoDay(GetString(item, "date"));
        var site = GetString(item, "site_name") ?? string.Empty;

        double? score = null;
        if (item.TryGetProperty("rerank_score", out var sc) && sc.ValueKind == JsonValueKind.Number)
            score = sc.GetDouble();

        string? content = null;
        if (wantContent)
        {
            var c = GetString(item, "content");
            if (!string.IsNullOrWhiteSpace(c))
                content = c.Length > 3_000 ? c[..3_000] + "…" : c;
        }

        return new SearchHit(
            Trim(title, 200),
            url.Trim(),
            summary,
            date,
            score,
            site,
            content);
    }

    /// <summary>'2026-06-01T00:00:00+00:00' → '2026-06-01'（模型看得更清楚）。</summary>
    private static string ToIsoDay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var s = value.Trim();

        if (s.Length >= 10
            && s[4] == '-' && s[7] == '-'
            && int.TryParse(s[..4], out _) && int.TryParse(s[5..7], out _) && int.TryParse(s[8..10], out _))
            return s[..10];

        return s.Length > 40 ? s[..40] : s;
    }

    /// <summary>把 days 翻译成官方支持的 <c>after:</c> 语法；用户自己写了时间过滤就不动。</summary>
    public static string ApplyDateFilter(string query, int? days)
    {
        if (days is null or <= 0)
            return query;

        var low = query.ToLowerInvariant();
        if (low.Contains("after:") || low.Contains("before:"))
            return query;

        var since = DateTime.UtcNow.AddDays(-Math.Max(1, days.Value))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return $"{query} after:{since}";
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    private static string? GetString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            _ => null,
        };
    }

    private static double GetDouble(string envName, double fallback)
    {
        var v = Environment.GetEnvironmentVariable(envName);
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : fallback;
    }

    /// <summary>
    /// 归一化 base url：允许 <c>https://api.deepseek.com</c>、<c>.../v1</c>、
    /// 甚至误配成 chat 地址 —— 三种都能用。
    /// </summary>
    public static string NormalizeBase(string? url)
    {
        var b = string.IsNullOrWhiteSpace(url)
            ? "https://api.deepseek.com"
            : url.Trim().TrimEnd('/');

        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            b = b[..^"/chat/completions".Length];

        if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            b = b[..^3];

        return b.TrimEnd('/');
    }
}
