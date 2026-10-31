using System.Text;

namespace FatFish.Core.Net;

/// <summary>路由决策结果。</summary>
/// <param name="Backends">这次用哪几个。</param>
/// <param name="Why">人读的决策理由（会写进日志/结果，便于排查「为什么只搜了一个后端」）。</param>
public sealed record RouteDecision(IReadOnlyList<string> Backends, string Why)
{
    public bool IsEmpty => Backends.Count == 0;
}

/// <summary>
/// 后端路由 + 结果融合。
///
/// 路由回答「这次用哪几个后端」，融合回答「几个后端的结果怎么并成一份」。
/// 两者都放在这里，因为它们必须一起考虑：融合质量取决于后端是否互补，
/// 而「选两个搜索习惯完全一样」的后端只是白花一倍钱。
///
/// ★ 与 Python 版的最大差异：这里用 <see cref="UsageLedger"/> 的历史成功率来调权。
///   Python 版调权用的是 quality.py 的 SQLite 台账；C# 版把台账简化成了 JSONL
///   （见 UsageLedger 里的取舍说明），但**用台账反哺路由**这件事保留了 ——
///   这是「记账」最容易被忽略的用途：它不只是给用户看花了多少钱，
///   还能让一个已经连续失败的后端自动降权。没人会手动去做这件事。
/// </summary>
public sealed class NetRouter
{
    // ---- 特征词（与 Python router.py 对齐）----

    private static readonly string[] CnHints =
    [
        "中文", "国内", "国产", "阿里", "腾讯", "字节", "百度", "华为", "政策",
        "知乎", "微信", "公众号", "微博", "小红书", "抖音", "淘宝",
    ];

    private static readonly string[] EnHints =
    [
        "github", "stackoverflow", "arxiv", "paper", "pypi", "npm", "docker",
        "kubernetes", "nuget", "rustdoc", "readthedocs", "docs.", "rfc", "api",
        "changelog", "release notes", "benchmark",
    ];

    private static readonly string[] FreshHints =
    [
        "最新", "今天", "刚才", "实时", "刚发布", "刚刚", "本周", "本月", "今年",
        "股价", "天气", "汇率", "比分", "新闻",
        "latest", "today", "news", "current", "now", "this week",
    ];

    private readonly UsageLedger? _ledger;

    public NetRouter(UsageLedger? ledger = null) => _ledger = ledger;

