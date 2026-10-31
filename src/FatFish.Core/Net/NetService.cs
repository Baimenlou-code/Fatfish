using System.Diagnostics;

namespace FatFish.Core.Net;

/// <summary>一次搜索的完整结果。</summary>
public sealed record NetSearchResult(
    string Text,
    IReadOnlyList<SearchHit> Hits,
    IReadOnlyList<string> Backends,
    long ElapsedMs,
    RouteDecision Route,
    IReadOnlyList<string> Failures)
{
    public bool Ok => Hits.Count > 0;
}

/// <summary>一次抓取的完整结果。</summary>
public sealed record NetFetchResult(
    string Text,
    IReadOnlyList<FetchHit> Hits,
    long ElapsedMs,
    IReadOnlyList<string> Failures)
{
    public bool Ok => Hits.Any(h => h.Error is null);
}

/// <summary>
/// 联网门面 —— 上层（工具、界面）只需要认识这一个类。
///
/// 职责链：
///   1. 按配置/参数决定用哪些后端（<see cref="NetRouter"/>）；
///   2. 并发调用（多个后端各发一次）；
///   3. 融合去重（RRF）；
///   4. 记账（<see cref="UsageLedger"/>）；
///   5. 排版成给模型读的文本。
///
/// ★ 为什么 step 2 要并发：两个后端各 1~2 秒，串行就是 4 秒。
///   而用户体感里「搜索慢」几乎全部来自这里。并发下总耗时 ≈ 最慢的那个。
///   ★ 但**限速桶不能共享**：每个 provider 自己持有，否则并发会互相挤令牌，
///   两个后端都被压到 2.5 QPS，等于既慢又容易超时。
/// </summary>
public sealed class NetService : IDisposable
{
    /// <summary>
    /// 进程级共享 HttpClient。
    /// </summary>
    /// <remarks>
    /// ★ 必须共享，不能每次 new：<see cref="HttpClient"/> 每次 new 都会建一份连接池，
    ///   而连接关闭后 TCP 端口还要在 TIME_WAIT 挂 4 分钟。一个会话里搜几十次，
    ///   就会开始出现「连接被重置 / 无法连接远程服务器」——这种症状看起来像网络问题，
    ///   实际是本机端口耗尽，排查方向会完全错。
    ///   ★ Timeout 设为无限：超时由每个请求自己的链接取消令牌管，
    ///   这样才能区分「这次请求超时」和「用户取消了」。
    /// </remarks>
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly NetOptions _options;
    private readonly UsageLedger? _ledger;
    private readonly NetRouter _router;
    private readonly Dictionary<string, INetProvider> _providers = new(StringComparer.Ordinal);

    public NetService(NetOptions options, UsageLedger? ledger = null, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ledger = ledger;
        _router = new NetRouter(ledger);

        http ??= SharedHttp;

        // 只装配配置齐全的后端：缺 key 的后端留在字典里是「看起来可用、一用就报错」
        if (options.HasDeepSeek)
            _providers["deepseek"] = new DeepSeekNetProvider(options, http);

        if (options.HasTavily)
            _providers["tavily"] = new TavilyNetProvider(options, http);
    }

    /// <summary>从环境变量装配（工具层用这个）。</summary>
    public static NetService FromEnvironment(UsageLedger? ledger = null)
        => new(NetOptions.FromEnvironment(), ledger);

    public NetOptions Options => _options;

    /// <summary>配置齐全的后端名（按字母序）。</summary>
    public IReadOnlyList<string> AvailableBackends
        => _providers.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// <summary>是否有任何可用后端。</summary>
    public bool AnyAvailable => _providers.Count > 0;

    /// <summary>支持抓正文的后端。</summary>
    public IReadOnlyCollection<string> FetchCapableBackends
        => _providers.Where(kv => kv.Value.SupportsFetch).Select(kv => kv.Key).ToList();

    // ------------------------------------------------------------------
    // 搜索
    // ------------------------------------------------------------------

