using System.Globalization;
using FatFish.Core.Llm;

namespace FatFish.Core.Safety;

/// <summary>双人核验的模式（对应 VERIFY_MODE）。</summary>
public enum VerifyMode
{
    /// <summary>关闭核验，只剩人工报批一道闸。</summary>
    Off,

    /// <summary>只核验「有副作用」的动作（默认）。</summary>
    Auto,

    /// <summary>连只读动作也核验（铁壁模式，token 消耗显著上升）。</summary>
    All,
}

/// <summary>核验服务不可用时的策略（对应 VERIFY_FAIL_MODE）。</summary>
public enum VerifyFailMode
{
    /// <summary>fail-open：放行，只标记 failed。人工报批照常兜底。</summary>
    Open,

    /// <summary>fail-closed：拦截。适合「宁可什么都干不了，也不让未审动作执行」的场景。</summary>
    Closed,
}

/// <summary>
/// 审查员的连接参数。
///
/// ★ 刻意与主模型**分开**，且 <see cref="VerifyOptions.FromEnvironment"/> 里
///   留空时回落到主模型配置。原因见 docs/Verify.md「异源核验」一节：
///   同一个模型审自己，等于自己给自己签字 —— 它的系统性偏差会被原样复制一遍。
///   但「留空回落」又是必要的：否则换模型时忘了改 VERIFIER_MODEL，
///   核验环节会直接 401/404，而主模型跑得好好的，很难想到是这里。
/// </summary>
public sealed record VerifierEndpoint
{
    public required string ApiKey { get; init; }

    /// <summary>key 来自哪个环境变量（排障用；绝不打印 key 本体）。</summary>
    public string ApiKeySource { get; init; } = "VERIFIER_API_KEY";

    public string BaseUrl { get; init; } = "https://api.deepseek.com";

    public string Model { get; init; } = "deepseek-flash";

    /// <summary>审查员要冷静：默认 0.2，明显低于主模型的 0.7。</summary>
    public double Temperature { get; init; } = 0.2;

    /// <summary>
    /// 审查意见的输出上限。
    ///
    /// ★ 2000 而不是跟着主模型的 131072：审查员的输出格式被限制成「整段不超过 5 行」，
    ///   给再多也没用；而一个失控的输出（比如它开始长篇复述送审代码）会既慢又贵。
    ///   Python 版从 1200 调到这里，是因为输入变多后它能给出更细的「要求」条目。
    /// </summary>
    public int MaxTokens { get; init; } = 2000;

    /// <summary>单次核验的超时（秒）。</summary>
    public int TimeoutSeconds { get; init; } = 120;

    public string ChatUrl
    {
        get
        {
            var b = BaseUrl.TrimEnd('/');
            return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? b
                : b + "/chat/completions";
        }
    }

    /// <summary>日志用：绝不含 key 本体。</summary>
    public string Describe()
        => $"{Model} @ {BaseUrl} (key={ApiKeySource}(***{Tail(ApiKey)}))";

    private static string Tail(string s) => s.Length <= 4 ? "****" : s[^4..];
}

/// <summary>
/// 双人核验的全部可调参数。
///
/// 键名与 Python 版 <c>FATHFISHII.py</c> 第 271~306 行**逐字对齐**，
/// 目的只有一个：一份 <c>.env</c> 两个版本都能用。
/// 这一点在 LlmOptions 上已经吃过一次亏（两个版本挂在两个 key 上，各花各的钱），
/// 所以这里每一个键名都对着 Python 源码抄，不「优化」成更顺手的名字。
/// </summary>
/// <remarks>
/// 长度上限的单位是**字符**而不是 token（中文约 1 字符 ≈ 1.5 token）。
/// 选「字符」是因为它可被精确计算，不需要 tokenizer —— 而审查员的预算必须可控，
/// 否则一次送审就能把 64k 上下文的费用翻倍。
/// </remarks>
public sealed class VerifyOptions
{
    // ---------- 开关与策略 ----------
    /// <summary>
    /// 核验模式。
    ///
    /// ★ 这是本类里**唯一可写**的属性，因为 <c>/verify on|off|all</c> 必须立即生效。
    ///   其余字段都是 <c>init</c>：它们决定预算与端点，中途改动只会让
    ///   「这一轮按新预算拼、上一轮按旧预算拼」的材料长度对不上，
    ///   排查时极难判断审查员到底看到了多少。
    /// </summary>
    public VerifyMode Mode { get; set; } = VerifyMode.Auto;

    /// <summary>超限后：true=整批拦截；false=放行走人工报批。</summary>
    public bool Strict { get; init; } = true;

