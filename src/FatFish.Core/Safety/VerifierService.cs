using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FatFish.Core.Safety;

/// <summary>一次核验的完整结果。</summary>
public sealed record VerifyOutcome
{
    public required Verdict Verdict { get; init; }

    /// <summary>核验服务本身出错了（网络 / 鉴权 / 超时）。</summary>
    public bool Failed { get; init; }

    /// <summary>压根没送审（模式关闭 / 没有需要核验的动作）。</summary>
    public bool Skipped { get; init; }

    public double ElapsedSeconds { get; init; }

    /// <summary>这一批被送审的动作（回填与展示要用）。</summary>
    public IReadOnlyList<string> ActionNames { get; init; } = Array.Empty<string>();

    /// <summary>是否应当拦下这批动作。</summary>
    /// <remarks>
    /// ★ 语义上成立，实现上**不用它做判定** ——
    ///   编排层用的是「Verdict.Approved 且 failed 没在 fail-closed 下被翻成拦截」。
    ///   这个属性只为日志与展示服务，避免两处各写一套判断。
    /// </remarks>
    public bool Blocked => !Skipped && !Verdict.Approved;

    /// <summary>控制台一行摘要。</summary>
    public string SummaryLine()
    {
        if (Skipped)
            return "（跳过核验）";

        var icon = Verdict.Value switch
        {
            "approve" => "✅",
            "supplement" => "🟡",
            "revise" => "🛑",
            _ => "❔",
        };

        var tag = Failed ? "核验服务异常" : $"审查员裁决：{Verdict.Value}";
        var reason = Verdict.Reason.Length > 120 ? Verdict.Reason[..120] + "…" : Verdict.Reason;

        return $"{icon} {tag}｜{reason}";
    }

    /// <summary>
    /// 把审查意见拼成「回填给执行者」的文本（塞进 role=tool 消息）。
    ///
    /// ★ 这段文字是**写给模型看的**，不是写给用户看的。所以它必须：
    ///   · 明确说「不要原样重试」（否则模型会原样再发一次，白烧一轮）；
    ///   · 把 demands 逐条列出来（模型对列表的遵循度明显高于散文）；
    ///   · 在 final 时说明「已被拦截，别再试了，去跟用户解释」。
    /// </summary>
    public string ToFeedback(bool final = false)
    {
        var sb = new StringBuilder();

        sb.Append("【双人核验·未通过｜审查员裁决：").Append(Verdict.Value)
          .Append('（').Append(Verdict.Label).Append("）】").Append('\n');

        if (Verdict.Reason.Length > 0)
            sb.Append("理由：").Append(Verdict.Reason).Append('\n');

        if (Verdict.Risks.Count > 0)
        {
            sb.Append("审查员识别的风险：\n");
            foreach (var r in Verdict.Risks)
                sb.Append("  - ").Append(r).Append('\n');
        }

        if (Verdict.Demands.Count > 0)
        {
            sb.Append("要求你（执行者）补充或修改：\n");

            for (var i = 0; i < Verdict.Demands.Count; i++)
                sb.Append("  ").Append(i + 1).Append(") ").Append(Verdict.Demands[i]).Append('\n');
        }
        else
        {
            sb.Append("要求你（执行者）补充说明本次操作的目的、影响范围与回滚方式。\n");
        }

        if (Verdict.Alternatives.Count > 0)
        {
            sb.Append("审查员建议的替代方案：\n");
            foreach (var a in Verdict.Alternatives)
                sb.Append("  - ").Append(a).Append('\n');
        }

        sb.Append("👉 请先补充说明或变更方案，然后重新提交核验；不要原样重试被驳回的动作。\n");

        if (final)
        {
            sb.Append("⚠️ 已超出核验重试上限，本行动作被拦截，")
              .Append("请向用户说明情况并等待进一步指示。\n");
        }

        sb.Append("（这是第二位 AI 独立审查员的意见，供你自我修正使用。）");

        return sb.ToString();
    }
}

