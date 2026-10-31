using System.Globalization;

namespace FatFish.Core.Llm;

/// <summary>
/// LLM 连接与生成参数。
///
/// <b>键名与 Python 版完全一致</b>（2026-10-29 修正）：
///   取值优先级 <c>FATFISH_*</c> → <c>DEEPSEEK_*</c>（旧写法，向后兼容）→ 内置默认。
/// 与 FATHFISHII.py 第 186~190 行逐字对齐。
///
/// 为什么必须对齐：.env 里 <c>FATFISH_API_KEY</c> 与 <c>DEEPSEEK_API_KEY</c>
/// 是**两个不同的 key**（实测不同值）。C# 版原先只读 <c>DEEPSEEK_API_KEY</c>，
/// 于是两个版本挂在两个账号上，各花各的钱 —— 而且余额只在 Python 侧看得到，
/// C# 侧花了多少完全不可见。这类「配置看起来都对、账单对不上」的问题很难查，
/// 因为两边都跑得好好的。
///
/// 另外两个默认值也一并纠回与 Python 一致：
///   · <c>model</c>：deepseek-chat → <b>deepseek-flash</b>（Python 版主模型）；
///   · <c>max_tokens</c>：8192 → <b>131072</b>（Python 版 MAX_REPLY_TOKENS 的默认值）。
///     8192 会把长回答（尤其整文件代码）从中间切断，而切断后的内容看起来是「完整的」，
///     得等真去编译才发现少了半截。
///   · <c>temperature</c>：1.0 → <b>0.7</b>（Python 版主模型）。
/// </summary>
public sealed class LlmOptions
{
    public required string ApiKey { get; init; }

    /// <summary>API Key 实际来自哪个环境变量（FATFISH_API_KEY / DEEPSEEK_API_KEY）。</summary>
    /// <remarks>
    /// 单独记下来只为了一件事：让人一眼看出「这个进程花的是哪个 key 的钱」。
    /// 排查账单差异时，这一点比 model 名更关键。
    /// </remarks>
    public string ApiKeySource { get; init; } = "FATFISH_API_KEY";

    /// <summary>形如 https://api.deepseek.com（可带 /v1，也可直接给完整 chat 地址）。</summary>
    public string BaseUrl { get; init; } = "https://api.deepseek.com";

    public string Model { get; init; } = "deepseek-flash";

    public double Temperature { get; init; } = 0.7;

    public int MaxTokens { get; init; } = 131072;

    /// <summary>单轮对话的总超时（秒）。流式输出期间按「收到数据的间隔」重置。</summary>
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// 拼接最终的 chat completions 地址。
    ///
    /// 三种输入都兼容：
    ///   https://api.deepseek.com
    ///   https://api.deepseek.com/v1
    ///   https://api.deepseek.com/chat/completions  （已是完整地址）
    /// </summary>
    public string ChatUrl
    {
        get
        {
            var b = BaseUrl.TrimEnd('/');

            if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return b;

            return b + "/chat/completions";
        }
    }

    /// <summary>
    /// 只解析 API Key（不要求其它配置齐全）。
    /// UI / CLI 要在「缺 key 时给一句人话提示」但又不想抛异常的地方用它。
    /// </summary>
    /// <param name="key">解析到的 key。</param>
    /// <param name="sourceKey">来自哪个环境变量名。</param>
    public static bool TryResolveApiKey(out string key, out string sourceKey)
    {
        var fatfish = First("FATFISH_API_KEY");
        if (fatfish is not null)
        {
            key = fatfish;
            sourceKey = "FATFISH_API_KEY";
            return true;
        }

        var deepseek = First("DEEPSEEK_API_KEY");
        if (deepseek is not null)
        {
            key = deepseek;
            sourceKey = "DEEPSEEK_API_KEY";
            return true;
        }

        key = string.Empty;
        sourceKey = string.Empty;
        return false;
    }

    /// <summary>从环境变量读取。缺 key 时抛异常，由上层给出可读提示。</summary>
    public static LlmOptions FromEnvironment()
    {
        if (!TryResolveApiKey(out var key, out var sourceKey))
        {
            throw new InvalidOperationException(
                "未找到 API Key。请在 .env 或系统环境变量中设置 FATFISH_API_KEY" +
                "（旧写法 DEEPSEEK_API_KEY 也认）后再启动。");
        }

        return new LlmOptions
        {
            ApiKey = key,
            ApiKeySource = sourceKey,
            BaseUrl = First("FATFISH_BASE_URL", "DEEPSEEK_BASE_URL") ?? "https://api.deepseek.com",
            Model = First("FATFISH_MODEL", "DEEPSEEK_MODEL") ?? "deepseek-flash",

            Temperature = FirstDouble(0.7, "FATFISH_TEMPERATURE", "DEEPSEEK_TEMPERATURE"),

            // MAX_REPLY_TOKENS 是 Python 版的名字，排在里面是为了「一份 .env 两边都生效」。
            MaxTokens = FirstInt(131072, "FATFISH_MAX_TOKENS", "MAX_REPLY_TOKENS", "DEEPSEEK_MAX_TOKENS"),

            TimeoutSeconds = FirstInt(300, "FATFISH_TIMEOUT", "DEEPSEEK_TIMEOUT"),
        };
    }

    /// <summary>日志用：绝不含 API key 本体，但会写明它来自哪个键。</summary>
    public string Describe()
        => $"model={Model} base={BaseUrl} max_tokens={MaxTokens} temp={Temperature} " +
           $"timeout={TimeoutSeconds}s key={ApiKeySource}(***{Tail(ApiKey)})";

    private static string Tail(string s) => s.Length <= 4 ? "****" : s[^4..];

    // ---------- 取值：按名字顺序取第一个非空 ----------

    private static string? First(params string[] names)
    {
        foreach (var name in names)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return null;
    }

    private static int FirstInt(int fallback, params string[] names)
    {
        foreach (var name in names)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n;
        }

        return fallback;
    }

    private static double FirstDouble(double fallback, params string[] names)
    {
        foreach (var name in names)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
        }

        return fallback;
    }
}
