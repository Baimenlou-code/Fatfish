using System.Text;
using System.Text.Json;
using FatFish.Core.Llm;
using FatFish.Core.Safety;
using FatFish.Core.Tools;

namespace FatFish.Core.Agent;

/// <summary>编排层配置。</summary>
public sealed class AgentOptions
{
    /// <summary>单轮用户输入内，最多允许几轮「模型 ↔ 工具」往返。</summary>
    public int MaxRounds { get; init; } = 25;

    /// <summary>单条工具结果回填给模型的字符上限（超出截断）。</summary>
    public int MaxToolResultChars { get; init; } = 30_000;

    /// <summary>历史保留的最大条数（超出后从最早的成对消息开始丢）。</summary>
    public int MaxHistoryMessages { get; init; } = 200;

    /// <summary>是否在日志里打印每一轮的摘要。</summary>
    public bool Verbose { get; init; }

    /// <summary>系统提示词。</summary>
    public string SystemPrompt { get; init; } = DefaultSystemPrompt;

    public const string DefaultSystemPrompt =
        "你是 FatFish，一条运行在用户命令行里的蓝色大肥鱼。你能读写文件、执行命令与代码。\n" +
        "工具使用的硬性约定：\n" +
        "1. 修改任何文件之前，必须先调用 ws_read 读取它，获得读凭证；否则写入会被拒绝。\n" +
        "2. 路径一律使用相对工作台根目录的相对路径，不要用绝对路径，也不要试图访问工作台之外。\n" +
        "3. 涉及危险的写 / 删 / 执行操作时，如实说明你要做什么、影响范围与回滚方式。" +
        "**是否弹出人工确认，由用户当前设定的审批档位决定，可能直接免批 —— " +
        "不要假设「反正有人会拦」就省略说明。**" +
        "免于人工批准时，每批动作仍会送给独立审查员复核，它知道这一点并按更严的标准把关。\n" +
        "4. 不要臆测文件内容；不确定就先读。回答简洁、直接、有用。\n" +
        "5. 看目录结构用 ws_list，找内容用 ws_search —— 这两个不需要审批，" +
        "比用 ws_run_cmd 跑 dir / findstr 更快也更安全。\n" +
        "6. ws_search 不会返回 .env / 私钥等敏感文件的内容，这是有意的；" +
        "确实需要时改用 ws_read（它会触发人工审批）。\n" +
        "7. 工作台根目录是**会变的**：ws_cd 切换后所有相对路径都按新根解析，" +
        "并且读凭证会被清空（改文件前要重新 ws_read）。不确定当前在哪时，先调 ws_where。\n" +
        "8. ws_cd 的目标若在默认作业区之外，不会立即生效 —— 它会返回「需用户批准」。" +
        "此时必须先把目标目录和理由告诉用户，等用户明确同意后再调用 ws_cd_approve。" +
        "不要自己替用户决定，也不要跳过 ws_cd_approve 直接操作目标目录。\n" +
        "9. 工具列表里可能有来自插件的工具（描述末尾带「插件 X」）。它们和内置工具走**同一套**审批与核验，" +
        "风险档由「这个工具能做什么」自动推断（能跑命令 / 代码的一律按高危处理），" +
        "不要因为它是插件就以为更宽松，也不要以为它能绕过审批。" +
        "不确定有哪些插件时用 ws_plugin_list；改完 plugin.json 想立刻生效时用 ws_plugin_reload。";
}

/// <summary>
/// 编排层观察者 —— 内核 → 界面/日志 的回调。
///
/// 全部给默认实现：调用方只覆盖自己关心的几个，
/// 不必为了「实现接口」写一堆空方法。
/// </summary>
public interface IAgentObserver
{
    /// <summary>模型正文增量（用于流式显示）。</summary>
    void OnTextDelta(string text) { }

    /// <summary>思考链增量。</summary>
    void OnReasoningDelta(string text) { }

    /// <summary>新一轮开始（round 从 0 计）。</summary>
    void OnRoundBegin(int round) { }

    /// <summary>模型请求调用某个工具。</summary>
    void OnToolCallStarted(string toolName, JsonElement args) { }

