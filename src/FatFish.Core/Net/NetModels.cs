using System.Text.Json.Serialization;

namespace FatFish.Core.Net;

/// <summary>
/// 一次检索请求。
/// </summary>
/// <param name="Query">检索词（1~128 字符）。官方 API 会原样透传语法，可写 site: / lang: 等。</param>
/// <param name="Count">要几条，1~50。</param>
/// <param name="Days">只要最近 N 天的结果；null = 不限。会翻译成官方的 <c>after:YYYY-MM-DD</c>。</param>
/// <param name="NeedContent">是否让后端顺便带上正文（官方 <c>need_content</c>）。</param>
/// <param name="OriginalQuestion">用户的原话。官方用它做 query 改写/重排，能显著提升召回。</param>
public sealed record NetQuery(
    string Query,
    int Count = 5,
    int? Days = null,
    bool NeedContent = false,
    string? OriginalQuestion = null);

/// <summary>
/// 一条搜索结果（所有后端归一到这个形状）。
/// </summary>
/// <param name="Title">标题。</param>
/// <param name="Url">链接。</param>
/// <param name="Snippet">摘要。官方给的是 600~800 字的 summary，正好是给模型读的长度。</param>
/// <param name="Published">发布/更新日期（<c>yyyy-MM-dd</c>），未知为空串。</param>
/// <param name="Score">后端给的排序分。不同后端不可比，只在同一后端内可排序。</param>
/// <param name="Site">站点名。</param>
/// <param name="Content">正文（仅 <see cref="NetQuery.NeedContent"/> 时可能有）。</param>
public sealed record SearchHit(
    string Title,
    string Url,
    string Snippet,
    string Published = "",
    double? Score = null,
    string Site = "",
    string? Content = null)
{
    /// <summary>融合时用的规范键：同一篇文章被两个后端返回时应合并。</summary>
    [JsonIgnore]
    public string UrlKey => NetUrl.Key(Url);
}

/// <summary>
/// 一条抓取结果。
/// </summary>
public sealed record FetchHit(string Url, string Title, string Content, string? Error = null);

/// <summary>
/// 联网层的统一异常。
///
/// 与 <see cref="Llm.LlmException"/> 分开：联网失败不该让用户以为「模型挂了」——
/// 两者的处置完全不同（换个后端 vs 检查网络/余额）。
/// </summary>
public sealed class NetException : Exception
{
    public NetException(string message, string? provider = null, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Provider = provider;
        StatusCode = statusCode;
    }

    public string? Provider { get; }

    /// <summary>HTTP 状态码（非 HTTP 失败时为 null）。</summary>
    public int? StatusCode { get; }

    /// <summary>该失败值不值得换一个后端重试。</summary>
    /// <remarks>
    /// 402（余额不足）/ 403（被安全策略拒绝）/ 401（key 无效）换后端有意义；
    /// 429（限速）与 5xx 换后端也有意义；408/504 同理。
    /// 而「query 为空」这种纯参数错误换谁都一样 —— 重试只会多花一次钱。
    /// </remarks>
    public bool IsRetryableOnOtherBackend =>
        StatusCode is null or 401 or 402 or 403 or 408 or 429 or >= 500;
}

/// <summary>URL 归一化 —— 融合去重、缓存键都靠它。</summary>
public static class NetUrl
{
    /// <summary>跟踪参数：同一篇文章带不带它们都是同一篇。</summary>
    private static readonly string[] TrackingParams =
    [
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "spm", "from", "ref", "ref_src", "share_token", "scene",
    ];

    /// <summary>
    /// 把 URL 归一成可比较的键。
    ///
    /// 规则刻意保守：**只削掉肯定不影响内容的部分**（协议、主机大小写、末尾斜杠、
    /// 跟踪参数、#片段）。不动查询参数主序，也不做「猜 canonical」——
    /// 归一过头会把两篇不同的文章合成一条，那种错误无法通过日志发现。
    /// </summary>
    public static string Key(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var s = url.Trim();

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
            return s.ToLowerInvariant();

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
            path = "/";

        var kept = new List<string>();

        // ★ 用 uri.Query 手工解析而不是 HttpUtility.ParseQueryString：
        //   Core 不想为了这点事引入 System.Web 或第三方包。
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];

            if (Array.IndexOf(TrackingParams, name.ToLowerInvariant()) >= 0)
                continue;

            kept.Add(pair);
        }

        var query = kept.Count > 0 ? "?" + string.Join('&', kept) : string.Empty;

        return $"{host}{path}{query}";
    }

    /// <summary>只保留 http/https 且主机名非空的 URL。</summary>
    public static bool IsHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && uri.Host.Length > 0;
    }

    /// <summary>给模型看的短链接（超长 URL 会白白吃掉几百 token）。</summary>
    public static string Short(string url, int max = 120)
        => url.Length <= max ? url : url[..(max - 1)] + "…";
}
