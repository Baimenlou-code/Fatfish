using System.Globalization;
using System.Text.Json;

namespace FatFish.Core.Net;

/// <summary>余额的紧张程度。</summary>
public enum BalanceLevel
{
    /// <summary>查不到（没配 key / 网络失败）。</summary>
    Unknown,

    /// <summary>充足。</summary>
    Ok,

    /// <summary>偏紧（DeepSeek &lt; 20 元 / Tavily &lt; 20%）。</summary>
    Warn,

    /// <summary>吃紧（DeepSeek &lt; 5 元 / Tavily &lt; 5%）。</summary>
    Crit,

    /// <summary>见底（0）。</summary>
    Dead,
}

/// <summary>DeepSeek 账户余额。</summary>
public sealed record DeepSeekBalance(
    bool Ok,
    string Currency,
    double Total,
    double Granted,
    double ToppedUp,
    bool IsAvailable,
    string KeyMask,
    string? Error,
    DateTimeOffset Ts);

/// <summary>Tavily 用量（含额度池与 key 级两道上限）。</summary>
/// <param name="Left">实际还能用多少 = min(计划池剩余, key 剩余)。</param>
/// <param name="LeftPct">计划池剩余百分比。</param>
public sealed record TavilyUsage(
    bool Ok,
    string Plan,
    int PlanLimit,
    int PlanUsed,
    int KeyLimit,
    int KeyUsed,
    int? Left,
    double? LeftPct,
    int SearchUsage,
    int ExtractUsage,
    string KeyMask,
    string? Error,
    DateTimeOffset Ts);

/// <summary>一次余额查询的完整快照。</summary>
public sealed record BalanceSnapshot(
    DeepSeekBalance DeepSeek,
    TavilyUsage Tavily,
    DateTimeOffset Ts)
{
    /// <summary>
    /// 用给定服务的阈值分级。
    /// </summary>
    /// <remarks>
    /// ★ 分级必须由 <see cref="BalanceService"/> 来算，不能在 Snapshot 里做成属性：
    ///   阈值（&lt;20 元偏紧 / &lt;5 元吃紧 / Tavily &lt;20%）是**可被环境变量覆盖**的，
    ///   而 Snapshot 只是一份数据、拿不到那些阈值。
    ///   做成属性就只能写死默认值，于是「改了 .env 的阈值但界面不变」——
    ///   这种问题没人会联想到「属性里写死了」。
    /// </remarks>
    public (BalanceLevel DeepSeek, BalanceLevel Tavily, BalanceLevel Overall) Grade(BalanceService svc)
    {
        var ds = svc.GradeDeepSeek(DeepSeek);
        var tv = svc.GradeTavily(Tavily);

        return (ds, tv, (BalanceLevel)Math.Max((int)ds, (int)tv));
    }
}

/// <summary>
/// 余额与额度查询。
///
/// 两个端点、两套认证方式（这是最容易配错的地方）：
///   DeepSeek: GET {base}/user/balance   头 <c>Authorization: Bearer &lt;key&gt;</c>
///   Tavily  : GET https://api.tavily.com/usage  头 <c>Authorization: Bearer &lt;key&gt;</c>
///
/// ★ 注意 Tavily 的两套写法：搜索/抓取的 api_key 放在 JSON body 里（老设计），
///   而 /usage 只认 Bearer 头。同一个 key、同一台服务器、两种传法 ——
///   照着搜索的写法去查余额，会拿到 401，而且错误信息里完全看不出「是传法错了」。
///
/// ★ Tavily 的两道上限必须同时看：
///   account.plan_limit - plan_usage  计划池还剩多少
///   key.limit - key.usage            这把 key 还剩多少
///   实际可用 = 两者取小。只看计划池会误判成「还有很多」，
///   而真正卡住的往往是 key 级上限（团队里别人已经用掉了）。
/// </summary>
public sealed class BalanceService
{
    private readonly HttpClient _http;

