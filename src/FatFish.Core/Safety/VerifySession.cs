using System.Text;
using System.Text.Json;
using FatFish.Core.Llm;

namespace FatFish.Core.Safety;

/// <summary>一批动作核验后的走向。</summary>
public enum VerifyDecision
{
    /// <summary>放行，继续走人工报批与执行。</summary>
    Proceed,

    /// <summary>已打回执行者，本批动作**不执行**。</summary>
    SentBack,

    /// <summary>超过重试上限且严格模式：整批拦截，结束本轮。</summary>
    Blocked,
}

/// <summary>一批动作的核验结论。</summary>
public sealed record VerifyBatchResult
{
    public required VerifyDecision Decision { get; init; }
    public required VerifyOutcome Outcome { get; init; }

    /// <summary>回填给执行者的文本（仅 SentBack / Blocked 时有意义）。</summary>
    public string Feedback { get; init; } = string.Empty;

    /// <summary>控制台/状态栏的一行说明。</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 一轮对话内的核验记账 + 决策。
///
/// 为什么把「数次数」独立出来
/// ------------------------
/// 这段逻辑（supplement 免费额度、revise 计次、超限后按 STRICT 拦截或放行）
/// 是整条链上**唯一有状态**的部分，也是最容易写出「无限循环」或
/// 「一次就打回到底」的地方。把它和「调模型」「拼提示词」分开，
/// 就能用纯单元测试把每种组合都过一遍，不需要网络、不需要模型。
///
/// 计数作用域：**一次用户输入**（<see cref="BeginTurn"/> 重置）。
/// 不是整个会话 —— 否则用户聊到第 20 轮时，模型连改个错别字都会撞上限。
/// </summary>
public sealed class VerifySession
{
    private readonly VerifierService _service;
    private readonly VerifyOptions _options;
    private readonly Action<string>? _log;

    private int _retries;
    private int _supplements;

    public VerifySession(VerifierService service, Action<string>? log = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _options = service.Options;
        _log = log;
    }

    public VerifyOptions Options => _options;

    /// <summary>本轮已用掉的「打回重交」次数（revise，以及用尽额度后的 supplement）。</summary>
    public int Retries => _retries;

    /// <summary>本轮已用掉的「免费补充」轮数。</summary>
    public int Supplements => _supplements;

    public bool IsEnabled => _options.IsEnabled;

    /// <summary>开始处理一条新的用户输入时调用。</summary>
    public void BeginTurn()
    {
        _retries = 0;
        _supplements = 0;
    }

    /// <summary>
    /// 送审并决策。
    /// </summary>
    public async Task<VerifyBatchResult> ReviewAsync(ReviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await _service.ReviewAsync(request, ct).ConfigureAwait(false);

        return Decide(outcome, request.Actions.Count);
    }

    /// <summary>
    /// 按裁决与当前计数决定走向（纯函数式，便于单测）。
    /// </summary>
    public VerifyBatchResult Decide(VerifyOutcome outcome, int actionCount)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Skipped)
        {
            return new VerifyBatchResult { Decision = VerifyDecision.Proceed, Outcome = outcome };
        }

        // ------------------------------------------------------------------
        // ★ 服务故障：**不计次**。
        //
        //   Python 版把 fail-closed 的结果当成一次 supplement 计入了额度，
        //   于是「审查员服务抖一下」会被记成「执行者做得不对」：
        //   4 次补充额度 + 2 次重试额度被网络问题白白烧掉，模型还会收到
        //   「请补充说明」的指令 —— 它会照做，认真地补一堆根本没用的说明。
        //   更糟的是严格模式下最后会变成「整批拦截」，而执行者其实什么都没做错。
        //
        //   故障就该按故障处理：open 就放行（人工报批兜底），closed 就拦下，
        //   两条路都不消耗额度、都不触发 final。
        // ------------------------------------------------------------------
        if (outcome.Failed)
        {
            if (outcome.Verdict.Approved)
            {
                return new VerifyBatchResult
                {
                    Decision = VerifyDecision.Proceed,
                    Outcome = outcome,
                    Note = "⚠️ 核验服务异常，按 fail-open 放行（人工报批仍生效）",
                };
            }

            return new VerifyBatchResult
            {
                Decision = VerifyDecision.Blocked,
                Outcome = outcome,
                Feedback = outcome.ToFeedback(),
                Note = "⚠️ 核验服务异常，按 fail-closed 拦截",
            };
        }

        if (outcome.Verdict.Approved)
        {
            var note = actionCount > 0
                ? $"✅ 双人核验 → {outcome.Verdict.Value}：{Clip(outcome.Verdict.Reason, 80)}"
                : string.Empty;

            return new VerifyBatchResult
            {
                Decision = VerifyDecision.Proceed,
                Outcome = outcome,
                Note = note,
            };
        }

        // ------------------------------------------------------------------
        // 未通过：先看 supplement 的免费额度，再看 revise 的重试额度。
        // ------------------------------------------------------------------
        var isSupplement = string.Equals(outcome.Verdict.Value, "supplement", StringComparison.Ordinal);
        var soft = isSupplement && _supplements < _options.MaxSupplements;

        string tag;