/// <summary>
/// 双人核验服务 —— 装配送审材料、调用审查员、解析裁决、按 FAIL_MODE 兜底。
///
/// 它**不做**决策：不数重试次数、不决定打回还是拦截、不碰对话历史。
/// 那些属于 <see cref="VerifySession"/>（一轮内的记账）与 AgentLoop（编排）。
/// 保持这种分工，才能让「换了模型 / 换了网络 / 换了 FAIL_MODE」的后果
/// 被限制在这一层里，不会渗进对话主循环。
/// </summary>
public sealed class VerifierService : IDisposable
{
    private readonly VerifyOptions _options;
    private readonly VerifierClient? _client;
    private readonly Func<string, string, CancellationToken, Task<string>>? _chat;
    private readonly Action<string>? _log;
    private readonly string? _logPath;
    private readonly object _logGate = new();

    public VerifyOptions Options => _options;

    /// <param name="options">配置。</param>
    /// <param name="log">可选日志回调（控制台）。</param>
    /// <param name="logDirectory">
    /// 日志目录（通常是 <c>&lt;工作台&gt;/logs/verify</c>）。为空则不落盘。
    /// </param>
    public VerifierService(VerifyOptions options, Action<string>? log = null, string? logDirectory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;

        if (_options.IsEnabled)
            _client = new VerifierClient(options.Endpoint);

        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                _logPath = Path.Combine(logDirectory, $"verify-{DateTime.Now:yyyy-MM-dd}.log");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志目录建不出来不影响核验本身
                _logPath = null;
            }
        }
    }

    /// <summary>
    /// 测试 / 离线自检用：注入一个假的 chat 实现，不联网。
    /// </summary>
    public VerifierService(
        VerifyOptions options,
        Func<string, string, CancellationToken, Task<string>> chatOverride,
        Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _chat = chatOverride ?? throw new ArgumentNullException(nameof(chatOverride));
        _log = log;
    }

    // ==================================================================
    // 入口
    // ==================================================================

    /// <summary>核验一批动作。</summary>
    public async Task<VerifyOutcome> ReviewAsync(ReviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_options.IsEnabled || request.Actions.Count == 0)
        {
            return new VerifyOutcome
            {
                Verdict = new Verdict { Value = "approve", Reason = "（无需核验）" },
                Skipped = true,
                ActionNames = request.Actions.Select(a => a.Name).ToArray(),
            };
        }

        var names = request.Actions.Select(a => a.Name).ToArray();

        // ---- 测试桩：FATFISH_VERIFY_MOCK=approve|supplement|revise（不联网）----
        //  与 Python 版同一个键名：方便手工验证「回填链路通不通」，且不花一分钱。
        if (TryMock() is { } mock)
            return mock with { ActionNames = names };

        var prompt = VerifierPrompts.BuildActionPrompt(request, _options);

        var sw = Stopwatch.StartNew();
        try
        {
            var text = await ChatAsync(VerifierPrompts.VerifierSystem, prompt, ct).ConfigureAwait(false);
            sw.Stop();

            var verdict = VerdictParser.Parse(text);
            var outcome = new VerifyOutcome
            {
                Verdict = verdict,
                ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
                ActionNames = names,
            };

            Log($"核验完成 {verdict.Value}({verdict.Severity}) " +
                $"{outcome.ElapsedSeconds:0.00}s｜{verdict.Reason}");

            if (verdict.Degraded)
            {
                // 解析降级：把原始输出落盘，便于定位「无法解析」的真实形态。
                // 没有这一条，线上只会看到「审查员总在要求补充」，永远查不出原因。
                Log($"核验降级解析，审查员原始输出（{text.Length} 字符）：" +
                    VerdictParser.SplitInline(text, 800));
            }

            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户取消（不是核验失败）：原样抛出，由上层按取消处理。
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailOutcome(ex, names, sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>复核最终答复（可选功能）。</summary>
    public async Task<VerifyOutcome> ReviewAnswerAsync(
        string reply, string userGoal, string contextText, CancellationToken ct = default)
    {
        if (!_options.IsEnabled || !_options.VerifyFinalAnswer || string.IsNullOrWhiteSpace(reply))
        {
            return new VerifyOutcome
            {
                Verdict = new Verdict { Value = "approve", Reason = "（无需核验）" },
                Skipped = true,
            };
        }

        var prompt = VerifierPrompts.BuildAnswerPrompt(reply, userGoal, contextText, _options);

        var sw = Stopwatch.StartNew();
        try
        {
            var text = await ChatAsync(VerifierPrompts.AnswerSystem, prompt, ct).ConfigureAwait(false);
            sw.Stop();

            var verdict = VerdictParser.Parse(text);

            Log($"答复复核 {verdict.Value}({verdict.Severity}) {sw.Elapsed.TotalSeconds:0.00}s｜{verdict.Reason}");

            return new VerifyOutcome
            {
                Verdict = verdict,
                ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailOutcome(ex, Array.Empty<string>(), sw.Elapsed.TotalSeconds);
        }
    }

    // ==================================================================
    // 内部
    // ==================================================================

    private async Task<string> ChatAsync(string system, string user, CancellationToken ct)
    {
        if (_chat is not null)
            return await _chat(system, user, ct).ConfigureAwait(false);

        if (_client is null)
            throw new VerifierException("未配置审查员 API key");

        return await _client.CompleteAsync(system, user, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 服务不可用时的兜底 —— 由 FAIL_MODE 决定方向。
    /// </summary>
    /// <remarks>
    /// 默认 open 的理由：核验是**第二道**闸，人工报批还在后面。
    /// 网络抖一下就让人什么活都干不了，用户会直接把核验关掉 —— 那才是真的失去保护。
    /// 但 closed 也必须存在：在无人值守的自动化场景里，「没人兜底」意味着
    /// 放行等于裸奔，那时候宁可停。
    /// </remarks>
    private VerifyOutcome FailOutcome(Exception ex, IReadOnlyList<string> names, double seconds)
    {
        var err = $"{ex.GetType().Name}: {ex.Message}";
        Log("核验调用失败：" + err);

        if (_options.FailMode == VerifyFailMode.Closed)
        {
            return new VerifyOutcome
            {
                Verdict = new Verdict
                {
                    Value = "supplement",
                    Severity = "medium",
                    Reason = $"核验服务不可用（{err}），已按 fail-closed 策略拦截，请稍后重试。",
                    Demands = new[] { "动作未通过独立核验：审查服务暂时不可用，请稍后重试或人工确认。" },
                    Degraded = true,
                },
                Failed = true,
                ElapsedSeconds = Math.Round(seconds, 2),
                ActionNames = names,
            };
        }

        return new VerifyOutcome
        {
            Verdict = new Verdict
            {
                Value = "approve",
                Severity = "low",
                Reason = $"核验服务不可用（{err}），已按 fail-open 策略放行。",
                Degraded = true,
            },
            Failed = true,
            ElapsedSeconds = Math.Round(seconds, 2),
            ActionNames = names,
        };
    }

    private VerifyOutcome? TryMock()
    {
        var raw = Environment.GetEnvironmentVariable("FATFISH_VERIFY_MOCK")?.Trim().ToLowerInvariant();

        if (raw is not ("approve" or "supplement" or "revise"))
            return null;

        var blocked = raw != "approve";

        Log($"核验（测试桩 FATFISH_VERIFY_MOCK={raw}）：blocked={blocked}");

        return new VerifyOutcome
        {
            Verdict = new Verdict
            {
                Value = raw,
                Severity = blocked ? "high" : "low",
                Reason = $"[测试桩] 强制裁决为 {raw}",
                Demands = blocked ? new[] { $"[测试桩] 请补充说明后重新提交（{raw}）" } : Array.Empty<string>(),
                Alternatives = blocked ? new[] { "[测试桩] 建议先只读探查再动手" } : Array.Empty<string>(),
                Risks = raw == "revise" ? new[] { "[测试桩] 假风险" } : Array.Empty<string>(),
            },
        };
    }

    /// <summary>
    /// 双写日志：回调（控制台）+ 文件（事后审计）。全部吞异常 ——
    /// 审计记录写不进去，不该让核验失败。
    /// </summary>
    private void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

        try
        {
            _log?.Invoke("🧿 " + message);
        }
        catch (Exception)
        {
            // 宿主的日志回调抛异常时，宁可丢这条日志
        }

        if (_logPath is null)
            return;

        lock (_logGate)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose() => _client?.Dispose();
}
