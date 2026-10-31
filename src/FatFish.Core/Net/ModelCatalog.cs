using System.Net;
using System.Text.Json;
using FatFish.Core.Plugins;
using FatFish.Core.Tools;

namespace FatFish.Core.Net;

/// <summary>一次模型发现的结果。</summary>
/// <param name="Models">模型 id 列表（已去重、按字典序）。</param>
/// <param name="UsedKey">成功的那把 key 的序号（从 1 开始）；失败时为 null。</param>
/// <param name="Error">失败原因，一句话、能照着改。</param>
public sealed record ModelCatalogResult(
    bool Ok,
    IReadOnlyList<string> Models,
    string? Error = null,
    int? StatusCode = null,
    int? UsedKey = null,
    long ElapsedMs = 0)
{
    public static ModelCatalogResult Fail(string error, int? status = null, long elapsedMs = 0)
        => new(false, Array.Empty<string>(), error, status, null, elapsedMs);
}

/// <summary>
/// 模型发现：<c>GET {base}{modelsPath}</c>，Bearer 鉴权，把返回里的模型 id 抽出来。
/// </summary>
/// <remarks>
/// <b>为什么 UA 要写死成 okhttp/4.12.0：</b>
/// 生图插件当初实测过三种 UA —— Python-urllib 被 Cloudflare 直接 403（error 1010，
/// 响应里还写着 Do not retry），Chrome / curl 的 UA 会卡在 TLS 挑战上超时，
/// 只有 okhttp 这种 API 客户端风格能稳定拿到 200。这条经验在 .NET 这边同样适用：
/// HttpClient 默认不带 UA，被挡的概率比 okhttp 那行更高。
///
/// <b>为什么只对 401 / 403 换下一把 key：</b>
/// 这两类是"这把钥匙不行"，换一把有意义。429（限速/额度）换 key 也可能有用，
/// 但同一个账号的两把 key 通常共享额度，盲目轮换只会把日志搅浑；
/// 5xx 是上游的问题，换 key 一样失败，白等一轮。400 则几乎总是参数问题。
/// </remarks>
public static class ModelCatalog
{
    /// <summary>与生图插件一致的客户端 UA（见类注释里的实测结论）。</summary>
    public const string DefaultUserAgent = "okhttp/4.12.0";

    private const int MaxBodyBytes = 256 * 1024;
    private const int DefaultTimeoutSeconds = 10;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // 不跟随跳转：跳转目标可能不在插件声明的白名单里，
        // 而且 302 到登录页会让我们把「页面 HTML」当成 JSON 去解析。
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// 按 key 库里的顺序逐把尝试，第一把能通就用它。
    /// </summary>
    /// <param name="keys">key 库（<see cref="PluginKeySet"/>）；为空时直接返回失败。</param>
    /// <param name="baseUrl">接口地址，例如 <c>https://onliqiu.com/v1</c>。</param>
    /// <param name="modelsPath">模型列表路径，默认 <c>/models</c>。</param>
    public static async Task<ModelCatalogResult> FetchAsync(
        PluginKeySet keys,
        string? baseUrl,
        string? modelsPath,
        int timeoutSeconds = DefaultTimeoutSeconds,
        string userAgent = DefaultUserAgent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.IsEmpty)
            return ModelCatalogResult.Fail("这个插件没有配置任何 key，无法查询模型");

        if (string.IsNullOrWhiteSpace(baseUrl))
            return ModelCatalogResult.Fail("没有配置接口地址，无法查询模型");

        var url = BuildUrl(baseUrl, modelsPath);

        if (url is null)
            return ModelCatalogResult.Fail("接口地址不合法：" + baseUrl);

        ModelCatalogResult? last = null;

        foreach (var key in keys.Keys)
        {
            var one = await FetchOnceAsync(url, key, timeoutSeconds, userAgent, ct).ConfigureAwait(false);

            if (one.Ok)
                return one;

            last = one;

            // 只有「这把钥匙不行」才值得换下一把；其余错误换 key 也一样。
            if (one.StatusCode is not (401 or 403))
                return one;
        }

