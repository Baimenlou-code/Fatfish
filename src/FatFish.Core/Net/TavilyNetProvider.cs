using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace FatFish.Core.Net;

/// <summary>
/// Tavily 后端（search + extract）。
///
///   POST https://api.tavily.com/search   {"api_key","query","max_results","search_depth","include_answer"}
///   POST https://api.tavily.com/extract  {"api_key","urls","extract_depth"}
///
/// 计费：basic = 1 credit，advanced = 2 credits。免费额度每月 1000 credits。
/// 所以默认用 basic 搜索（够用且便宜），抓正文用 advanced（默认值，抓得更全）。
///
/// ★ api_key 放在 JSON body 里，不是 Authorization 头 —— 这是 Tavily 的历史设计。
///   两种写法官方都收，但 /usage 端点只认 Bearer 头；混在一起容易配错，
///   所以这里显式按端点区分（参见 <see cref="Balance.BalanceService"/>）。
/// </summary>
public sealed class TavilyNetProvider : INetProvider
{
    private const string SearchUrl = "https://api.tavily.com/search";
    private const string ExtractUrl = "https://api.tavily.com/extract";

    private readonly NetOptions _options;
    private readonly HttpClient _http;

    public TavilyNetProvider(NetOptions options, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = http ?? new HttpClient();
    }

    public string Name => "tavily";

    public bool SupportsFetch => true;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.TavilyApiKey);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(NetQuery query, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new NetException("未配置 TAVILY_API_KEY，无法联网", Name);

        var payload = new Dictionary<string, object?>
        {
            ["api_key"] = _options.TavilyApiKey,
            ["query"] = (query.Query ?? string.Empty).Trim(),
            ["max_results"] = Math.Clamp(query.Count, 1, 20),

            // basic = 1 credit / advanced = 2 credits。
            // 默认用便宜的：需要更全的话，调用方会改用官方后端。
            ["search_depth"] = "basic",
            ["include_answer"] = true,
        };

        if (query.Days is > 0)
            payload["days"] = query.Days.Value;

        using var doc = await PostAsync(SearchUrl, payload, "search", ct).ConfigureAwait(false);

        var hits = new List<SearchHit>();
        var answer = GetString(doc.RootElement, "answer");

        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("results", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var url = GetString(item, "url") ?? string.Empty;
                if (!NetUrl.IsHttpUrl(url))
                    continue;

                var title = GetString(item, "title") ?? "（无标题）";
                var content = (GetString(item, "content") ?? string.Empty).Trim();

                if (content.Length > _options.SnippetLimit)
                    content = content[.._options.SnippetLimit] + "…";

                double? score = null;
                if (item.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number)
                    score = sc.GetDouble();

                hits.Add(new SearchHit(
                    title, url, content,
                    GetString(item, "published_date") is { Length: >= 10 } pd ? pd[..10] : string.Empty,
                    score,
                    SiteOf(url),
                    null));
            }
        }

        // Tavily 的 answer 是一段现成摘要。挂在第一条上（而不是丢进 Text），
        // 这样融合时它跟着「摘要」一起走，不会被当成一条独立结果参与排序。
        if (!string.IsNullOrWhiteSpace(answer) && hits.Count > 0)
            hits[0] = hits[0] with { Snippet = "【摘要】" + answer.Trim() + "\n" + hits[0].Snippet };

        return hits;
    }

    public async Task<IReadOnlyList<FetchHit>> FetchAsync(
        IReadOnlyList<string> urls,
        int maxChars,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new NetException("未配置 TAVILY_API_KEY，无法联网", Name);

        var clean = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var u in urls ?? Array.Empty<string>())
        {
            if (!NetUrl.IsHttpUrl(u))
                continue;

            if (seen.Add(NetUrl.Key(u)))
                clean.Add(u.Trim());
        }

        if (clean.Count == 0)
            throw new NetException("没有可抓取的 URL", Name);

        // Tavily 一次最多 20 个，但一次抓太多会很难看出哪条失败 —— 与官方后端对齐，取 5 个
        var batch = clean.Take(5).ToList();

        using var doc = await PostAsync(ExtractUrl, new Dictionary<string, object?>
        {
            ["api_key"] = _options.TavilyApiKey,
            ["urls"] = batch,
            ["extract_depth"] = "advanced",
        }, "extract", ct).ConfigureAwait(false);

        var results = new List<FetchHit>();

        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("results", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var url = GetString(item, "url") ?? string.Empty;
                var content = (GetString(item, "raw_content") ?? GetString(item, "content") ?? string.Empty).Trim();

                if (content.Length == 0)
                {
                    results.Add(new FetchHit(url, url, string.Empty, "正文为空"));
                    continue;
                }

                if (content.Length > maxChars)
                    content = content[..maxChars] + "\n…（已截断）";

                results.Add(new FetchHit(url, GetString(item, "title") ?? SiteOf(url), content));
            }
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("failed_results", out var failed)
            && failed.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in failed.EnumerateArray())
            {
                results.Add(new FetchHit(
                    GetString(item, "url") ?? "?",
                    string.Empty,
                    string.Empty,
                    GetString(item, "error") ?? "未知错误"));
            }
        }

        if (results.Count > 0 && results.All(r => r.Error is not null))
            throw new NetException(
                "所有页面都没抓到正文：" + string.Join("；", results.Take(2).Select(r => r.Error)),
                Name);

        if (results.Count == 0)
            throw new NetException("未抓到内容", Name);

        return results;
    }

    // ------------------------------------------------------------------

    private async Task<JsonDocument> PostAsync(
        string url,
        Dictionary<string, object?> payload,
        string api,
        CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        HttpResponseMessage resp;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload, options: Tools.ToolJson.DefaultOptions),
            };

            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "FatFish-Net/1.0");

            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NetException($"Tavily {api} 请求超时（{timeout.TotalSeconds:0}s）", Name, 408);
        }
        catch (HttpRequestException ex)
        {
            throw new NetException($"Tavily {api} 请求失败：{ex.Message}", Name, null, ex);
        }

        using (resp)
        {
            var code = (int)resp.StatusCode;

            if (code != 200)
            {
                var msg = await SafeTextAsync(resp, ct).ConfigureAwait(false);

                var hint = code switch
                {
                    401 => "401 未授权，TAVILY_API_KEY 可能无效或过期",
                    429 => "429 限速（免费额度可能已用完）",
                    432 => "432 计划额度用尽",
                    _ => $"HTTP {code}",
                };

                throw new NetException(
                    $"Tavily {api} 出错：{hint}：{(msg.Length > 200 ? msg[..200] : msg)}",
                    Name, code);
            }

            var raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            try
            {
                return JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                throw new NetException(
                    $"Tavily {api} 返回不是 JSON：{(raw.Length > 300 ? raw[..300] : raw)}", Name, code);
            }
        }
    }

    private static async Task<string> SafeTextAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return resp.ReasonPhrase ?? string.Empty;
        }
    }

    private static string SiteOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : string.Empty;

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
}