        if (soft)
        {
            _supplements++;
            tag = $"要求补充说明（不计退回次数，补充轮 {_supplements}/{_options.MaxSupplements}）";
        }
        else if (_retries < _options.MaxRetries)
        {
            _retries++;
            tag = $"未放行，已打回执行者补充/改方案（第 {_retries}/{_options.MaxRetries} 次）";
        }
        else
        {
            // 额度用尽
            if (_options.Strict)
            {
                _log?.Invoke($"[双人核验] 超限拦截：{outcome.Verdict.Reason}");

                return new VerifyBatchResult
                {
                    Decision = VerifyDecision.Blocked,
                    Outcome = outcome,
                    Feedback = outcome.ToFeedback(final: true),
                    Note = "🛑 已达双人核验重试上限，动作被拦截（严格模式，全批未执行）",
                };
            }

            _log?.Invoke($"[双人核验] 超限放行（宽松）：{outcome.Verdict.Reason}");

            return new VerifyBatchResult
            {
                Decision = VerifyDecision.Proceed,
                Outcome = outcome,
                Note = "⚠️ 已达双人核验重试上限，按宽松模式放行（继续走人工报批）",
            };
        }

        _log?.Invoke($"[双人核验] 打回：{outcome.Verdict.Reason} " +
                     $"(suppl={_supplements}/{_options.MaxSupplements}, " +
                     $"retry={_retries}/{_options.MaxRetries})");

        return new VerifyBatchResult
        {
            Decision = VerifyDecision.SentBack,
            Outcome = outcome,
            Feedback = outcome.ToFeedback(),
            Note = "🔁 审查员" + tag + "[sent back for revision]",
        };
    }

    /// <summary>拼装送审上下文里的「这是第几次送审」一段。</summary>
    public string BuildExtraNote(string workspaceRoot, bool autoApprovedThisTurn, string autoApproveScopeLabel, string? bypassDetail = null)
    {
        var attempt = _retries + _supplements + 1;

        var sb = new StringBuilder();
        sb.Append("当前工作台根：").Append(workspaceRoot).Append('\n');
        sb.Append("本轮第 ").Append(attempt).Append(" 次送审");

        if (attempt > 1)
        {
            sb.Append("（此前已送审过同一动作，请先核对你的上一条要求是否已被满足，");
            sb.Append("不要另提无关的新要求）");
        }

        // ★ 一键放行会让「人工兜底」这个前提失效。审查员的默认人格里写着
        //   「你不必替所有潜在风险兜底，后面还有用户确认」—— 那句话在这里就不成立了。
        //   所以必须**如实告知**：它现在是唯一的外部复核，标准要按原本的来。
        //   （Python 版 2026-09-19 的注释记录了这条：范围=all 时删除 / 执行也会被放行。）
        if (autoApprovedThisTurn && !string.IsNullOrWhiteSpace(bypassDetail))
        {
            // ★ 档位免批（不是「一键放行」）：用户把「要不要弹窗」整体关掉了。
            //   必须说清「没有任何人逐条看过」，否则审查员会按旧人格假设
            //   「后面还有用户兜底」而放松标准 —— 而这种放松是静默的，没人会发现。
            sb.Append("\n⚠️ 本批存在**免于人工批准**的动作（").Append(bypassDetail.Trim()).Append("）：")
              .Append("这些动作没有任何人逐条审阅过。你是唯一的外部复核 —— ")
              .Append("请按原本标准从严把关，不要因「用户自己把审批档位调松了」而放宽；")
              .Append("对不可逆、越界、删改敏感文件、外发数据的动作要格外严格，")
              .Append("必要时直接判 revise 或 reject。");
        }
        else if (autoApprovedThisTurn)
        {
            sb.Append("\n⚠️ 本轮已开启「一键放行」（范围：").Append(autoApproveScopeLabel).Append("）：")
              .Append(autoApproveScopeLabel switch
              {
                  "all" => "**含删除 / 执行命令 / 执行代码在内**（仅敏感文件除外）都会自动放行，" +
                           "本批不存在任何逐批人工确认",
                  "writes" => "覆盖内容写入（写 / 追加 / 替换），删除 / 执行类仍会逐批人工确认",
                  "none" => "实际不覆盖任何动作（等于回到逐批确认）",
                  _ => "覆盖范围见档位说明",
              })
              .Append("。对这些动作，你是唯一的外部复核 —— ")
              .Append("请按原本标准从严把关，不要因「用户已表示信任」而放宽。");
        }

        return sb.ToString();
    }

    private static string Clip(string s, int n)
        => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..n] + "…";
}

/// <summary>
/// 用户即时命令记录 —— 供审查员判断「用户是否已亲自授权」。
///
/// ★ 这个类存在的唯一理由：**模型没有写入通道**。
///   送审材料里其他每一段（需求、计划、动作、上下文）都来自模型生成的内容，
///   只有这一段是主程序从本地输入通道直接采集的用户原声。
///   审查员因此可以据此确信「这是用户本人的意图」，而不是执行者的自我授权。
///
///   不带它的时候踩过的坑：用户亲手输入 <c>/ws cd D:\data</c>，
///   模型据此调 ws_cd，审查员判「擅自扩大工作范围」驳回 —— 而那是用户的命令。
///
/// 滚动保留最近 12 条（与 Python 版一致：太多会挤占预算，太少不够判断）。
/// </summary>
public sealed class UserEventLog
{
    private const int Capacity = 12;

    private readonly Queue<string> _events = new();
    private readonly object _gate = new();

    /// <summary>记一条用户原声（命令 / 报批答复）。</summary>
    public void Record(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var line = $"[{DateTime.Now:HH:mm:ss}] {text.Trim()}";

        lock (_gate)
        {
            _events.Enqueue(line);

            while (_events.Count > Capacity)
                _events.Dequeue();
        }
    }

    /// <summary>拼成送审材料里的一段；没有记录时返回空串（该段整段不出现）。</summary>
    public string Render()
    {
        lock (_gate)
        {
            if (_events.Count == 0)
                return string.Empty;

            return string.Join("\n", _events);
        }
    }

    /// <summary>清空（<c>/new</c> 时调用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }
}