    /// <summary>工具执行完毕。</summary>
    void OnToolCallFinished(string toolName, ToolResult result) { }

    /// <summary>用量统计。</summary>
    void OnUsage(LlmUsage usage) { }

    /// <summary>状态栏文字。</summary>
    void OnStatus(string text) { }

    /// <summary>
    /// 请求人工确认。返回 false 表示拒绝执行。
    /// 默认实现直接拒绝 —— 宁可让功能不可用，也不让危险动作在无人确认时悄悄执行。
    /// </summary>
    ValueTask<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
        => ValueTask.FromResult(false);
}

/// <summary>一次 RunAsync 的结果。</summary>
public sealed record AgentRunResult
{
    public required string FinalText { get; init; }
    public int Rounds { get; init; }
    public int ToolCallCount { get; init; }
    public LlmUsage? Usage { get; init; }
    public string? StopReason { get; init; }

    public bool HitRoundLimit => StopReason == "round_limit";
}

/// <summary>
/// 一条「进入历史的消息」的完整描述。
///
/// 为什么不直接给 <see cref="ChatMessage"/>：会话落盘要记的是
/// 「哪个工具、成没成功、花了多久」，这些在 ChatMessage 里**没有**——
/// tool 消息只有一段 JSON 字符串，工具名和耗时都躺在字符串里。
/// 让落盘器去解析 JSON 既脆弱（格式一变就失效），也无法区分「用户拒绝」和「工具失败」
/// （两者都是 ok:false，但前者根本没有真正执行）。
/// </summary>
public sealed record AgentTraceEntry
{
    public required ChatMessage Message { get; init; }

    /// <summary>role=tool 时，这是哪个工具的结果。</summary>
    public string? ToolName { get; init; }

    /// <summary>role=tool 时，工具是否成功（用户拒绝 = false，但 ElapsedMs 为 0）。</summary>
    public bool? ToolOk { get; init; }

    /// <summary>role=tool 时的耗时（毫秒）。</summary>
    public long? ElapsedMs { get; init; }

    /// <summary>role=assistant 时，这一轮发了哪几个工具调用。</summary>
    public IReadOnlyList<string>? ToolNames { get; init; }

    /// <summary>role=assistant 时的用量。</summary>
    public LlmUsage? Usage { get; init; }
}

/// <summary>
/// AgentLoop —— 把 LLM 与工具串成闭环。
///
/// 对应 Python 版 FATHFISHII.py 主循环里那段 while round &lt; MAX_ROUNDS。
///
/// ★ 设计铁律：审批判定只在本类里做一次。
///   工具自己不知道「用户是否已一键放行」，也不知道「这是第几轮」；
///   它只负责执行。这样「规则漂移」在结构上就不可能发生。
/// </summary>
public sealed class AgentLoop
{
    private readonly DeepSeekClient _llm;
    private readonly ToolRegistry _tools;
    private readonly ToolContext _context;
    private readonly AgentOptions _options;
    private readonly ApprovalPolicy _policy;

    /// <summary>双人核验（第二位 AI 审查员）。为 null 或关闭时只剩人工报批一道闸。</summary>
    private readonly VerifySession? _verify;

    /// <summary>用户即时命令记录 —— 送审材料里唯一「模型无法伪造」的一段。</summary>
    private readonly UserEventLog? _userEvents;

    private readonly List<ChatMessage> _history = new();

    /// <summary>本次会话内是否已「一键放行」。</summary>
    private bool _autoApprovedForSession;

    /// <summary>累计用量（整个进程生命周期）。</summary>
    private long _totalPromptTokens;
    private long _totalCompletionTokens;

    public AgentLoop(
        DeepSeekClient llm,
        ToolRegistry tools,
        ToolContext context,
        ApprovalPolicy policy,
        AgentOptions? options = null,
        VerifySession? verify = null,
        UserEventLog? userEvents = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? new AgentOptions();

        // 可选注入：现有调用方（含全部单测）不必改签名。
        // 关闭核验时传 null 即可 —— 那正是 VerifyOptions.Mode == Off 的效果。
        _verify = verify;
        _userEvents = userEvents;

        _history.Add(ChatMessage.System(_options.SystemPrompt));
    }

    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>
    /// 每有一条消息进入历史就触发一次。
    ///
    /// ★ 触发点在**历史真正改变之后**，不是在模型返回时。
    ///   这样订阅者（会话落盘）看到的消息序列与模型看到的完全一致，
    ///   不会出现「日志里有一条、历史里没有」的错位。
    /// ★ 订阅者抛异常会打断对话，所以订阅方必须自己吞掉所有异常
    ///   （SessionStore.Append 就是这么做的）。
    /// </summary>
    public event Action<AgentTraceEntry>? Traced;