    /// <summary>同一轮内允许「打回重交」的次数（仅 revise 计入）。</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// supplement（要求补充资料）的免费轮数上限，不计入退回次数。
    ///
    /// ★ 为什么要有这个「免费额度」：revise 意味着「你错了」，supplement 意味着
    ///   「你说得不够清楚」。把后者按前者计次，会让「执行者老实承认信息不足」
    ///   变成一种惩罚 —— 于是它下次会倾向于**编一段说明**来换取通过，
    ///   而这恰恰是最危险的：审查员被一段漂亮的谎言说服了。
    ///   但完全不计次又会让审查员无限索取下去（原文注释写的就是「防审查员无限索取」），
    ///   所以是「4 次免费，之后按 revise 算」。
    /// </remarks>
    public int MaxSupplements { get; init; } = 4;

    /// <summary>是否连最终答复也复核（默认关：成本翻倍，收益不稳定）。</summary>
    public bool VerifyFinalAnswer { get; init; }

    public VerifyFailMode FailMode { get; init; } = VerifyFailMode.Open;

    // ---------- 审查员 ----------
    public required VerifierEndpoint Endpoint { get; init; }

    // ---------- 送审材料长度上限 ----------
    public int PromptMaxChars { get; init; } = 64_000;
    public int ActionPreview { get; init; } = 4_000;
    public int ReplacePreview { get; init; } = 2_000;
    public int GoalMaxChars { get; init; } = 6_000;

    /// <summary>「执行者自述/计划」的上限 —— 方案的核心，所以给得最大。</summary>
    public int PlanMaxChars { get; init; } = 16_000;

    public int ContextMaxChars { get; init; } = 24_000;
    public int AnswerMaxChars { get; init; } = 24_000;
    public int AnswerCtxChars { get; init; } = 12_000;

    // ---------- 判定 ----------

    public bool IsEnabled => Mode != VerifyMode.Off;

    /// <summary>
    /// 某个工具调用是否该送 AI 核验。
    /// </summary>
    /// <param name="toolName">工具名。</param>
    /// <param name="hasSideEffects">
    /// 该工具自己声明的「有副作用」标志（由调用方从 <c>ITool.HasSideEffects</c> 现取）。
    ///
    /// ★ 为什么是参数而不是内置一张名单：Python 版的 <c>SIDE_EFFECT_TOOLS</c> 是手写集合，
    ///   它**漏掉了后来新增的 ws_bg_cmd / ws_bg_python** —— 换句话说，
    ///   「把危险命令包进后台任务」曾经是一次能绕过 AI 核验的免费通道。
    ///   C# 版改成直接问工具自己，新增工具只要老实声明 HasSideEffects，
    ///   就自动进入核验范围，不需要回来改第二张表。
    /// </param>
    public bool ShouldVerify(string toolName, bool hasSideEffects)
    {
        if (!IsEnabled)
            return false;

        return Mode == VerifyMode.All || hasSideEffects;
    }

    /// <summary>状态行摘要（<c>/verify</c> 与启动横幅用）。</summary>
    public string Describe()
    {
        if (!IsEnabled)
            return "OFF（/verify on 开启）";

        var scope = Mode == VerifyMode.All ? "ALL（含只读）" : "AUTO（仅有副作用动作）";

        return $"ON · {scope} · 审查员 {Endpoint.Model} · " +
               $"{(Strict ? "严格拦截" : "宽松放行")} · " +
               $"重试上限 {MaxRetries} · 补充上限 {MaxSupplements} · " +
               $"故障时{(FailMode == VerifyFailMode.Closed ? "拦截" : "放行")}";
    }

    /// <summary>送审长度上限的一行摘要（排障时看「为什么审查员没说那句话」）。</summary>
    public string LimitsLabel()
        => $"总 {PromptMaxChars} ｜ 计划 {PlanMaxChars} ｜ 需求 {GoalMaxChars} ｜ " +
           $"动作 {ActionPreview} ｜ 上下文 {ContextMaxChars} ｜ 输出 {Endpoint.MaxTokens} tok";

    // ------------------------------------------------------------------
    // 从环境变量装配
    // ------------------------------------------------------------------

    /// <summary>
    /// 从环境变量读取。<paramref name="main"/> 是主模型配置，用作审查员的回落值。
    /// </summary>
    public static VerifyOptions FromEnvironment(LlmOptions main)
        => FromLookup(Environment.GetEnvironmentVariable, main);

    /// <summary>
    /// 从任意「键 → 值」来源读取。
    ///
    /// ★ 单独留出这个入口是为了**可测**：环境变量是进程级全局状态，
    ///   测试里改它会影响同时在跑的其他测试类（xUnit 默认并行执行）。
    ///   有了它，配置解析的全部组合都能用字典喂进去，互不干扰。
    ///   宿主也可以用它从配置文件读 —— 所以它是 public 的。
    /// </summary>
    public static VerifyOptions FromLookup(Func<string, string?> read, LlmOptions main)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(main);