        return last ?? ModelCatalogResult.Fail("没有可用的 key");
    }

    /// <summary>单把 key 的一次请求（测试里直接调这个，注入假端点）。</summary>
    public static async Task<ModelCatalogResult> FetchOnceAsync(
        Uri url,
        PluginKey key,
        int timeoutSeconds,
        string userAgent,
        CancellationToken ct = default)
    {
        var started = Environment.TickCount64;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Value);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        HttpResponseMessage response;

        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ModelCatalogResult.Fail($"请求超时（{timeoutSeconds} 秒）：{url.Host}", elapsedMs: Elapsed(started));
        }
        catch (HttpRequestException ex)
        {
            return ModelCatalogResult.Fail("连不上 " + url.Host + "：" + ex.Message, elapsedMs: Elapsed(started));
        }

        using (response)
        {
            var body = await ReadLimitedAsync(response, MaxBodyBytes, cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status != 200)
            {
                return ModelCatalogResult.Fail(
                    Hint(status) ?? $"接口返回 HTTP {status}",
                    status,
                    elapsedMs: Elapsed(started));
            }

            List<string> models;

            try
            {
                models = ParseModels(body);
            }
            catch (JsonException)
            {
                // 200 但不是 JSON：多半是被反爬页面顶上来了。
                // 把前 120 字符带出去，够判断是"HTML 登录页"还是"空响应"。
                var head = body.Length > 120 ? body[..120] : body;
                return ModelCatalogResult.Fail(
                    "返回的不是 JSON（可能被反向代理挡了）：" + head.Replace('\n', ' '),
                    status,
                    elapsedMs: Elapsed(started));
            }

            if (models.Count == 0)
                return ModelCatalogResult.Fail("接口通了，但返回里没有任何模型 id", status, elapsedMs: Elapsed(started));

            return new ModelCatalogResult(true, models, null, status, key.Index, Elapsed(started));
        }
    }

    /// <summary>
    /// 从响应体里抽模型 id。兼容三种形状：
    /// <c>{"data":[{"id":"..."}]}</c>（OpenAI / onliqiu）、
    /// <c>{"models":[...]}</c>、以及 <c>[...]</c>（裸数组）。
    /// </summary>
    public static List<string> ParseModels(string json)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
            Collect(root, set);
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "data", "models", "items" })
            {
                if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    Collect(arr, set);
                    break;
                }
            }
        }

        return set.ToList();
    }

    private static void Collect(JsonElement array, SortedSet<string> set)
    {
        foreach (var item in array.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    Add(set, item.GetString());
                    break;

                case JsonValueKind.Object:
                    foreach (var field in new[] { "id", "name", "model" })
                    {
                        if (item.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                        {
                            Add(set, v.GetString());
                            break;
                        }
                    }
                    break;
            }
        }
    }

    private static void Add(SortedSet<string> set, string? value)
    {
        var v = value?.Trim();

        if (!string.IsNullOrEmpty(v))
            set.Add(v);
    }

    /// <summary>
    /// 拼地址。允许 <paramref name="baseUrl"/> 写到 <c>/v1</c>，
    /// 也容忍有人把整条 <c>/images/generations</c> 粘进来（老配置就是这么写的）。
    /// </summary>
    public static Uri? BuildUrl(string? baseUrl, string? modelsPath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var b = baseUrl.Trim().TrimEnd('/');

        // 老配置里形如 https://host/v1/images/generations —— 退回到 /v1
        const string imagesSegment = "/images/";
        int idx = b.IndexOf(imagesSegment, StringComparison.OrdinalIgnoreCase);

        if (idx > 0)
            b = b[..idx];

        var path = string.IsNullOrWhiteSpace(modelsPath) ? "/models" : modelsPath.Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;

        return Uri.TryCreate(b + path, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }

    private static string? Hint(int status) => status switch
    {
        401 => "key 无效（HTTP 401）。检查 .env 里的 key 是否粘全、有没有多一个空格",
        403 => "没有权限（HTTP 403）。可能是这把 key 没开通接口，也可能是被 Cloudflare 拦了",
        404 => "路径不对（HTTP 404）。接口地址一般写到 /v1 即可，不要写到 /models",
        429 => "被限速或额度用完（HTTP 429）",
        >= 500 => "上游出错（HTTP " + status + "），过一会儿再试",
        _ => null,
    };

    private static async Task<string> ReadLimitedAsync(
        HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        while (ms.Length < limit)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);

            if (read == 0)
                break;

            ms.Write(buffer, 0, (int)Math.Min(read, limit - ms.Length));
        }

        return ProcessEncoding.Decode(ms.ToArray(), null);
    }

    private static long Elapsed(long started) => Environment.TickCount64 - started;
}