    public BalanceService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    }

    /// <summary>阈值：低于这个数（元）算偏紧。</summary>
    public double WarnYuan { get; init; } = ReadDouble("NETBAL_DS_WARN", 20);

    /// <summary>阈值：低于这个数（元）算吃紧。</summary>
    public double CritYuan { get; init; } = ReadDouble("NETBAL_DS_CRIT", 5);

    /// <summary>阈值：Tavily 剩余百分比低于此算偏紧。</summary>
    public double TavilyWarnPct { get; init; } = ReadDouble("NETBAL_TAVILY_WARN", 20);

    /// <summary>阈值：Tavily 剩余百分比低于此算吃紧。</summary>
    public double TavilyCritPct { get; init; } = ReadDouble("NETBAL_TAVILY_CRIT", 5);

    public int TimeoutSeconds { get; init; } = (int)ReadDouble("NETBAL_TIMEOUT", 12);

    /// <summary>余额快照的缓存时长（秒）。默认 60，与 Python 版 NETBAL_CACHE_TTL 一致。</summary>
    public int CacheTtlSeconds { get; init; } = (int)ReadDouble("NETBAL_CACHE_TTL", 60);

    private static readonly object CacheGate = new();
    private static BalanceSnapshot? _cache;
    private static DateTimeOffset _cacheAt;

    public async Task<BalanceSnapshot> FetchAsync(bool force = false, CancellationToken ct = default)
    {
        // ★ 进程级缓存（默认 60 秒）。
        //   为什么必须要：Tavily 的 /usage 端点对调用频率限制很紧，
        //   连续查几次就 429；而状态栏启动时会查一次、用户可能再点几次。
        //   没有缓存的话，表现是「余额一会儿有一会儿没有」，看着像接口不稳定。
        var ttl = TimeSpan.FromSeconds(Math.Max(0, CacheTtlSeconds));

        if (!force && ttl > TimeSpan.Zero)
        {
            lock (CacheGate)
            {
                if (_cache is not null && DateTimeOffset.UtcNow - _cacheAt < ttl)
                    return _cache;
            }
        }

        var dsTask = FetchDeepSeekAsync(ct);
        var tvTask = FetchTavilyAsync(ct);

        await Task.WhenAll(dsTask, tvTask).ConfigureAwait(false);

        var snap = new BalanceSnapshot(dsTask.Result, tvTask.Result, DateTimeOffset.UtcNow);

        lock (CacheGate)
        {
            _cache = snap;
            _cacheAt = snap.Ts;
        }

        return snap;
    }

    // ------------------------------------------------------------------

    public async Task<DeepSeekBalance> FetchDeepSeekAsync(CancellationToken ct = default)
    {
        var mask = NetOptions.Mask(GetKey("FATFISH_API_KEY", "DEEPSEEK_API_KEY"));

        if (string.IsNullOrWhiteSpace(GetKey("FATFISH_API_KEY", "DEEPSEEK_API_KEY")))
            return Fail("未找到 DeepSeek key（FATFISH_API_KEY / DEEPSEEK_API_KEY）", mask);

        var baseUrl = DeepSeekNetProvider.NormalizeBase(
            GetEnv("FATFISH_BASE_URL", "DEEPSEEK_BASE_URL") ?? "https://api.deepseek.com");

        var url = baseUrl + "/user/balance";

        try
        {
            using var doc = await GetJsonAsync(url, GetKey("FATFISH_API_KEY", "DEEPSEEK_API_KEY")!, ct)
                .ConfigureAwait(false);

            var root = doc.RootElement;
            var isAvailable = root.TryGetProperty("is_available", out var av)
                              && av.ValueKind is JsonValueKind.True;

            JsonElement pick = default;
            var found = false;

            if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in infos.EnumerateArray())
                {
                    var cur = Str(b, "currency") ?? string.Empty;

                    if (cur.Equals("CNY", StringComparison.OrdinalIgnoreCase))
                    {
                        pick = b;
                        found = true;
                        break;
                    }

                    if (!found)
                    {
                        pick = b;
                        found = true;
                    }
                }
            }

            double Num(string name)
                => pick.ValueKind == JsonValueKind.Object && pick.TryGetProperty(name, out var v)
                   && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d
                    : 0;

            return new DeepSeekBalance(
                Ok: true,
                Currency: found ? Str(pick, "currency") ?? "CNY" : "CNY",
                Total: Num("total_balance"),
                Granted: Num("granted_balance"),
                ToppedUp: Num("topped_up_balance"),
                IsAvailable: isAvailable,
                KeyMask: mask,
                Error: null,
                Ts: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, mask);
        }

        static DeepSeekBalance Fail(string error, string mask) => new(
            Ok: false, Currency: "CNY", Total: 0, Granted: 0, ToppedUp: 0,
            IsAvailable: false, KeyMask: mask, Error: error, Ts: DateTimeOffset.UtcNow);
    }

    public async Task<TavilyUsage> FetchTavilyAsync(CancellationToken ct = default)
    {
        var key = GetKey("TAVILY_API_KEY");
        var mask = NetOptions.Mask(key);

        if (string.IsNullOrWhiteSpace(key))
            return Fail("未找到 TAVILY_API_KEY", mask);

        try
        {
            using var doc = await GetJsonAsync("https://api.tavily.com/usage", key!, ct)
                .ConfigureAwait(false);

            var root = doc.RootElement;

            var k = root.TryGetProperty("key", out var ke) ? ke : default;
            var a = root.TryGetProperty("account", out var ac) ? ac : default;

            var planLimit = Int(a, "plan_limit");
            var planUsed = Int(a, "plan_usage");
            var keyLimit = Int(k, "limit");
            var keyUsed = Int(k, "usage");

            int? byPlan = planLimit > 0 ? Math.Max(0, planLimit - planUsed) : null;
            int? byKey = keyLimit > 0 ? Math.Max(0, keyLimit - keyUsed) : null;

            int? left = (byPlan, byKey) switch
            {
                (null, null) => null,
                (null, _) => byKey,
                (_, null) => byPlan,
                _ => Math.Min(byPlan!.Value, byKey!.Value),
            };

            double? pct = planLimit > 0
                ? Math.Round(100.0 * Math.Max(0, planLimit - planUsed) / planLimit, 1)
                : null;

            return new TavilyUsage(
                Ok: true,
                Plan: Str(a, "current_plan") ?? "?",
                PlanLimit: planLimit,
                PlanUsed: planUsed,
                KeyLimit: keyLimit,
                KeyUsed: keyUsed,
                Left: left,
                LeftPct: pct,
                SearchUsage: Int(a, "search_usage"),
                ExtractUsage: Int(a, "extract_usage"),
                KeyMask: mask,
                Error: null,
                Ts: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, mask);
        }

        static TavilyUsage Fail(string error, string mask) => new(
            Ok: false, Plan: "?", PlanLimit: 0, PlanUsed: 0, KeyLimit: 0, KeyUsed: 0,
            Left: null, LeftPct: null, SearchUsage: 0, ExtractUsage: 0,
            KeyMask: mask, Error: error, Ts: DateTimeOffset.UtcNow);
    }

    // ------------------------------------------------------------------
    // 分级
    // ------------------------------------------------------------------

    public static BalanceLevel GradeDeepSeek(DeepSeekBalance b, double warn = 20, double crit = 5)
    {
        if (!b.Ok)
            return BalanceLevel.Unknown;

        if (b.Total <= 0)
            return BalanceLevel.Dead;

        if (b.Total < crit)
            return BalanceLevel.Crit;

        return b.Total < warn ? BalanceLevel.Warn : BalanceLevel.Ok;
    }

    public BalanceLevel GradeDeepSeek(DeepSeekBalance b) => GradeDeepSeek(b, WarnYuan, CritYuan);

    public BalanceLevel GradeTavily(TavilyUsage t) => GradeTavily(t, TavilyWarnPct, TavilyCritPct);

    /// <summary>
    /// Tavily 的分级。
    /// </summary>
    /// <remarks>
    /// ★ 用百分比起判，不用绝对数：1000 credits 的 20% 与 10000 credits 的 20%
    ///   是完全不同的绝对量，但「还剩 20%」这个体感是一样的 —— 而用户真正需要感知
    ///   的正是「这个月还够不够用」。
    /// </remarks>
    public static BalanceLevel GradeTavily(TavilyUsage t, double warnPct = 20, double critPct = 5)
    {
        if (!t.Ok || t.LeftPct is null)
            return BalanceLevel.Unknown;

        if (t.Left is 0)
            return BalanceLevel.Dead;

        if (t.LeftPct < critPct)
            return BalanceLevel.Crit;

        return t.LeftPct < warnPct ? BalanceLevel.Warn : BalanceLevel.Ok;
    }

    public static string LevelIcon(BalanceLevel lv) => lv switch
    {
        BalanceLevel.Ok => "✅",
        BalanceLevel.Warn => "🟡",
        BalanceLevel.Crit => "🔴",
        BalanceLevel.Dead => "⛔",
        _ => "⚪",
    };

    public static string LevelText(BalanceLevel lv) => lv switch
    {
        BalanceLevel.Ok => "充足",
        BalanceLevel.Warn => "偏紧",
        BalanceLevel.Crit => "吃紧",
        BalanceLevel.Dead => "见底",
        _ => "未知",
    };

    /// <summary>一行摘要（状态栏用）。</summary>
    public static string OneLine(BalanceSnapshot s)
    {
        var ds = s.DeepSeek.Ok
            ? $"DS ¥{s.DeepSeek.Total:0.00}"
            : "DS ?";

        var tv = s.Tavily.Ok && s.Tavily.Left is { } left
            ? $"TV {left} ({s.Tavily.LeftPct:0.#}%)"
            : "TV ?";

        return $"{ds}  {tv}";
    }

    // ------------------------------------------------------------------

    private async Task<JsonDocument> GetJsonAsync(string url, string key, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, TimeoutSeconds)));

        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // ★ 两种认证写法都带上：
        //   Authorization 是 /usage 与 /user/balance 的正规写法，
        //   api_key 查询参数是 Tavily 老版接口的写法。
        //   同时提供不会造成歧义（服务器取自己能认的那个）。
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "FatFish-Net/1.0");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
            .ConfigureAwait(false);

        var raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = raw.Length > 200 ? raw[..200] : raw;

            // ★ 429 单独说清楚：Tavily 的 /usage 限流很紧（与搜索配额是两套），
            //   而「HTTP 429」翻译不成行动 —— 用户会去查额度，实际只是查得太勤。
            var hint = (int)resp.StatusCode == 429
                ? "查询太频繁（该端点有独立限流），稍等几十秒再查"
                : $"HTTP {(int)resp.StatusCode}";

            throw new NetException($"{hint}：{body}", statusCode: (int)resp.StatusCode);
        }

        return JsonDocument.Parse(raw);
    }

    private static string? Str(JsonElement obj, string name)
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

    private static int Int(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
            return 0;

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var v) => v,
            JsonValueKind.String when int.TryParse(el.GetString(), out var v) => v,
            _ => 0,
        };
    }

    private static string? GetEnv(params string[] names)
    {
        foreach (var n in names)
        {
            var v = Environment.GetEnvironmentVariable(n);
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return null;
    }

    private static string? GetKey(params string[] names) => GetEnv(names);

    private static double ReadDouble(string name, double fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);

        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : fallback;
    }
}