        string? First(string name)
        {
            var v = read(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        bool FirstBool(bool fallback, string name)
        {
            var v = First(name);
            return v is null ? fallback : v.ToLowerInvariant() is not ("0" or "false" or "no" or "off");
        }

        int FirstInt(int fallback, int floor, string name)
        {
            var v = First(name);
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return fallback;

            return n >= floor ? n : fallback;
        }

        double FirstDouble(double fallback, string name)
        {
            var v = First(name);
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return fallback;

            return Math.Clamp(d, 0.0, 2.0);
        }

        var keyRaw = First("VERIFIER_API_KEY") ?? string.Empty;

        // 与 Python 版同一条保护：VERIFIER_API_KEY 若不是个像样的 key（比如填了个 URL、
        // 或者复制时截断了），就**回落到主 key**，而不是拿一份坏 key 去发请求。
        // 坏 key 的报错是 401，看起来像「审查员挂了」，实际是配置写错了。
        var keyBad = keyRaw.Length > 0 && !LooksLikeKey(keyRaw);
        var useMainKey = keyBad || keyRaw.Length == 0;

        var endpoint = new VerifierEndpoint
        {
            ApiKey = useMainKey ? main.ApiKey : keyRaw,
            ApiKeySource = useMainKey ? main.ApiKeySource : "VERIFIER_API_KEY",
            BaseUrl = First("VERIFIER_BASE_URL") ?? main.BaseUrl,
            Model = First("VERIFIER_MODEL") ?? main.Model,
            Temperature = FirstDouble(0.2, "VERIFIER_TEMPERATURE"),

            // 0 在 Python 版里表示「不覆盖」：这里同样把 0 当成缺省，
            // 否则 VERIFIER_MAX_TOKENS=0 会让审查员一个字都说不出来。
            MaxTokens = FirstInt(2000, 1, "VERIFIER_MAX_TOKENS"),
            TimeoutSeconds = FirstInt(120, 1, "VERIFIER_TIMEOUT"),
        };

        return new VerifyOptions
        {
            Mode = ParseMode(First("VERIFY_MODE")),
            Strict = FirstBool(true, "VERIFY_STRICT"),

            // 0 是**有效值**（表示「一次都不打回」），所以 floor 用 0。
            MaxRetries = FirstInt(2, 0, "VERIFY_MAX_RETRIES"),
            MaxSupplements = FirstInt(4, 0, "VERIFY_MAX_SUPPLEMENTS"),

            VerifyFinalAnswer = FirstBool(false, "VERIFY_FINAL_ANSWER"),
            FailMode = string.Equals(First("VERIFY_FAIL_MODE"), "closed", StringComparison.OrdinalIgnoreCase)
                ? VerifyFailMode.Closed
                : VerifyFailMode.Open,

            Endpoint = endpoint,

            PromptMaxChars = FirstInt(64_000, 2000, "VERIFIER_PROMPT_CHARS"),
            ActionPreview = FirstInt(4_000, 200, "VERIFIER_ACTION_PREVIEW"),
            ReplacePreview = FirstInt(2_000, 200, "VERIFIER_REPLACE_PREVIEW"),
            GoalMaxChars = FirstInt(6_000, 200, "VERIFIER_GOAL_CHARS"),
            PlanMaxChars = FirstInt(16_000, 200, "VERIFIER_PLAN_CHARS"),
            ContextMaxChars = FirstInt(24_000, 200, "VERIFIER_CONTEXT_CHARS"),
            AnswerMaxChars = FirstInt(24_000, 200, "VERIFIER_ANSWER_CHARS"),
            AnswerCtxChars = FirstInt(12_000, 200, "VERIFIER_ANSWER_CTX"),
        };
    }

    /// <summary>构造一个「全关」的配置（工具、测试、宿主的默认值）。</summary>
    public static VerifyOptions Disabled(LlmOptions main) => new()
    {
        Mode = VerifyMode.Off,
        Endpoint = new VerifierEndpoint
        {
            ApiKey = main.ApiKey,
            ApiKeySource = main.ApiKeySource,
            BaseUrl = main.BaseUrl,
            Model = main.Model,
        },
    };

    /// <summary>
    /// 长得像不像一个 API key。
    ///
    /// 不用「必须以 sk- 开头」那种严格判定：审查员完全可能挂在别家服务上，
    /// 那种 key 的格式五花八门。这里只排除明显的配置错误 ——
    /// 带空格、带 http://、短得离谱。误放行一个怪格式 key 的代价是 401；
    /// 误判一个正常 key 的代价是「悄悄用了主模型的 key」，后者难查得多。
    /// </summary>
    private static bool LooksLikeKey(string s)
        => s.Length >= 20
           && !s.Contains(' ')
           && !s.Contains("http", StringComparison.OrdinalIgnoreCase);

    private static VerifyMode ParseMode(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "off" or "0" or "false" or "no" => VerifyMode.Off,
        "all" or "full" => VerifyMode.All,
        _ => VerifyMode.Auto,
    };

}