    /// <summary>历史被清空时触发（Ctrl+N / <c>/new</c>）。</summary>
    public event Action? HistoryCleared;

    public (long Prompt, long Completion) TotalUsage => (_totalPromptTokens, _totalCompletionTokens);

    /// <summary>是否已一键放行（UI 可据此显示状态）。</summary>
    public bool AutoApprovedForSession => _autoApprovedForSession;

    public void SetAutoApprovedForSession(bool value) => _autoApprovedForSession = value;

    /// <summary>清空对话历史，保留 system 提示。</summary>
    public void ResetHistory()
    {
        _history.Clear();
        _history.Add(ChatMessage.System(_options.SystemPrompt));
        HistoryCleared?.Invoke();
    }

    /// <summary>
    /// 把恢复出来的对话装进历史（**替换**掉现有的非 system 消息）。
    ///
    /// 只接受 user 与「不带 tool_calls 的 assistant」——
    /// 这是 <see cref="Session.SessionStore"/> 恢复时就已经过滤好的结果，
    /// 但仍然在这里再挡一次：历史里混进孤立的 tool 消息会让下一次请求直接被服务端拒绝
    /// （协议要求 tool 必须紧跟 tool_calls），而那个错误信息完全指不到「是恢复惹的祸」。
    /// </summary>
    /// <returns>实际装入的条数。</returns>
    public int RestoreHistory(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (_history.Count > 1)
            _history.RemoveRange(1, _history.Count - 1);

        var loaded = 0;

        foreach (var m in messages)
        {
            if (m.Role is not ("user" or "assistant"))
                continue;

            if (m.ToolCalls is { Count: > 0 })
                continue;

            if (string.IsNullOrEmpty(m.Content))
                continue;

            _history.Add(m);
            loaded++;
        }

        // 恢复的历史可能超过 MaxHistoryMessages，直接走一次裁剪，避免下一次请求就超窗。
        TrimHistory();

        return loaded;
    }

    // ------------------------------------------------------------------
    // 主循环
    // ------------------------------------------------------------------

