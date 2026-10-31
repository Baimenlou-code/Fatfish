using System.Net;
using FatFish.Core.Plugins;

namespace FatFish.Core.Net;

/// <summary>某一把 key 的模型清单（成功就是清单，失败就是原因）。</summary>
/// <param name="KeyIndex">key 在库里的序号（1 起；日志里只用它，不打印 key）。</param>
/// <param name="Base">这把 key 自带的接口地址；null 表示跟插件级的地址走。</param>
public sealed record KeyCatalog(
    int KeyIndex,
    bool Ok,
    IReadOnlyList<string> Models,
    string? Error,
    int? StatusCode,
    long ElapsedMs,
    string? Base = null)
{
    /// <summary>给人看的来源：<c>api.x.ai</c> 或 <c>#1</c>（跟插件级地址时只显示序号）。</summary>
    public string Where => Base is null ? $"#{KeyIndex}" : $"#{KeyIndex} {PluginModelOption.HostOf(Base)}";
}

/// <summary>「每把 key 各查一次」之后的汇总结果。</summary>
/// <param name="Models">全部 key 的并集（去重、按字典序）。</param>
/// <param name="ModelKeys">模型 → 提供它的 key 序号（同一模型被多把 key 支持时取序号最小的）。</param>
public sealed record ModelCatalogAllResult(
    IReadOnlyList<KeyCatalog> PerKey,
    IReadOnlyList<string> Models,
    IReadOnlyDictionary<string, int> ModelKeys)
{
    /// <summary>
    /// 展开后的全部通道（模型 × key × 接口地址），按 key 顺序、模型字典序。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能只有 <see cref="Models"/>：</b>
    /// 那个是去重后的名字。当同一个模型在「中转 key #2」和「xAI 官方 key #3」上都有时，
    /// 用户需要**看得见两条、并且选得到** —— 两家的价格、速度、接口方言都不一样。
    /// 去重后的列表会把其中一条永久藏起来。
    /// </remarks>
    public IReadOnlyList<PluginModelOption> Options { get; init; } = Array.Empty<PluginModelOption>();

    /// <summary>至少有一把 key 查到了东西。</summary>
    public bool Ok => Models.Count > 0;

    public IReadOnlyList<string> Errors =>
        PerKey.Where(k => !k.Ok).Select(k => $"{k.Where}：{k.Error}").ToList();

    public string Describe()
    {
        if (Ok)
        {
            var failed = PerKey.Count(k => !k.Ok);
            var channels = Options.Count > Models.Count
                ? $"、{Options.Count} 条通道（同名模型来自不同 key）"
                : string.Empty;

            return failed == 0
                ? $"{PerKey.Count} 把 key 共 {Models.Count} 个模型{channels}"
                : $"{PerKey.Count} 把 key 共 {Models.Count} 个模型{channels}（{failed} 把查询失败）";
        }

        return Errors.Count > 0 ? string.Join("；", Errors) : "没有可用的 key";
    }
}

/// <summary>
/// 逐把 key 查询模型清单，再取并集 + 展开成通道。
/// </summary>
/// <remarks>
/// <b>为什么不复用 <see cref="ModelCatalog.FetchAsync"/>：</b>
/// 那个方法是「按顺序试，第一把通了就收工」—— 用于"确认连通性"是对的，
/// 用于"列出可选模型"是错的。
///
/// 2026-10-30 实测（同一中转站的两把 key）：
///   #1 → gpt-image-2 / gpt-image-2 default
///   #2 → grok-imagine-image / grok-imagine-image-2.0 / grok-imagine-image-quality
/// 两把 key 的可见模型**完全不同**。只看第一把，用户就永远选不到另一半。
///
/// <b>为什么每把 key 用各自的地址：</b>
/// 一个插件可能同时接中转站和官方 API（<c>key@https://api.x.ai/v1</c>）。
/// 全用插件级地址去查，官方那把会被 404，而用户会以为"这把 key 没模型"。
///
/// 代价是每次发现要发 N 次请求（N = key 数量）。这个代价是可控的：
/// 只在启动后台任务与用户点「刷新」时发生，不跟着每轮对话走。
/// </remarks>
public static class ModelCatalogAll
{
    public static async Task<ModelCatalogAllResult> FetchAsync(
        PluginKeySet keys,
        string? baseUrl,
        string? modelsPath,
        int timeoutSeconds = 10,
        string userAgent = ModelCatalog.DefaultUserAgent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.IsEmpty)
        {
            return new ModelCatalogAllResult(
                Array.Empty<KeyCatalog>(), Array.Empty<string>(),
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        }

        var perKey = new List<KeyCatalog>(keys.Keys.Count);

        foreach (var key in keys.Keys)
        {
            // 这把 key 自带地址就用自己的，否则落到插件级地址。
            var effectiveBase = key.Base ?? baseUrl;
            var url = ModelCatalog.BuildUrl(effectiveBase, modelsPath);

            if (url is null)
            {
                perKey.Add(new KeyCatalog(
                    key.Index, false, Array.Empty<string>(),
                    "接口地址不合法：" + (effectiveBase ?? "(未配置)"),
                    null, 0, key.Base));
                continue;
            }

            var one = await ModelCatalog
                .FetchOnceAsync(url, key, timeoutSeconds, userAgent, ct)
                .ConfigureAwait(false);

            perKey.Add(new KeyCatalog(
                key.Index,
                one.Ok,
                one.Models,
                one.Error,
                one.StatusCode,
                one.ElapsedMs,
                key.Base));
        }

        var union = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var options = new List<PluginModelOption>();

        foreach (var catalog in perKey.Where(k => k.Ok))
        {
            // 同一个模型在同一个接口地址下只会出现一次；跨 key 重复时要各自成项，
            // 所以这里刻意不去重 —— 去重正是旧实现把第二条通道藏起来的原因。
            foreach (var model in catalog.Models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
            {
                options.Add(new PluginModelOption
                {
                    Model = model,
                    KeyIndex = catalog.KeyIndex,
                    Base = catalog.Base,
                });

                union.Add(model);

                // 同一模型被多把 key 支持时取序号最小的那把：
                // 序号小的更靠前 = 用户更信任的账号（通常就是主号），
                // 而且这样"哪把 key 出图"不会随发现顺序漂移。
                if (!map.TryGetValue(model, out var existing) || catalog.KeyIndex < existing)
                    map[model] = catalog.KeyIndex;
            }
        }

        return new ModelCatalogAllResult(perKey, union.ToList(), map) { Options = options };
    }
}