    public async Task<NetSearchResult> SearchAsync(
        string query,
        int count = 5,
        int? days = null,
        IReadOnlyList<string>? backends = null,
        string? question = null,
        CancellationToken ct = default)
    {
        if (!AnyAvailable)
        {
            throw new NetException(
                "没有可用的搜索后端。请配置 TAVILY_API_KEY，或确保 FATFISH_API_KEY / DEEPSEEK_API_KEY 有效。");
        }

        if (string.IsNullOrWhiteSpace(query))
            throw new NetException("query 不能为空");

        var sw = Stopwatch.StartNew();

        var picked = PickBackends(backends, question ?? query, wantExtract: false);

        if (picked.IsEmpty)
            throw new NetException("没有匹配到可用后端（检查 NET_BACKENDS 配置）");

        var netQuery = new NetQuery(query.Trim(), count, days, false, question);

        // 并发调用；每个后端自己记账、自己吞异常
        var tasks = picked.Backends.Select(name => SearchOneAsync(name, netQuery, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var failures = results.Where(r => r.Error is not null)
            .Select(r => $"{r.Name}：{r.Error}")
            .ToList();

        var perBackend = new Dictionary<string, IReadOnlyList<SearchHit>>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if (r.Hits.Count > 0)
                perBackend[r.Name] = r.Hits;
        }

        if (perBackend.Count == 0)
        {
            var detail = failures.Count > 0 ? string.Join("；", failures) : "所有后端都没搜到结果";
            throw new NetException("搜索失败 —— " + detail);
        }

        var merged = NetRouter.Merge(perBackend, _options.Merge, limit: Math.Clamp(count, 1, 20));

        var text = NetRouter.Format(merged, Math.Clamp(count, 1, 20));

        if (failures.Count > 0)
            text += "\n\n（注意：以下后端本次失败，结果仅来自 " +
                    string.Join("、", perBackend.Keys) + " —— " + string.Join("；", failures) + "）";

        text += "\n\n（以上内容来自公开网页，未经核实；引用时请注明来源。）";

        sw.Stop();

        return new NetSearchResult(
            text,
            merged,
            perBackend.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            sw.ElapsedMilliseconds,
            picked,
            failures);
    }

    private async Task<(string Name, IReadOnlyList<SearchHit> Hits, string? Error)> SearchOneAsync(
        string name,
        NetQuery query,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var hits = await _providers[name].SearchAsync(query, ct).ConfigureAwait(false);

            sw.Stop();

            // 官方计费口径：成功且结果非空才记 1 次。Tavily 用 credit，同样是 1 条搜索 = 1。
            var units = hits.Count > 0 ? 1 : 0;

            _ledger?.Record("search", name, query.Query, hits.Count > 0, hits.Count,
                units, sw.ElapsedMilliseconds);

            return (name, hits, null);
        }
        catch (NetException ex)
        {
            sw.Stop();
            _ledger?.Record("search", name, query.Query, false, 0, 0, sw.ElapsedMilliseconds, ex.Message);
            return (name, Array.Empty<SearchHit>(), ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _ledger?.Record("search", name, query.Query, false, 0, 0, sw.ElapsedMilliseconds, ex.Message);
            return (name, Array.Empty<SearchHit>(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 抓取
    // ------------------------------------------------------------------

    public async Task<NetFetchResult> FetchAsync(
        IReadOnlyList<string> urls,
        int maxChars = 0,
        IReadOnlyList<string>? backends = null,
        string? question = null,
        CancellationToken ct = default)
    {
        if (!AnyAvailable)
        {
            throw new NetException(
                "没有可用的抓取后端。请配置 TAVILY_API_KEY，或确保 FATFISH_API_KEY / DEEPSEEK_API_KEY 有效。");
        }

        var clean = (urls ?? Array.Empty<string>())
            .Where(NetUrl.IsHttpUrl)
            .Select(u => u.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (clean.Count == 0)
            throw new NetException("没有可抓取的 URL（只接受 http/https 链接）");

        var sw = Stopwatch.StartNew();

        var picked = PickBackends(backends, question ?? clean[0], wantExtract: true);

        if (picked.IsEmpty)
            throw new NetException("没有支持抓正文的可用后端");

        // 只用一个后端：多个后端抓同一批 URL 是纯粹的重复计费
        var name = picked.Backends[0];
        var limit = maxChars > 0 ? maxChars : _options.FetchMaxChars;

        IReadOnlyList<FetchHit> hits;
        string? error = null;

        try
        {
            hits = await _providers[name].FetchAsync(clean, limit, ct).ConfigureAwait(false);
        }
        catch (NetException ex)
        {
            sw.Stop();
            _ledger?.Record("fetch", name, clean[0], false, 0, 0, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }

        sw.Stop();

        var okHits = hits.Where(h => h.Error is null).ToList();
        var failures = hits.Where(h => h.Error is not null)
            .Select(h => $"{NetUrl.Short(h.Url, 60)}：{h.Error}")
            .ToList();

        // Fetch 按成功页面计费：抓 5 页成功 3 页 = 3 次
        _ledger?.Record("fetch", name, clean[0], okHits.Count > 0, okHits.Count,
            okHits.Count, sw.ElapsedMilliseconds, error);

        var text = FormatFetch(hits, limit);

        return new NetFetchResult(text, hits, sw.ElapsedMilliseconds, failures);
    }

    private static string FormatFetch(IReadOnlyList<FetchHit> hits, int maxChars)
    {
        if (hits.Count == 0)
            return "（未抓到内容）";

        var sb = new System.Text.StringBuilder(2048);

        var i = 0;
        foreach (var h in hits)
        {
            i++;

            if (h.Error is not null)
            {
                sb.Append(i).Append(". ").Append(NetUrl.Short(h.Url)).Append('\n');
                sb.Append("   ⚠ 抓取失败：").Append(h.Error).Append('\n');
                continue;
            }

            sb.Append(i).Append(". ").Append(h.Title.Length > 0 ? h.Title : h.Url).Append('\n');
            sb.Append("   来源：").Append(h.Url).Append('\n');

            var body = h.Content;

            // 单页正文塞太满会把上下文挤爆，而且模型读长网页的边际收益很低。
            // 这里按 maxChars 再兜一次底（provider 已经截过一次，但那是按它自己的上限）。
            if (body.Length > maxChars)
                body = body[..maxChars] + "\n…（已截断）";

            sb.Append(body).Append("\n\n");
        }

        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------
    // 选后端
    // ------------------------------------------------------------------

    private RouteDecision PickBackends(
        IReadOnlyList<string>? explicitBackends,
        string question,
        bool wantExtract)
    {
        // 1) 调用方显式指定（工具参数）→ 严格照办，不做路由。
        //    理由：用户/模型说「只用 tavily」时，替它再挑一个是违背意图的。
        if (explicitBackends is { Count: > 0 })
        {
            var allowed = explicitBackends
                .Select(x => x.Trim().ToLowerInvariant())
                .Where(_providers.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (allowed.Count == 0)
            {
                return new RouteDecision(
                    Array.Empty<string>(),
                    "指定的后端都不可用（可用：" + string.Join("、", AvailableBackends) + "）");
            }

            return new RouteDecision(allowed, "按调用方指定");
        }

        var spec = _options.ResolveBackends();

        // 2) 未启用多后端 → 单后端。Tavily 优先，与 Python 版行为一致。
        if (spec is { Count: 0 })
        {
            var single = wantExtract
                ? AvailableBackends.FirstOrDefault(b => _providers[b].SupportsFetch)
                : null;

            single ??= _providers.ContainsKey("tavily")
                ? "tavily"
                : AvailableBackends.Count > 0 ? AvailableBackends[0] : null;

            return single is null
                ? new RouteDecision(Array.Empty<string>(), "没有可用后端")
                : new RouteDecision([single], "单后端模式（NET_BACKENDS 未启用）");
        }

        // 3) auto / 指定列表
        var candidates = spec is null
            ? AvailableBackends
            : spec.Where(_providers.ContainsKey).ToList();

        if (candidates.Count == 0)
            return new RouteDecision(Array.Empty<string>(), "配置的后端都不可用");

        if (candidates.Count == 1)
            return new RouteDecision(candidates, "只有一个可用后端");

        // 4) 路由关闭 → 全上
        if (string.Equals(_options.Router, "off", StringComparison.OrdinalIgnoreCase))
            return new RouteDecision(candidates, "路由已关闭，全部后端都上");

        return _router.Route(
            question,
            candidates,
            wantExtract,
            maxN: 2,
            extractCapable: FetchCapableBackends);
    }

    public void Dispose()
    {
        // 共享 HttpClient 由进程持有，不在这里释放（放了会让后续请求全部失败）。
        // 实现 IDisposable 只是为了「调用方用 using 时不必知道这个细节」。
    }
}
