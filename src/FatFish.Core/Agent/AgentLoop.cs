using System.Text;
using System.Text.Json;
using FatFish.Core.Llm;
using FatFish.Core.Safety;
using FatFish.Core.Tools;

namespace FatFish.Core.Agent;

/// <summary>编排层配置。</summary>
public sealed class AgentOptions
{
    /// <summary>
    /// 单轮用户输入内，最多允许几轮「模型 ↔ 工具」往返。
    /// </summary>
    /// <remarks>
    /// ★ 2026-11-06 起语义是 <b>可选覆盖</b>：
    ///   <c>&lt;= 0</c>（默认）＝ 听 <see cref="ApprovalPolicy.RoundLimit"/> 的，
    ///   也就是顶栏「审批」栏里那个可运行时切换的上限（15 / 25 / 35 / 50 / 100 / 不限）。
    ///   <c>&gt; 0</c> ＝ 构造期硬上限，压过策略。
    ///
    /// ★ 为什么保留这个「硬上限」而不是干脆删掉：
    ///   CLI 与单测不接 UI，也不该被某台机器上的用户偏好影响 ——
    ///   一条「跑 4 轮必然撞顶」的测试如果哪天被人把偏好改成 100，
    ///   就会挂在一个和它要验证的东西完全无关的地方。
    /// </remarks>
    public int MaxRounds { get; init; }

    /// <summary>
    /// 单条工具结果回填给模型的字符上限（超出则**头尾各留一半**，不是只留头）。
    /// </summary>
    /// <remarks>
    /// ★ 2026-11-05 从 30_000 提到 100_000。
    ///   原先那个数是在「窗口紧张、只能省着用」的前提下定的；
    ///   而 deepseek-flash（V4.1-Flash）的窗口是 1M、历史预算 550K ——
    ///   一条工具结果占 10 万字符（中文约 3 万 token）完全放得下，
    ///   没必要让模型带着残缺信息干活。
    /// ★ 仍保留上限：`dotnet build` 的完整日志能有几 MB，
    ///   无上限等于让一次工具调用把整个窗口挤爆。
    ///   完整内容由工具自己落盘并提供 full_output 路径，模型按需去取。
    /// </remarks>
    public int MaxToolResultChars { get; init; } = 100_000;

    /// <summary>
    /// 历史保留的 token 预算（按 <see cref="ChatMessage.EstimateTokens"/> 粗略估算）。
    /// <c>0</c> = 不按 token 裁剪。
    /// </summary>
    /// <remarks>
    /// ★ 2026-11-05：条数上限（原 <c>MaxHistoryMessages = 200</c>）已撤除，只认 token。
    ///   理由：条数不是资源。200 条可能是 5k token，也可能是 600 万字符 ——
    ///   同一个数字对应两种极端，按它裁决必然一边浪费一边爆窗。
    /// ★ 默认 550000：一轮实测 300K 上下文的会话说明正常用量远低于此，
    ///   它实际等于「不裁」，只在极长会话或一次性灌入超大文件时兜底。
    ///   与输出上限相加仍在 deepseek-flash 的 1M 窗口内（550K + 262K + 系统提示 ≈ 815K）。
    /// ★ 可用环境变量 <c>FATFISH_MAX_HISTORY_TOKENS</c> 覆盖。
    /// </remarks>
    public int MaxHistoryTokens { get; init; } = ReadInt("FATFISH_MAX_HISTORY_TOKENS", 550_000);

    /// <summary>是否在日志里打印每一轮的摘要。</summary>
    public bool Verbose { get; init; }

    /// <summary>系统提示词。</summary>
    public string SystemPrompt { get; init; } = DefaultSystemPrompt;

    private static int ReadInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

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
    /// <summary>
    /// 当前在用的模型客户端。
    ///
    /// ★ 为什么不是 readonly（2026-11-06）：热重载要能在**不重建 AgentLoop**的前提下换掉它。
    ///   重建 AgentLoop 会丢掉历史、会话订阅和累计用量 —— 「换个模型」不该等于「开新会话」。
    ///   所以历史/计数器留在这里，只有「往哪发请求」这一件事被换掉。
    /// </summary>
    private DeepSeekClient _llm;
    private readonly ToolRegistry _tools;
    private readonly ToolContext _context;
    private readonly AgentOptions _options;
    private readonly ApprovalPolicy _policy;

    /// <summary>双人核验（第二位 AI 审查员）。为 null 或关闭时只剩人工报批一道闸。</summary>
    private VerifySession? _verify;

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

    /// <summary>当前在用的模型客户端（热重载后更换）。</summary>
    public DeepSeekClient Llm => _llm;