    /// <summary>
    /// 处理一次用户输入，跑完「模型 ↔ 工具」的所有往返。
    /// </summary>
    public async Task<AgentRunResult> RunAsync(
        string userInput,
        IAgentObserver observer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(observer);

        if (string.IsNullOrWhiteSpace(userInput))
            return new AgentRunResult { FinalText = string.Empty, StopReason = "empty_input" };

        var userMessage = ChatMessage.User(userInput);
        _history.Add(userMessage);
        Traced?.Invoke(new AgentTraceEntry { Message = userMessage });
        TrimHistory();

        // 每条用户输入都是一轮新的核验：打回计数从零开始。
        // （作用域是「一次输入」而不是「整个会话」—— 否则聊到第 20 轮时，
        //   模型连改个错别字都会撞上限，用户只会觉得审查员在故意刁难。）
        _verify?.BeginTurn();

        // 记下用户原声。宿主可以不记普通输入（这里兜住了），
        // 但它必须记下那些「不经过模型」的本地命令（/ws cd、/verify on 之类）。
        _userEvents?.Record("用户输入：" + (userInput.Length > 200 ? userInput[..200] + "…" : userInput));

        var toolsSchema = _tools.Count > 0 ? _tools.BuildToolSchema() : (JsonElement?)null;

        var lastText = new StringBuilder();
        var rounds = 0;
        var toolCallCount = 0;
        LlmUsage? lastUsage = null;

        for (var round = 0; round < _options.MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            rounds = round + 1;
            observer.OnRoundBegin(round);

            lastText.Clear();

            LlmTurn turn;
            try
            {
                turn = await _llm.CompleteAsync(
                    _history,
                    toolsSchema,
                    onTextDelta: t =>
                    {
                        lastText.Append(t);
                        observer.OnTextDelta(t);
                    },
                    onReasoningDelta: observer.OnReasoningDelta,
                    ct).ConfigureAwait(false);
            }
            catch (LlmException ex)
            {
                observer.OnStatus("模型调用失败：" + ex.Message);
                throw;
            }

            if (turn.Usage is not null)
            {
                lastUsage = turn.Usage;
                _totalPromptTokens += turn.Usage.PromptTokens;
                _totalCompletionTokens += turn.Usage.CompletionTokens;
                observer.OnUsage(turn.Usage);
            }

            // 把 assistant 这一轮（文字 + 工具调用）回填历史
            var assistantMessage = turn.ToAssistantMessage();
            _history.Add(assistantMessage);
            Traced?.Invoke(new AgentTraceEntry
            {
                Message = assistantMessage,
                Usage = turn.Usage,
                ToolNames = turn.HasToolCalls
                    ? turn.ToolCalls.Select(c => c.Name).ToList()
                    : null,
            });

            if (!turn.HasToolCalls)
            {
                // 没有工具调用 → 本轮对话结束
                return new AgentRunResult
                {
                    FinalText = turn.Text,
                    Rounds = rounds,
                    ToolCallCount = toolCallCount,
                    Usage = lastUsage,
                    StopReason = turn.FinishReason ?? "stop",
                };
            }

            // ------------------------------------------------------------------
            // 双人核验（第二位 AI 审查员）
            //
            // ★ 位置是整条链上最关键的一处：必须在**任何**工具执行与人工报批之前。
            //   两道闸的语义是「审查员先看，用户再点头」。反过来的话，
            //   用户会先批一个审查员可能驳回的动作 —— 白点一次，
            //   而且事后看到「已批准但没执行」只会以为程序坏了。
            // ★ 按**批**核验而不是逐个：同一轮里的 6 次写文件是一个意图，
            //   逐个送审就是 6 次审查员调用，既贵又看不出「这一批要干什么」。
            // ------------------------------------------------------------------
            var batch = await VerifyBatchAsync(turn, observer, ct).ConfigureAwait(false);

            if (batch is { Result.Decision: VerifyDecision.Blocked })
            {
                // 严格模式超限：整批拦截，结束本轮。
                // 每个 tool_call 都必须拿到一条 tool 消息，否则下一次请求会被服务端直接拒绝。
                foreach (var call in turn.ToolCalls)
                    AppendToolMessage(call, batch.Result.Feedback, ok: false);

                TrimHistory();

                observer.OnStatus(batch.Result.Note);

                return new AgentRunResult
                {
                    FinalText = batch.Result.Feedback,
                    Rounds = rounds,
                    ToolCallCount = toolCallCount,
                    Usage = lastUsage,
                    StopReason = "verify_blocked",
                };
            }

            if (batch is { Result.Decision: VerifyDecision.SentBack })
            {
                // 打回：本批动作一个都不执行，把审查意见回填给执行者，让它下一轮改方案。
                var picked = batch.Picked.Select(p => p.Index).ToHashSet();

                // ★ 未送审的动作（同批的只读工具）也必须回填 —— 原因同上：
                //   协议要求每个 tool_call 都有对应消息。而且在语义上它们也确实没执行，
                //   回一句「一并暂缓」比让它谎报结果诚实得多。
                for (var i = 0; i < turn.ToolCalls.Count; i++)
                {
                    var call = turn.ToolCalls[i];

                    var content = picked.Contains(i)
                        ? batch.Result.Feedback
                        : "（同一批次中其他动作的双人核验未通过，本操作一并暂缓执行；" +
                          "待执行者补充说明或变更方案后重新提交核验。）";

                    AppendToolMessage(call, content, ok: false);
                }

                TrimHistory();

                observer.OnStatus(batch.Result.Note);
                _context.Log?.Invoke($"[双人核验] 本批未执行，已把审查意见回填给执行者" +
                                     $"（{batch.Picked.Count}/{turn.ToolCalls.Count} 个动作送审）");

                continue;
            }

            if (batch is not null && batch.Result.Note.Length > 0)
                observer.OnStatus(batch.Result.Note);

            // 逐个执行工具
            foreach (var call in turn.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();

                toolCallCount++;
                var args = call.ParseArguments();

                observer.OnToolCallStarted(call.Name, args);

                var approved = await EnsureApprovedAsync(call, args, observer, ct).ConfigureAwait(false);

                string payload;
                bool toolOk;
                long toolElapsedMs = 0;

                if (!approved)
                {
                    payload = SerializeForModel(ToolResult.Failure(
                        "用户拒绝执行此操作。请勿重复尝试，改为说明你的计划并等待用户指示。"));

                    // 用户拒绝 ≠ 工具失败：耗时记 0，落盘时能一眼区分
                    // 「它跑了但失败了」和「它压根没被允许跑」。
                    toolOk = false;
                    _context.Log?.Invoke($"[拒绝] {call.Name}");
                }
                else
                {
                    var result = await _tools
                        .InvokeAsync(call.Name, _context, args, ct)
                        .ConfigureAwait(false);

                    observer.OnToolCallFinished(call.Name, result);
                    payload = SerializeForModel(result);
                    toolOk = result.Ok;
                    toolElapsedMs = result.ElapsedMs;
                }

                var toolMessage = ChatMessage.ToolResult(call.Id, payload);
                _history.Add(toolMessage);
                Traced?.Invoke(new AgentTraceEntry
                {
                    Message = toolMessage,
                    ToolName = call.Name,
                    ToolOk = toolOk,
                    ElapsedMs = toolElapsedMs,
                });
            }

            TrimHistory();
        }

        return new AgentRunResult
        {
            FinalText = lastText.ToString(),
            Rounds = rounds,
            ToolCallCount = toolCallCount,
            Usage = lastUsage,
            StopReason = "round_limit",
        };
    }

