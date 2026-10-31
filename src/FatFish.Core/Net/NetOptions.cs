using System.Globalization;

namespace FatFish.Core.Net;

/// <summary>
/// 联网层配置。
///
/// 取值优先级与内核其它部分一致：<c>FATFISH_*</c> → 旧写法（<c>DEEPSEEK_*</c> / <c>TAVILY_*</c>）→ 默认。
///
/// ★ 为什么 <see cref="Backends"/> 的默认值是空串而不是 <c>auto</c>：
///   空串 = 「不启用多后端，就走单后端」。这样老部署（只配了 TAVILY_API_KEY）
///   的行为与 Python 版完全一致；想开多后端必须显式写 <c>NET_BACKENDS=auto</c>。
///   如果默认 auto，那么只配了 DeepSeek key 的机器会突然开始走官方按次计费的
///   Search/Fetch —— 一个「升级后账单变了」的问题，而且没人会想到是这里。
/// </summary>
public sealed class NetOptions
{
    public string? DeepSeekApiKey { get; init; }

    public string DeepSeekBaseUrl { get; init; } = "https://api.deepseek.com";

    public string? TavilyApiKey { get; init; }

    /// <summary>搜索后端多选。""=单后端 / "auto"=所有可用的 / "deepseek,tavily"=指定。</summary>
    public string Backends { get; init; } = string.Empty;

    /// <summary>多后端时用哪个路由策略决定「这次用哪几个」："rule" / "off"。</summary>
    public string Router { get; init; } = "rule";

    /// <summary>多后端结果如何融合："rrf" / "score" / "none"。</summary>
    public string Merge { get; init; } = "rrf";

    /// <summary>官方 API 的本地限速（QPS）。官方限制 Search/Fetch 各 5 QPS（账号级）。</summary>
    public double Qps { get; init; } = 5;

    /// <summary>单次 HTTP 超时（秒）。</summary>
    public int TimeoutSeconds { get; init; } = 20;

    /// <summary>单条摘要的字符上限。</summary>
    public int SnippetLimit { get; init; } = 900;

    /// <summary>单次抓取的正文长度上限。</summary>
    public int FetchMaxChars { get; init; } = 8_000;

    public bool HasDeepSeek => !string.IsNullOrWhiteSpace(DeepSeekApiKey);

    public bool HasTavily => !string.IsNullOrWhiteSpace(TavilyApiKey);

    public bool AnyProvider => HasDeepSeek || HasTavily;

    /// <summary>
    /// 解析 <see cref="Backends"/>：
    ///   <c>null</c> = auto（所有可用的都上）
    ///   <c>[]</c>   = 未启用多后端（走单后端）
    ///   <c>[a,b]</c> = 指定
    /// </summary>
    /// <remarks>
    /// ★ 这里 <c>null</c> 与 <c>[]</c> 的区分是整个多后端开关的基石，
    ///   调用方必须用 <c>is null</c> 判断，不能写 <c>if (!spec.Any())</c> ——
    ///   Python 版就因为 `not spec` 把 auto 误判成「未启用」，注释里专门记了这条。
    /// </remarks>
    public IReadOnlyList<string>? ResolveBackends()
    {
        var raw = (Backends ?? string.Empty).Trim();

        if (raw.Length == 0)
            return Array.Empty<string>();

        if (raw.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("all", StringComparison.OrdinalIgnoreCase)
            || raw == "*")
            return null;

        return raw
            .Split([',', '，', '、', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static NetOptions FromEnvironment()
    {
        var culture = CultureInfo.InvariantCulture;

        double GetDouble(double fallback, params string[] names)
        {
            foreach (var n in names)
            {
                var v = Environment.GetEnvironmentVariable(n);
                if (double.TryParse(v, NumberStyles.Float, culture, out var d))
                    return d;
            }

            return fallback;
        }

        int GetInt(int fallback, params string[] names)
        {
            foreach (var n in names)
            {
                var v = Environment.GetEnvironmentVariable(n);
                if (int.TryParse(v, NumberStyles.Integer, culture, out var i))
                    return i;
            }

            return fallback;
        }

        string? Get(params string[] names)
        {
            foreach (var n in names)
            {
                var v = Environment.GetEnvironmentVariable(n);
                if (!string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }

            return null;
        }

        return new NetOptions
        {
            // DeepSeek 的 key 与 LLM 共用：能聊天就能搜，不需要第二份配置。
            DeepSeekApiKey = Get("FATFISH_API_KEY", "DEEPSEEK_API_KEY"),
            DeepSeekBaseUrl = Get("FATFISH_BASE_URL", "DEEPSEEK_BASE_URL") ?? "https://api.deepseek.com",
            TavilyApiKey = Get("TAVILY_API_KEY"),
            Backends = Get("NET_BACKENDS", "FATFISH_NET_BACKENDS") ?? string.Empty,
            Router = Get("NET_BACKEND_ROUTER", "FATFISH_NET_ROUTER") ?? "rule",
            Merge = Get("NET_MERGE", "FATFISH_NET_MERGE") ?? "rrf",
            Qps = GetDouble(5, "DEEPSEEK_SEARCH_QPS", "FATFISH_NET_QPS"),
            TimeoutSeconds = GetInt(20, "SEARCH_TIMEOUT", "FATFISH_NET_TIMEOUT"),
            SnippetLimit = GetInt(900, "SEARCH_SNIPPET_LIMIT", "FATFISH_NET_SNIPPET_LIMIT"),
            FetchMaxChars = GetInt(8_000, "FATFISH_NET_FETCH_CHARS"),
        };
    }

    /// <summary>日志用：绝不打印 key 本体。</summary>
    public string Describe()
    {
        var ks = new List<string>();
        if (HasDeepSeek) ks.Add("deepseek:" + Mask(DeepSeekApiKey!));
        if (HasTavily) ks.Add("tavily:" + Mask(TavilyApiKey!));

        return $"backends={(Backends.Length == 0 ? "(单后端)" : Backends)} " +
               $"router={Router} merge={Merge} qps={Qps} timeout={TimeoutSeconds}s " +
               $"keys=[{string.Join(" ", ks)}]";
    }

    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return "(无)";

        // 只露尾 4 位：前缀往往能看出是哪个平台，露出来没坏处，但中间那段是秘密。
        return key.Length <= 8 ? "****" : $"***{key[^4..]}";
    }
}
