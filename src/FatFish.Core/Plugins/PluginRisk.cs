namespace FatFish.Core.Plugins;

/// <summary>
/// 风险的**推断**规则 —— 插件系统的「自适应」核心。
///
/// 一句话概括：**风险由能力决定，不由插件自述决定。**
///
/// 为什么这条要写死在这里，而不是相信清单里的 <c>"risk": "low"</c>：
/// 插件是可以从任何地方拷进目录的一堆文件。如果「我声明我是低风险」
/// 就能免掉人工报批，那么任何恶意（或只是偷懒）的插件只要写一行
/// <c>"risk": "low"</c> 就能拿到和 <c>ws_delete</c> 同等的执行权而无需用户点头。
/// 到那时整套闸门就只剩下「信任插件作者」这一道 —— 而那道闸从来不存在。
///
/// 所以规则是：
///   推断值 = f(执行面)          ← 只看插件**能做什么**
///   生效值 = max(推断值, 声明值) ← 声明只能加严
///
/// 由此得到三条不需要额外维护的性质：
///   · <c>cmd</c> / <c>python</c> 永远不可能低于 High（能跑任意代码就是能跑）；
///   · <c>http POST</c> 永远不可能低于 Normal（它能改远端状态）；
///   · <c>http GET</c> 若作者声明 High，会真的按 High 处理（加严有效）。
/// </summary>
public static class PluginRisk
{
    /// <summary>按执行面推断风险档。</summary>
    public static PluginRiskTier Infer(PluginRunSpec run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run.Kind switch
        {
            // 读一个远端资源，不改本地、不改远端 —— 与 web_fetch 同类。
            PluginRunKind.Http => run.Http!.Method is "GET" or "HEAD"
                ? PluginRiskTier.Low
                : PluginRiskTier.Normal,

            // ★ 命令与代码：无论命令内容看起来多无害，都必须是最严档。
            //   "echo hi" 与 "del /s /q C:\*" 在静态层面无法区分 ——
            //   而插件作者随时可以改 command 字段，改完还叫同名工具。
            //   按「最坏情况」定档，是这里唯一站得住的做法。
            _ => PluginRiskTier.High,
        };
    }

    /// <summary>按执行面推断是否只读。</summary>
    /// <remarks>
    /// 只读的判定同样只看「能力」，不看声明：
    /// HTTP 里只有 GET / HEAD 是只读的；POST 会改远端状态，不算只读。
    /// cmd / python 一律不算只读 —— 哪怕它实际只做了 <c>echo</c>。
    /// </remarks>
    public static bool InferReadOnly(PluginRunSpec run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run.Kind == PluginRunKind.Http
               && run.Http!.Method is "GET" or "HEAD";
    }

    /// <summary>
    /// 合并「推断」与「声明」：取更严的那个。
    /// </summary>
    /// <param name="inferred">由 <see cref="Infer"/> 算出。</param>
    /// <param name="declared">清单里写的；未写或写 auto 时为 null。</param>
    /// <remarks>
    /// ★ 实现依赖 <see cref="PluginRiskTier"/> 的数值顺序（越大越严）。
    ///   这不是巧合，是刻意的：用 <c>Math.Max</c> 表达「只能加严」比
    ///   写三层 if 更难改错 —— 后者很容易在某次重构里把方向写反，
    ///   而写反之后的行为是「插件获得了更多权限」，不会报错，只会更危险。
    /// </remarks>
    public static PluginRiskTier Combine(PluginRiskTier inferred, PluginRiskTier? declared)
        => declared is null
            ? inferred
            : (PluginRiskTier)Math.Max((int)inferred, (int)declared.Value);

    /// <summary>解析清单里的 risk 字段。不认识的值返回 null（调用方应报错）。</summary>
    public static PluginRiskTier? ParseDeclared(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "auto" => null,
            "low" => PluginRiskTier.Low,
            "normal" or "medium" or "write" => PluginRiskTier.Normal,
            "high" or "danger" => PluginRiskTier.High,
            _ => (PluginRiskTier)(-1),   // 哨兵：非法值，与「未声明」区分开
        };
    }

    /// <summary>是否是 <see cref="ParseDeclared"/> 的非法哨兵。</summary>
    public static bool IsInvalid(PluginRiskTier? tier) => tier is { } t && (int)t < 0;
}