    /// <summary>
    /// 换一个模型客户端，<b>不动历史、不动用量、不通知会话</b>。
    ///
    /// 调用时机：热重载配置之后（见 <c>EnvRuntime.Reload</c>）。
    /// 安全边界：本方法只保证「下一次请求走新的客户端」；
    /// 正在生成的那一轮仍在用旧客户端跑完 —— 那是刻意的，
    /// 中途换掉会让同一条回答前后来自两个模型，比不换更糟。
    /// </summary>
    public void SwapLlm(DeepSeekClient llm)
        => _llm = llm ?? throw new ArgumentNullException(nameof(llm));

    /// <summary>换掉审查员（热重载后 VERIFIER_* 变了时用）。传 null = 关闭核验。</summary>
    public void SwapVerify(VerifySession? verify) => _verify = verify;

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

        // 恢复的历史可能已经超过 token 预算，直接走一次裁剪，避免下一次请求就超窗。
        TrimHistory();

        return loaded;
    }

    /// <summary>
    /// 直接往历史里塞一段「用户侧上下文」，不触发模型回合。
    /// </summary>
    /// <remarks>
    /// 用途：宿主把文件原文 / 文件摘要放进上下文，但不想立刻花一次模型调用、
    /// 也不想让模型马上开口说话。走和 RunAsync 同一条 Traced 落盘路径，
    /// 所以会话恢复时这段内容还在。
    ///
    /// ★ 只追加 user 消息：历史末尾若停在 assistant(tool_calls) 上再插 user，
    ///   下一次请求会被服务端以「tool 必须紧跟 tool_calls」拒绝。
    ///   调用方（界面）只在回合结束、不在运行中时才会走到这里。
    /// </remarks>
    /// <returns>注入后的历史条数。</returns>
    public int InjectContext(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return _history.Count;

        var message = ChatMessage.User(text);

        _history.Add(message);
        Traced?.Invoke(new AgentTraceEntry { Message = message });
        TrimHistory();

        return _history.Count;
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

        // ★ 上限每轮重新读一次：用户可以在跑到一半时从顶栏「审批」栏改
        //   （15 / 25 / 35 / 50 / 100 / 不限），下一轮立刻按新值判断 ——
        //   与审批档位同一套「运行时可变」的理由。
        //   MaxRounds > 0 时是构造期硬上限（CLI / 单测用），压过策略。
        var roundLimitHit = 0;

        for (var round = 0; ; round++)
        {
            var limit = EffectiveRoundLimit();

            if (limit > 0 && round >= limit)
            {
                roundLimitHit = limit;
                break;
            }

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
                // ★ 失败要写进历史，否则下一轮模型以为上一轮是正常结束的。
                //   一行就够：只报「失败了 + 为什么」，不往历史里灌提示词。
                //   合并进最后一条 user/tool 而不是新增，是为了避免连续两条 user。
                var failureNotice = $"[系统通知] 上一轮调用失败：{ex.Message}";

                if (_history.Count > 0 && _history[^1].Role is "user" or "tool")
                {
                    var lastMessage = _history[^1];
                    _history[^1] = lastMessage with
                    {
                        Content = (lastMessage.Content ?? string.Empty) + "\n\n" + failureNotice,
                    };
                }
                else
                {
                    var failureMessage = ChatMessage.User(failureNotice);
                    _history.Add(failureMessage);
                    Traced?.Invoke(new AgentTraceEntry { Message = failureMessage });
                }

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

        // 走到这里只有一种可能：循环被上限打断（limit > 0 且 round 已到）。
        // 「不限轮数」时循环只会在 return / 异常 / 取消时离开，不会落到这里。
        if (roundLimitHit > 0)
        {
            observer.OnStatus(
                $"[上限] 已达本轮 {ApprovalPolicy.RoundLimitName(roundLimitHit)}上限，已停止。" +
                "可在顶栏「审批」栏调高，或设为「不限轮数」后继续。");

            _context.Log?.Invoke(
                $"[上限] 单轮回合数达到 {roundLimitHit}（顶栏「审批」栏可改）");
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

    /// <summary>
    /// 本次实际生效的回合上限：<see cref="AgentOptions.MaxRounds"/> &gt; 0 时用它（构造期硬上限），
    /// 否则听 <see cref="ApprovalPolicy.RoundLimit"/>（顶栏可改，0 = 不限）。
    /// </summary>
    /// <remarks>
    /// ★ 做成方法而不是构造时算一次：上限是运行时偏好，
    ///   在构造期定死就等于「改完必须重启」—— 那正是这个功能要避免的。
    /// </remarks>
    private int EffectiveRoundLimit()
        => _options.MaxRounds > 0 ? _options.MaxRounds : _policy.RoundLimit;

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

        // ★ 按业务分域（2026-11-05）：决定这一批**送哪些材料**、以及给审查员**哪一套判据**。
        //   原来每一批都送同一套素材（含几千字的近期上下文），不管这批是要删文件还是搜资料 ——
        //   既贵，又给了审查员多余的「可以自由发挥」的原料，那正是幻觉的来源。
        var profile = VerifyProfiles.For(
            VerifyProfiles.Classify(picks.Select(p => (p.Name, p.Args)).ToList()));

        var request = new ReviewRequest
        {
            Actions = picks.Select(p => (p.Name, p.Args)).ToList(),

            // 最近一条 user 消息 = 「用户到底要什么」。
            UserGoal = LastUserText(),

            // 本轮 assistant 的正文 = 「模型自己说要干什么」。
            // 审查员看得到这段，所以「计划里写着一套、动作里做着另一套」会被抓住。
            AiPlan = turn.Text,

            // ★ 不是「给不给上下文」，而是「给几条」：多轮任务里「用户到底要干什么」
            //   经常不在最后一句里（先「看看目录结构」、再「把那个配置改一下」），
            //   所以 Exec / FileWrite 各给最近 4 条，联网 2 条，动工作台本身 0 条。
            ContextText = VerifyProfiles.LimitContext(RecentContextMessages(), profile),
            UserEvents = profile.WantUserEvents ? (_userEvents?.Render() ?? string.Empty) : string.Empty,
            ExtraNote = _verify.BuildExtraNote(
                _context.Guard.Root,
                // ★ 这里**不参与放行判定**，它只生成一段送给审查员的文字：「本批有没有动作被免掉了人工确认」。
                //   逐项放行仍由 EnsureApprovedAsync 对**每个** call 单独调 NeedsApproval 决定：
                //   混合批次里敏感文件那一条照样会弹审批窗（档位 4 则转口令），不会被这里「顺带」放过。
                // ★ 用 Exists 而不是 All 是刻意的：只要有一条被免批，审查员就该按「无人兜底」从严；
                //   换成 All 会在混合批次下漏报「这一批里有东西没人批准过」，反而让监管变松。
                _autoApprovedForSession || (_policy.Level != ApprovalLevel.Strict && picks.Exists(pk => !_policy.NeedsApproval(pk.Name, ToArgMap(pk.Args)).Need)),
                AutoApproveScopeLabel(),
                // 该域判据 + 读凭证覆盖率 一起塞进「送审上下文」那一节。
                // 那一节在提示词里有 800 字符预算，所以三段都刻意写得很短。
                profile.Instruction
                + VerifyProfiles.CoverageNote(turn.ToolCalls, _context)
                + _policy.DescribeBypass()),
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
    /// 近期上下文摘要：截尾保近，返回**逐条**消息。
    ///
    /// ★ 只取 user / assistant，跳过 system 与 tool —— tool 消息是一大坨 JSON，
    ///   塞进去会挤爆预算，而它对「用户想要什么」几乎不提供信息。
    /// ★ 返回列表而不是拼接好的长串（2026-11-06 修正）：
    ///   拼成串之后上层只能按 '\n' 切，无法区分「一条多行回答」和「几十条消息」，
    ///   于是「按域只给 N 条上下文」会静默退化成「只给最后 N 行」。
    /// </summary>
    private List<string> RecentContextMessages()
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

        return picked;
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

    /// <summary>一次裁剪的计划：从 <paramref name="Cut"/> 开始保留，前面 Cut-1 条被省略。</summary>
    public readonly record struct TrimPlan(int Cut, int DroppedCount, int DroppedTokens)
    {
        public static TrimPlan None => new(1, 0, 0);

        public bool Any => Cut > 1;
    }

    /// <summary>「已省略」声明的开头。用来识别「历史里已经有一条声明」，避免越堆越多。</summary>
    internal const string OmittedNoticePrefix = "【已省略更早的";

    /// <summary>
    /// 纯函数：算出「从哪一条开始保留」。返回 Cut &lt;= 1 表示不需要裁剪。
    /// </summary>
    /// <remarks>
    /// ★ 为什么是纯函数：裁剪的下刀点出过错（旧实现找的是「第一条 user」，
    ///   而 <c>_history[1]</c> 恒为第一条 user，于是 <c>removed</c> 永远是 0 ——
    ///   六个调用点每轮都在调，每一次都什么也没做，历史只增不减）。
    ///   抽成纯函数之后，边界情况可以被单元测试直接钉住，不必等到线上撞墙。
    /// ★ 唯一的硬约束是协议安全：assistant(tool_calls) 与随后的 tool 结果必须成组出现，
    ///   所以下刀点只能落在「一条 user 消息」上。
    ///   找不到安全落点时返回 None（宁可不裁），也绝不发出会引发
    ///   "tool must follow tool_calls" 的非法请求。
    /// </remarks>
    public static TrimPlan PlanTrim(IReadOnlyList<ChatMessage> history, int maxTokens)
    {
        if (history.Count <= 1 || maxTokens <= 0)
            return TrimPlan.None;

        // 从最新往旧累加，累加爆掉的那一条之前，就是保留窗口的起点。
        var target = 1;
        var acc = 0;

        for (var i = history.Count - 1; i >= 1; i--)
        {
            acc += history[i].EstimateTokens();

            if (acc > maxTokens)
            {
                target = i + 1;
                break;
            }
        }

        if (target <= 1)
            return TrimPlan.None;

        // 跳过既有的省略声明：它自己不该成为下刀点，否则声明会被一轮轮往前推。
        while (target < history.Count && IsOmittedNotice(history[target]))
            target++;

        var cut = -1;

        // 首选：第一条 user —— 在 user 处下刀，tool 消息永远不会与 tool_calls 分离。
        for (var i = target; i < history.Count; i++)
        {
            if (history[i].Role == "user")
            {
                cut = i;
                break;
            }
        }

        // 兜底：历史里已经没有 user 了，退到「第一条不是 tool 的消息」。
        if (cut < 0)
        {
            for (var i = target; i < history.Count; i++)
            {
                if (history[i].Role != "tool")
                {
                    cut = i;
                    break;
                }
            }
        }

        if (cut <= 1)
            return TrimPlan.None;

        var droppedTokens = 0;

        for (var i = 1; i < cut; i++)
            droppedTokens += history[i].EstimateTokens();

        return new TrimPlan(cut, cut - 1, droppedTokens);
    }

    /// <summary>这条消息是不是我们自己插入的「已省略」声明。</summary>
    private static bool IsOmittedNotice(ChatMessage m)
        => m.Content is { Length: > 0 } text
           && text.StartsWith(OmittedNoticePrefix, StringComparison.Ordinal);

    /// <summary>
    /// 按 token 预算裁剪历史。
    /// </summary>
    /// <remarks>
    /// ★ 裁掉之后必须**告诉模型它忘了什么**：一条不声明的裁剪，
    ///   换来的不是「省了一笔」，而是下一轮开始凭空编造「你之前说过……」。
    ///   所以这里插入一条 user 消息，说明省略了多少条、原文在哪、以及「不要假装记得」。
    ///   会话全文本来就落在 logs/sessions，模型可以用 ws_search 回去查 ——
    ///   保住的是可及性，不是把它塞回上下文。
    /// ★ 只保留一条声明：下一轮裁剪会把它一起删掉再插新的，不会累积。
    /// </remarks>
    private void TrimHistory()
    {
        var plan = PlanTrim(_history, _options.MaxHistoryTokens);

        if (!plan.Any)
            return;

        _history.RemoveRange(1, plan.Cut - 1);

        _history.Insert(1, ChatMessage.User(
            $"{OmittedNoticePrefix} {plan.DroppedCount} 条对话（约 {plan.DroppedTokens} tokens）。" +
            "原文在 logs/sessions 的会话文件里；需要时用 ws_search 在那个文件里查关键词。" +
            "不要假装记得被省略的内容。】"));

        _context.Log?.Invoke(
            $"[历史裁剪] 省略 {plan.DroppedCount} 条（约 {plan.DroppedTokens} tokens），" +
            $"保留 {_history.Count} 条");
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
                    // ★ 头尾各半，而不是只留头（2026-11-05 改）。
                    //   尾部承载的是「结论」：退出码、traceback、汇总统计，
                    //   以及工具新增的 full_output 路径 —— 它排在 JSON 末尾，
                    //   只留头的话第一个被切掉的就是它。
                    //   只留头会让模型把「一屏正常日志」读成「一切正常」，方向正好是反的。
                    var half = _options.MaxToolResultChars / 2;

                    sb.Append(",\"data\":");
                    sb.Append(JsonSerializer.Serialize(
                        raw[..half]
                        + $"\n……（已省略 {raw.Length - half * 2:N0} 字符；"
                        + "完整内容请用结果里的 full_output 路径查，或在原文件里 ws_search）……\n"
                        + raw[^half..],
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