    // ------------------------------------------------------------------
    // 双人核验
    // ------------------------------------------------------------------

    /// <summary>本轮送审的索引 → 用于精确回填「哪些动作被驳回、哪些一并暂缓」。</summary>
    private sealed record BatchVerify(
        VerifyBatchResult Result,
        IReadOnlyList<(int Index, string Name)> Picked);

    /// <summary>
    /// 挑出本轮需要 AI 核验的动作，合成一批送审。返回 null 表示「无需核验」。
    /// </summary>
    private async Task<BatchVerify?> VerifyBatchAsync(
        LlmTurn turn,
        IAgentObserver observer,
        CancellationToken ct)
    {
        if (_verify is not { IsEnabled: true })
            return null;

        var picks = new List<(int Index, string Name, JsonElement Args)>(turn.ToolCalls.Count);

        for (var i = 0; i < turn.ToolCalls.Count; i++)
        {
            var call = turn.ToolCalls[i];

            // ★ 直接问工具自己「你有没有副作用」，而不是查一张手写名单。
            //   Python 版的 SIDE_EFFECT_TOOLS 是一张手写集合，它**漏掉了后来新增的**
            //   ws_bg_cmd / ws_bg_python —— 于是「把危险命令包进后台任务」
            //   曾经是一条能绕过 AI 核验的免费通道，而且完全不报错。
            //   问工具自己之后，新增工具只要老实声明 HasSideEffects 就自动被覆盖。
            var hasSideEffects = _tools.Find(call.Name)?.HasSideEffects ?? false;

            if (!_verify.Options.ShouldVerify(call.Name, hasSideEffects))
                continue;

            picks.Add((i, call.Name, call.ParseArguments()));
        }

        if (picks.Count == 0)
            return null;

        observer.OnStatus(
            $"🧿 双人核验中：{picks.Count} 个动作（审查员 {_verify.Options.Endpoint.Model}）…");

        var request = new ReviewRequest
        {
            Actions = picks.Select(p => (p.Name, p.Args)).ToList(),

            // 最近一条 user 消息 = 「用户到底要什么」。
            UserGoal = LastUserText(),

            // 本轮 assistant 的正文 = 「模型自己说要干什么」。
            // 审查员看得到这段，所以「计划里写着一套、动作里做着另一套」会被抓住。
            AiPlan = turn.Text,

            ContextText = RecentContext(),
            UserEvents = _userEvents?.Render() ?? string.Empty,
            ExtraNote = _verify.BuildExtraNote(
                _context.Guard.Root,
                // ★ 这里**不参与放行判定**，它只生成一段送给审查员的文字：「本批有没有动作被免掉了人工确认」。
                //   逐项放行仍由 EnsureApprovedAsync 对**每个** call 单独调 NeedsApproval 决定：
                //   混合批次里敏感文件那一条照样会弹审批窗（档位 4 则转口令），不会被这里「顺带」放过。
                // ★ 用 Exists 而不是 All 是刻意的：只要有一条被免批，审查员就该按「无人兜底」从严；
                //   换成 All 会在混合批次下漏报「这一批里有东西没人批准过」，反而让监管变松。
                _autoApprovedForSession || (_policy.Level != ApprovalLevel.Strict && picks.Exists(pk => !_policy.NeedsApproval(pk.Name, ToArgMap(pk.Args)).Need)),
                AutoApproveScopeLabel(),
                _policy.DescribeBypass()),
        };

        var result = await _verify.ReviewAsync(request, ct).ConfigureAwait(false);

        return new BatchVerify(result, picks.Select(p => (p.Index, p.Name)).ToList());
    }