    /// <summary>
    /// 规则路由：给每个候选后端打分，取前 N 个。
    /// </summary>
    /// <param name="question">用户原话（不是改写后的检索词 —— 改写已经丢了「我要最新」这类信号）。</param>
    /// <param name="candidates">可选后端（已按可用性过滤）。</param>
    /// <param name="wantExtract">这次是否需要正文。</param>
    /// <param name="maxN">最多选几个（每多一个就多一次计费）。</param>
    public RouteDecision Route(
        string question,
        IReadOnlyList<string> candidates,
        bool wantExtract = false,
        int maxN = 2,
        IReadOnlyCollection<string>? extractCapable = null)
    {
        if (candidates.Count == 0)
            return new RouteDecision(Array.Empty<string>(), "没有可用后端");

        var q = (question ?? string.Empty).ToLowerInvariant();
        var hasCjk = ContainsCjk(question ?? string.Empty);
        var cnHit = hasCjk || CnHints.Any(h => q.Contains(h, StringComparison.Ordinal));
        var enHit = EnHints.Any(h => q.Contains(h, StringComparison.Ordinal));
        var freshHit = FreshHints.Any(h => q.Contains(h, StringComparison.Ordinal));

        var scored = new List<(double Score, string Name, List<string> Why)>();

        foreach (var name in candidates)
        {
            var score = 1.0;
            var why = new List<string>();

            if (cnHit && name is "deepseek" or "bocha")
            {
                score += 0.35;
                why.Add("中文场景");
            }

            if (enHit && name is "tavily" or "serper" or "bing")
            {
                score += 0.40;
                why.Add("英文技术场景");
            }

            if (freshHit && name is "deepseek" or "serper" or "bocha")
            {
                score += 0.20;
                why.Add("时效性需求");
            }

            if (wantExtract)
            {
                var capable = extractCapable is null || extractCapable.Contains(name);
                if (capable)
                {
                    score += 0.30;
                    why.Add("需要正文");
                }
                else
                {
                    score -= 0.50;
                }
            }

            // 历史成功率明显差的降权（样本不足 3 次时不动 —— 一次失败不该打死一个后端）
            var stats = _ledger?.BackendStats(name, days: 30);
            if (stats is { Calls: >= 3, OkRate: < 0.5 })
            {
                score -= 0.40;
                why.Add($"历史成功率 {stats.OkRate:P0}");
            }

            scored.Add((Math.Round(score, 3), name, why));
        }

        var picked = scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, maxN))
            .ToList();

        var detail = string.Join("、", picked.Select(p =>
            p.Why.Count > 0 ? $"{p.Name}({string.Join('/', p.Why)})" : p.Name));

        return new RouteDecision(
            picked.Select(p => p.Name).ToList(),
            "规则决策：" + detail);
    }

    /// <summary>是否含中日韩字符（用来判断「这是中文问题」）。</summary>
    public static bool ContainsCjk(string text)
    {
        foreach (var c in text)
        {
            if (c >= '\u4e00' && c <= '\u9fff')
                return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // 融合
    // ------------------------------------------------------------------

    /// <summary>
    /// 把多个后端的结果融合成一份。
    /// </summary>
    /// <param name="perBackend">后端名 → 该后端的结果（已按各自排序）。</param>
    /// <param name="merge">"rrf" / "score" / "none"。</param>
    /// <param name="limit">最终保留几条。</param>
    /// <remarks>
    /// ★ 默认 RRF（Reciprocal Rank Fusion）：<c>score = Σ 1/(k + rank)</c>，k=60。
    ///
    ///   为什么不用「按分数平均」：不同后端的 score 完全不可比 ——
    ///   官方给的是 rerank_score（0~1），Tavily 给的是它自己的相关度，
    ///   两个数放在一起平均没有任何意义，而结果看起来「挺像回事」。
    ///   RRF 只用**名次**，是唯一不需要校准的融合方式，这也是它在检索界成为默认做法的原因。
    ///
    /// ★ 为什么 k = 60：这是 RRF 原论文的经验值。k 越大，名次差异越被抹平
    ///   （第 1 名与第 10 名差别变小）；k=60 的效果是「两个后端都排前面的结果最靠前，
    ///   只有一个后端排第一的结果也不错」，正是我们要的互补性。
    /// </remarks>
    public static List<SearchHit> Merge(
        IReadOnlyDictionary<string, IReadOnlyList<SearchHit>> perBackend,
        string merge = "rrf",
        int limit = 8)
    {
        var mode = (merge ?? "rrf").Trim().ToLowerInvariant();

        if (mode == "none")
        {
            // 不融合：按后端顺序拼接，URL 去重
            var flat = new List<SearchHit>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (_, hits) in perBackend)
            {
                foreach (var h in hits)
                {
                    if (seen.Add(h.UrlKey))
                        flat.Add(h);
                }
            }

            return flat.Count > limit ? flat.GetRange(0, limit) : flat;
        }

        if (mode == "score")
            return MergeByScore(perBackend, limit);

        return MergeByRrf(perBackend, limit);
    }

    private const int RrfK = 60;

    private static List<SearchHit> MergeByRrf(
        IReadOnlyDictionary<string, IReadOnlyList<SearchHit>> perBackend,
        int limit)
    {
        var acc = new Dictionary<string, (SearchHit Hit, double Score)>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var (_, hits) in perBackend)
        {
            for (var i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                var key = h.UrlKey;

                if (key.Length == 0)
                    continue;

                var gain = 1.0 / (RrfK + i + 1);

                if (acc.TryGetValue(key, out var cur))
                {
                    // 同一篇被两个后端都收到：分数累加（这正是 RRF 的互补性来源），
                    // 摘要取更长的那一份 —— 短的那份往往是「只截了开头一句」。
                    acc[key] = (cur.Hit.Snippet.Length >= h.Snippet.Length ? cur.Hit : h, cur.Score + gain);
                }
                else
                {
                    acc[key] = (h, gain);
                    order.Add(key);
                }
            }
        }

        return order
            .Select(k => (Hit: acc[k].Hit, Score: acc[k].Score))
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Hit with { Score = Math.Round(x.Score, 4) })
            .ToList();
    }

    /// <summary>
    /// 按后端自报分数合并（仅用于「所有后端都是同一个 provider」这种场合）。
    /// </summary>
    /// <remarks>
    /// ★ 默认不用。保留它是因为 SEARCH 结果里确实有 score 字段，
    ///   用户可能想看看「带分数长什么样」；但一旦出现跨后端比较，
    ///   这个结果的排序就是不可信的 —— 代码里没有机制阻止误用，
    ///   所以在 <see cref="Merge"/> 的默认值处把它排在 rrf 之后。
    /// </remarks>
    private static List<SearchHit> MergeByScore(
        IReadOnlyDictionary<string, IReadOnlyList<SearchHit>> perBackend,
        int limit)
    {
        var acc = new Dictionary<string, (SearchHit Hit, double Score)>(StringComparer.Ordinal);

        foreach (var (_, hits) in perBackend)
        {
            foreach (var h in hits)
            {
                var key = h.UrlKey;
                if (key.Length == 0)
                    continue;

                var s = h.Score ?? 0;

                if (acc.TryGetValue(key, out var cur))
                    acc[key] = (cur.Hit.Snippet.Length >= h.Snippet.Length ? cur.Hit : h, Math.Max(cur.Score, s));
                else
                    acc[key] = (h, s);
            }
        }

        return acc.Values
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Hit)
            .ToList();
    }

    // ------------------------------------------------------------------
    // 给模型看的文本
    // ------------------------------------------------------------------

    /// <summary>
    /// 把结果排成模型能直接读的编号列表。
    /// </summary>
    /// <remarks>
    /// 格式刻意与 Python 版一致（<c>1. 标题\n   摘要\n   来源：url</c>）：
    /// 模型已经在这个形状上被训练/调试过很多轮，换格式是纯风险。
    /// </remarks>
    public static string Format(IReadOnlyList<SearchHit> hits, int limit = 8)
    {
        if (hits.Count == 0)
            return "（未搜到相关结果）";

        var sb = new StringBuilder(1024);

        var i = 0;
        foreach (var h in hits.Take(limit))
        {
            i++;
            sb.Append(i).Append(". ").Append(h.Title.Length > 0 ? h.Title : "（无标题）").Append('\n');

            if (h.Snippet.Length > 0)
            {
                foreach (var line in h.Snippet.Split('\n'))
                    sb.Append("   ").Append(line).Append('\n');
            }

            sb.Append("   来源：").Append(h.Url);

            if (h.Published.Length > 0)
                sb.Append('（').Append(h.Published).Append('）');

            sb.Append('\n');

            if (h.Content is { Length: > 0 })
            {
                sb.Append("   正文：").Append(h.Content).Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }
}
