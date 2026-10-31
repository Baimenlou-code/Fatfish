using System.Text.Json;
using FatFish.Core.Net;

namespace FatFish.Core.Tools.Web;

/// <summary>
/// net_balance —— 查看余额、额度与本月联网花费。
///
/// 这个工具存在的理由不是「好看」，而是**让模型知道自己花得起多少**：
/// 余额见底时继续搜只会得到 HTTP 402，而 402 的原文是一句英文，
/// 模型很可能把它理解成「参数不对」然后反复重试 —— 每次都更贵。
/// 有了这个工具，模型可以在大量联网之前先看一眼，也可以在失败后确认原因。
///
/// ★ 它同时是「台账」的读取口：<c>logs/usage/net_usage.jsonl</c> 里记的每一次搜索
///   都在这里汇总成「本月用了几次、花了多少钱」。这样余额（官方接口）与花费（本地台账）
///   能互相对照 —— 两者对不上时，通常意味着有人用同一个 key 在别处跑了东西。
/// </summary>
public sealed class NetBalanceTool : ITool
{
    public string Name => "net_balance";

    public string Description =>
        "查看 DeepSeek 账户余额、Tavily 本月剩余额度，以及本机记录的本月联网花费（搜索/抓取次数）。" +
        "联网失败（尤其是怀疑额度用完）时用它确认原因；大量联网之前也可以先看一眼。" +
        "只读，不产生费用。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("month", Schema.String("要汇总的月份，格式 yyyy-MM，例如 2026-10；省略表示本月")),
            ("refresh", Schema.Boolean(
                "是否强制忽略 60 秒缓存、重新向官方接口查询（默认 false）", false)),
        ]);

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public async ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        var month = args.GetString("month");
        var refresh = args.GetBool("refresh", false);

        // ---- 1. 官方余额（并发查两个后端，默认走 60 秒缓存）----
        var svc = new BalanceService();
        var snap = await svc.FetchAsync(force: refresh, ct: ct).ConfigureAwait(false);

        // 分级要由 svc 算：阈值可以被 .env 覆盖（NETBAL_DS_WARN 等）
        var (dsLevel, tvLevel, _) = snap.Grade(svc);

        // ---- 2. 本地台账 ----
        MonthSummary? summary = null;
        string? ledgerPath = null;

        try
        {
            var ledger = UsageLedger.Open(context.Guard.Root);
            summary = ledger.Summarize(month);
            ledgerPath = context.Guard.ToRelative(ledger.FilePath);
        }
        catch (Exception ex)
        {
            context.Log?.Invoke("[台账] 读取失败（不影响余额查询）：" + ex.Message);
        }

        var lines = new List<string>
        {
            $"DeepSeek 余额：{(snap.DeepSeek.Ok ? $"¥{snap.DeepSeek.Total:0.00}" : "查询失败")}" +
            $"（{BalanceService.LevelIcon(dsLevel)} {BalanceService.LevelText(dsLevel)}）" +
            (snap.DeepSeek.Ok ? $"，其中赠送 ¥{snap.DeepSeek.Granted:0.00} / 充值 ¥{snap.DeepSeek.ToppedUp:0.00}" : string.Empty),

            $"Tavily 额度：{(snap.Tavily.Ok && snap.Tavily.Left is { } left ? $"{left} 剩余（{snap.Tavily.LeftPct:0.#}%）" : "查询失败")}" +
            $"（{BalanceService.LevelIcon(tvLevel)} {BalanceService.LevelText(tvLevel)}）" +
            (snap.Tavily.Ok ? $"，计划 {snap.Tavily.Plan}，已用 search {snap.Tavily.SearchUsage} / extract {snap.Tavily.ExtractUsage}" : string.Empty),
        };

        if (snap.DeepSeek.Error is not null)
            lines.Add("DeepSeek 错误：" + snap.DeepSeek.Error);

        if (snap.Tavily.Error is not null)
            lines.Add("Tavily 错误：" + snap.Tavily.Error);

        if (summary is not null)
        {
            if (summary.IsEmpty)
            {
                lines.Add($"{summary.Month} 本机台账：暂无联网记录");
            }
            else
            {
                lines.Add($"{summary.Month} 本机台账：共 {summary.TotalCalls} 次（成功 {summary.TotalOk}），" +
                          $"估算花费 ¥{summary.TotalCost:0.00}");

                foreach (var b in summary.Backends)
                {
                    lines.Add($"  · {b.Label}（{b.Backend}）：{b.Calls} 次 / {b.Ok} 成功 / {b.Units} {b.Unit}" +
                              (b.Cost > 0 ? $" ≈ ¥{b.Cost:0.00}" : string.Empty));
                }
            }

            if (ledgerPath is not null)
                lines.Add($"台账文件：{ledgerPath}");
        }

        return ToolResult.Success(new
        {
            deepseek = new
            {
                ok = snap.DeepSeek.Ok,
                level = dsLevel.ToString().ToLowerInvariant(),
                total = snap.DeepSeek.Total,
                granted = snap.DeepSeek.Granted,
                topped_up = snap.DeepSeek.ToppedUp,
                currency = snap.DeepSeek.Currency,
                key = snap.DeepSeek.KeyMask,
                error = snap.DeepSeek.Error,
            },
            tavily = new
            {
                ok = snap.Tavily.Ok,
                level = tvLevel.ToString().ToLowerInvariant(),
                plan = snap.Tavily.Plan,
                plan_limit = snap.Tavily.PlanLimit,
                plan_used = snap.Tavily.PlanUsed,
                key_limit = snap.Tavily.KeyLimit,
                key_used = snap.Tavily.KeyUsed,
                left = snap.Tavily.Left,
                left_pct = snap.Tavily.LeftPct,
                search_usage = snap.Tavily.SearchUsage,
                extract_usage = snap.Tavily.ExtractUsage,
                key = snap.Tavily.KeyMask,
                error = snap.Tavily.Error,
            },
            month = summary?.Month,
            calls = summary?.TotalCalls ?? 0,
            ok_calls = summary?.TotalOk ?? 0,
            estimated_cost = summary?.TotalCost ?? 0,
            text = string.Join("\n", lines),
        });
    }
}