    /// <summary>往历史里追加一条 tool 消息并触发落盘。</summary>
    private void AppendToolMessage(ToolCall call, string payload, bool ok)
    {
        var msg = ChatMessage.ToolResult(call.Id, payload);
        _history.Add(msg);
        Traced?.Invoke(new AgentTraceEntry
        {
            Message = msg,
            ToolName = call.Name,
            ToolOk = ok,
            ElapsedMs = 0,
        });
    }

    /// <summary>最近一条用户消息的正文。</summary>
    private string LastUserText()
    {
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role == "user" && !string.IsNullOrWhiteSpace(_history[i].Content))
                return _history[i].Content!;
        }

        return string.Empty;
    }

    /// <summary>
    /// 近期上下文摘要：截尾保近。
    ///
    /// ★ 只取 user / assistant，跳过 system 与 tool —— tool 消息是一大坨 JSON，
    ///   塞进去会挤爆预算，而它对「用户想要什么」几乎不提供信息。
    /// </summary>
    private string RecentContext()
    {
        const int maxMessages = 16;
        const int maxEach = 600;

        var picked = new List<string>(maxMessages);

        for (var i = _history.Count - 1; i >= 0 && picked.Count < maxMessages; i--)
        {
            var m = _history[i];

            if (m.Role is not ("user" or "assistant"))
                continue;

            if (string.IsNullOrWhiteSpace(m.Content))
                continue;

            var text = m.Content.Length <= maxEach
                ? m.Content
                : VerifierPrompts.SafeTruncate(m.Content, maxEach) + "…";

            picked.Add($"[{m.Role}] {text}");
        }

        picked.Reverse();

        return string.Join("\n", picked);
    }

    /// <summary>一键放行档位的文字标签（送给审查员，让它知道「还有没有人兜底」）。</summary>
    private string AutoApproveScopeLabel() => _policy.AutoMode switch
    {
        AutoApproveMode.None => "none",
        AutoApproveMode.All => "all",
        _ => "writes",
    };

    // ------------------------------------------------------------------
    // 审批
    // ------------------------------------------------------------------

    private async ValueTask<bool> EnsureApprovedAsync(
        ToolCall call,
        JsonElement args,
        IAgentObserver observer,
        CancellationToken ct)
    {
        var argMap = ToArgMap(args);
        var verdict = _policy.NeedsApproval(call.Name, argMap);

        if (!verdict.Need)
            return true;

        // 已经一键放行，且本动作不属于「永不放行」名单
        if (_autoApprovedForSession && !_policy.NeverAutoApproves(call.Name, argMap))
        {
            _context.Log?.Invoke($"[自动放行] {call.Name}（本会话已一键放行）");
            return true;
        }

        var allowsOneKey = !_policy.NeverAutoApproves(call.Name, argMap);

        var reason = string.IsNullOrEmpty(verdict.Reason)
            ? "该操作会修改工作台内容"
            : verdict.Reason;

        // 敏感路径给出更具体的原因
        var path = argMap.TryGetValue("path", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

        if (!string.IsNullOrEmpty(path))
        {
            var sens = _policy.DescribeSensitivity(path);
            if (!string.IsNullOrEmpty(sens))
            {
                reason = sens;
                allowsOneKey = false; // 敏感文件永不允许一键放行
            }
        }

        // ★ 档位 4：敏感文件不弹审批窗，改问口令。
        //   Kind 由策略层给出（ApprovalVerdict.Password），UI 据此决定用哪个对话框 ——
        //   判「要不要口令」的规则只在 ApprovalPolicy 一处，界面不自己推断。
        if (verdict.Kind == ApprovalKind.Password)
            allowsOneKey = false;

        var request = new ApprovalRequest(call.Name, argMap, reason, allowsOneKey, verdict.Kind);

        observer.OnStatus($"等待确认：{call.Name}");

        var ok = await observer.RequestApprovalAsync(request, ct).ConfigureAwait(false);

        if (ok && allowsOneKey)
        {
            // 用户选择的是「放行这一次」；一键放行由 UI 显式调用 SetAutoApprovedForSession
        }

        return ok;
    }

    private static Dictionary<string, JsonElement> ToArgMap(JsonElement args)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (args.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var prop in args.EnumerateObject())
            map[prop.Name] = prop.Value;

        return map;
    }

    // ------------------------------------------------------------------
    // 历史维护
    // ------------------------------------------------------------------

    /// <summary>
    /// 裁剪历史。
    ///
    /// ★ 难点：assistant(tool_calls) 与随后的 tool 结果必须成组出现，
    ///   从中间切开会让 API 报 "tool must follow tool_calls"。
    ///   所以裁剪必须在「一条 user 消息」处下刀。
    /// </summary>
    private void TrimHistory()
    {
        if (_history.Count <= _options.MaxHistoryMessages)
            return;

        var overflow = _history.Count - _options.MaxHistoryMessages;

        // 从索引 1（跳过 system）开始找第一个可以安全下刀的位置
        var cut = 1;
        for (var i = 1; i < _history.Count && overflow > 0; i++)
        {
            if (_history[i].Role == "user")
            {
                cut = i;
                overflow -= (i - 1);
                break;
            }
        }

        // 找不到 user 边界（极端情况），保守地只删最早的一条 assistant+tool 整组
        if (cut <= 1)
            return;

        var removed = cut - 1;
        if (removed <= 0)
            return;

        _history.RemoveRange(1, removed);

        _context.Log?.Invoke($"[历史裁剪] 移除最早 {removed} 条消息，当前 {_history.Count} 条");
    }

    // ------------------------------------------------------------------
    // 工具结果 → 给模型看的文本
    // ------------------------------------------------------------------

    private string SerializeForModel(ToolResult result)
    {
        var sb = new StringBuilder(256);

        sb.Append(result.Ok ? "{\"ok\":true" : "{\"ok\":false");

        if (result.Ok)
        {
            if (result.Data is { } data)
            {
                var raw = data.GetRawText();
                if (raw.Length > _options.MaxToolResultChars)
                {
                    sb.Append(",\"data\":");
                    sb.Append(JsonSerializer.Serialize(
                        raw[.._options.MaxToolResultChars] + "\n…（已截断）",
                        Tools.ToolJson.DefaultOptions));
                    sb.Append(",\"truncated\":true");
                }
                else
                {
                    sb.Append(",\"data\":").Append(raw);
                }
            }
        }
        else
        {
            sb.Append(",\"error\":")
              .Append(JsonSerializer.Serialize(result.Error ?? "未知错误", Tools.ToolJson.DefaultOptions));
        }

        if (result.Notice is not null)
        {
            sb.Append(",\"notice\":")
              .Append(JsonSerializer.Serialize(result.Notice, Tools.ToolJson.DefaultOptions));
        }

        sb.Append(",\"elapsed_ms\":").Append(result.ElapsedMs);
        sb.Append('}');

        return sb.ToString();
    }
}
