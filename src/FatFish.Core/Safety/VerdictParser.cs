using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FatFish.Core.Safety;

/// <summary>审查员的一次裁决。</summary>
/// <remarks>
/// 对应 Python 版 <c>verify_tools.parse_verdict()</c> 返回的那个 dict。
/// 用 record 而不是 enum + 散装字段，是因为「裁决 + 理由 + 要求 + 风险」
/// 必须作为**一整块**在层间传递 —— 拆开传就会出现「回填时漏了 demands」
/// 这种既不报错、又让执行者无从下手的缺口。
/// </remarks>
public sealed record Verdict
{
    /// <summary>approve / supplement / revise。</summary>
    public required string Value { get; init; }

    /// <summary>high / medium / low（仅用于展示与日志，不参与判定）。</summary>
    public string Severity { get; init; } = "medium";

    public string Reason { get; init; } = string.Empty;

    /// <summary>要求执行者补充或修改的具体条目。</summary>
    public IReadOnlyList<string> Demands { get; init; } = Array.Empty<string>();

    /// <summary>更优的替代方案。</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = Array.Empty<string>();

    /// <summary>审查员识别到的风险点。</summary>
    public IReadOnlyList<string> Risks { get; init; } = Array.Empty<string>();

    /// <summary>是否走了降级解析（JSON 失败 / 关键词兜底 / 完全解析不出）。</summary>
    public bool Degraded { get; init; }

    /// <summary>审查员原始输出（排障用，不参与展示）。</summary>
    public string Raw { get; init; } = string.Empty;

    public bool Approved => Value == "approve";

    public string Label => Value switch
    {
        "approve" => "通过",
        "supplement" => "需补充说明",
        "revise" => "需变更方案",
        _ => Value,
    };
}

/// <summary>
/// 裁决解析器 —— 移植自 Python 版 <c>verify_tools.parse_verdict()</c>。
///
/// 为什么解析器这么「宽容」
/// ----------------------
/// 审查员(另一个模型)的输出形态是不可能被完全约束住的。实测见过：
///   · 严格 JSON、```json 包裹的 JSON、前后带一段解释的 JSON；
///   · JSON 字符串里写裸换行（标准解析器直接报错）；
///   · 干脆不写 JSON，就写一个 <c>y</c>；
///   · 写「变更：目标目录不对」这种一行流。
///
/// 解析失败不是「小问题」：它会被兜底成 supplement，于是**每一个**动作都被打回，
/// 用户看到的是「审查员一直在找茬」，而真实原因是解析器读不懂它的输出。
/// 所以这里按「严格 → 宽容 → 关键词 → 文本协议 → 保守兜底」五级递进。
/// </summary>
public static class VerdictParser
{
    public static readonly string[] ValidVerdicts = { "approve", "supplement", "revise" };

    /// <summary>(中文词, 规范值)。按前缀匹配。</summary>
    private static readonly (string Word, string Value)[] ZhWords =
    {
        ("通过", "approve"), ("放行", "approve"), ("同意", "approve"), ("批准", "approve"),
        ("补充", "supplement"), ("需补充", "supplement"), ("存疑", "supplement"),
        ("变更", "revise"), ("需变更", "revise"), ("驳回", "revise"), ("拒绝", "revise"),
    };

    /// <summary>英文词只做**整行**精确匹配，防 "yaml" / "node" 被误命中。</summary>
    private static readonly Dictionary<string, string> EnWords = new(StringComparer.Ordinal)
    {
        ["approve"] = "approve",
        ["supplement"] = "supplement",
        ["revise"] = "revise",
        ["ok"] = "approve",
        ["pass"] = "approve",
        ["yes"] = "approve",
        ["reject"] = "revise",
        ["no"] = "revise",
    };

    /// <summary>归一化首行：去掉空白与各种标点，转小写。</summary>
    private static readonly Regex Noise = new(
        @"[\s:：,，.。;；!！?？""'`*#\-\[\]()（）]+", RegexOptions.Compiled);

    private static readonly Regex JsonVerdictKey = new(
        @"verdict""?\s*:\s*""?(approve|supplement|revise)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ==================================================================
    // 入口
    // ==================================================================

    /// <summary>
    /// 把审查员的原始输出解析成结构化裁决。
    ///
    /// 解析不出任何东西时返回 <c>supplement</c>（而不是 approve）——
    /// 看不懂就要求补充说明，是唯一安全的方向。
    /// </summary>
    public static Verdict Parse(string? text)
    {
        var raw = text ?? string.Empty;

        // ① 严格 / 宽容 JSON
        var blob = ExtractBalancedJson(raw);
        if (blob is not null && TryParseObject(blob) is { } obj)
            return FromJson(obj, raw);

        // ② JSON 关键字降级（形如 verdict: "supplement"）
        var m = JsonVerdictKey.Match(raw);
        if (m.Success)
        {
            return new Verdict
            {
                Value = m.Groups[1].Value.ToLowerInvariant(),
                Severity = "medium",
                Reason = "（审查员输出非严格 JSON，已降级解析）",
                Degraded = true,
                Raw = raw,
            };
        }

        // ③ 简短文本协议
        var byText = ParseText(raw);
        if (byText is not null)
            return byText;

        // ④ 保守兜底
        return new Verdict
        {
            Value = "supplement",
            Severity = "medium",
            Reason = "审查员返回内容无法解析，按保守策略要求补充说明。",
            Demands = new[] { "请补充说明本次动作的目的、影响范围与回滚方式。" },
            Degraded = true,
            Raw = raw,
        };
    }

    // ==================================================================
    // ③ 文本协议
    // ==================================================================

    /// <summary>
    /// 解析「简短文本协议」：首行裁决词，其后 理由/要求/备选/风险 行。
    ///
    /// 首行支持极简简写（省 token）：<c>y</c> = 通过、<c>n</c> = 变更。
    /// 解析不出返回 null，由 <see cref="Parse"/> 走保守兜底。
    /// </summary>
    public static Verdict? ParseText(string? raw)
    {
        var lines = (raw ?? string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        if (lines.Length == 0)
            return null;

        var verdict = string.Empty;
        var brief = string.Empty;

        // ★ 极简简写入口。判定用**整行相等**，绝不做子串匹配 ——
        //   否则 "yaml" 会被读成 y、"node" 会被读成 n。
        var head = CleanHead(lines[0]);
        if (head is "y" or "yes")
        {
            verdict = "approve";
            brief = "y";
        }
        else if (head is "n" or "no")
        {
            verdict = "revise";
            brief = "n";
        }

        if (verdict.Length == 0)
        {
            foreach (var ln in lines.Take(3))
            {
                verdict = MatchVerdictWord(ln);
                if (verdict.Length > 0)
                    break;
            }
        }

        if (verdict.Length == 0)
        {
            foreach (var ln in lines)
            {
                verdict = MatchVerdictWord(ln);
                if (verdict.Length > 0)
                    break;
            }
        }

        if (verdict.Length == 0)
            return null;

        var reason = string.Join(" ", GrabLabeled(lines, "理由"));
        if (reason.Length > 500)
            reason = reason[..500];

        var low = raw!.ToLowerInvariant();

        string severity;
        if (raw.Contains("高风险", StringComparison.Ordinal)
            || raw.Contains("严重", StringComparison.Ordinal)
            || low.Contains("high", StringComparison.Ordinal))
        {
            severity = "high";
        }
        else if (raw.Contains("低风险", StringComparison.Ordinal) || low.Contains("low", StringComparison.Ordinal))
        {
            severity = "low";
        }
        else
        {
            severity = verdict == "approve" ? "low" : "medium";
        }

        if (reason.Length == 0)
        {
            reason = brief.Length > 0
                ? $"（审查员极简回复「{brief}」，未附理由）"
                : "（审查员给出简短裁决，未附理由）";
        }

        return new Verdict
        {
            Value = verdict,
            Severity = severity,
            Reason = reason,
            Demands = GrabLabeled(lines, "要求"),
            Alternatives = GrabLabeled(lines, "备选"),
            Risks = GrabLabeled(lines, "风险"),
            Raw = raw,
        };
    }

    private static string CleanHead(string s)
        => Noise.Replace((s ?? string.Empty).Trim().ToLowerInvariant(), string.Empty);

    /// <summary>在一行里识别裁决词，返回规范值或空串。</summary>
    private static string MatchVerdictWord(string line)
    {
        var core = CleanHead(line);

        if (core.Length == 0)
            return string.Empty;

        if (EnWords.TryGetValue(core, out var en))
            return en;

        foreach (var (word, value) in ZhWords)
        {
            if (core.StartsWith(word, StringComparison.Ordinal))
                return value;
        }

        return string.Empty;
    }

    /// <summary>抽取以「label：」开头的内容（中英文冒号都认）。</summary>
    /// <remarks>
    /// 同一标签可能出现多行（模型偶尔会把「要求」拆成几条各自一行），
    /// 与 Python 版一致：**全部收集**，而不是只取第一条 ——
    /// 只取第一条会让「要求 2、要求 3」在回填时凭空消失。
    /// 相邻重复只留一份，因为模型也常把同一句重复两遍。
    /// </remarks>
    private static List<string> GrabLabeled(IEnumerable<string> lines, string label)
    {
        var outList = new List<string>();

        foreach (var ln in lines)
        {
            var hasCn = ln.StartsWith(label + "：", StringComparison.Ordinal);
            var hasEn = ln.StartsWith(label + ":", StringComparison.Ordinal);

            if (!hasCn && !hasEn)
                continue;

            var v = ln[(label.Length + 1)..].Trim();

            if (v.Length == 0)
                continue;

            if (outList.Count == 0 || outList[^1] != v)
                outList.Add(v);
        }

        return outList;
    }

    // ==================================================================
    // ① JSON
    // ==================================================================

    /// <summary>从文本里剥出第一个平衡的 <c>{...}</c>（跳过字符串字面量内的括号）。</summary>
    public static string? ExtractBalancedJson(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var start = text.IndexOf('{');
        if (start < 0)
            return null;

        var depth = 0;
        var inStr = false;
        var esc = false;

        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];

            if (inStr)
            {
                if (esc)
                    esc = false;
                else if (ch == '\\')
                    esc = true;
                else if (ch == '"')
                    inStr = false;

                continue;
            }

            switch (ch)
            {
                case '"':
                    inStr = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return text[start..(i + 1)];
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// 尽量宽容地把文本解析为 JSON。
    ///
    /// 模型常见毛病：在 JSON 字符串里写裸换行 / 制表符，导致标准解析失败。
    /// 先直接解析；失败则把**字符串字面量内**的裸控制字符转义后再试一次。
    /// </summary>
    private static JsonElement? TryParseObject(string blob)
    {
        if (TryParse(blob) is { } first)
            return first;

        return TryParse(EscapeControlCharsInStrings(blob));
    }

    private static JsonElement? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string EscapeControlCharsInStrings(string blob)
    {
        var sb = new StringBuilder(blob.Length + 32);
        var inStr = false;
        var esc = false;

        foreach (var ch in blob)
        {
            if (inStr)
            {
                if (esc)
                {
                    sb.Append(ch);
                    esc = false;
                    continue;
                }

                switch (ch)
                {
                    case '\\':
                        sb.Append(ch);
                        esc = true;
                        continue;
                    case '"':
                        sb.Append(ch);
                        inStr = false;
                        continue;
                    case '\n':
                        sb.Append("\\n");
                        continue;
                    case '\r':
                        sb.Append("\\r");
                        continue;
                    case '\t':
                        sb.Append("\\t");
                        continue;
                    default:
                        sb.Append(ch);
                        continue;
                }
            }

            sb.Append(ch);

            if (ch == '"')
                inStr = true;
        }

        return sb.ToString();
    }

    private static Verdict FromJson(JsonElement obj, string raw)
    {
        var verdict = (GetString(obj, "verdict") ?? string.Empty).Trim().ToLowerInvariant();

        if (!ValidVerdicts.Contains(verdict, StringComparer.Ordinal))
            verdict = "supplement";

        var severity = (GetString(obj, "severity") ?? string.Empty).Trim().ToLowerInvariant();

        return new Verdict
        {
            Value = verdict,
            Severity = severity.Length > 0 ? severity : "medium",
            Reason = (GetString(obj, "reason") ?? string.Empty).Trim(),
            Demands = AsList(obj, "demands"),
            Alternatives = AsList(obj, "alternatives"),
            Risks = AsList(obj, "risks"),
            Raw = raw,
        };
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>
    /// 把 demands / risks 之类的字段归一成字符串数组。
    ///
    /// ★ 必须接受「字符串」形态：审查员经常写 <c>"demands": "改用 ws_replace"</c>
    ///   而不是数组。如果只认数组，这一条要求就被静默丢掉了 ——
    ///   回填文本里只剩理由，执行者根本不知道要改什么。
    /// </summary>
    private static IReadOnlyList<string> AsList(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
            return Array.Empty<string>();

        switch (el.ValueKind)
        {
            case JsonValueKind.String:
            {
                var s = el.GetString();
                return string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : new[] { s };
            }

            case JsonValueKind.Array:
            {
                var list = new List<string>();

                foreach (var item in el.EnumerateArray())
                {
                    var s = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : item.GetRawText();

                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s);
                }

                return list;
            }

            case JsonValueKind.Null or JsonValueKind.Undefined:
                return Array.Empty<string>();

            default:
                return new[] { el.GetRawText() };
        }
    }

    /// <summary>把多行文本压成一行（日志用）。</summary>
    public static string SplitInline(string? text, int maxChars)
    {
        var s = (text ?? string.Empty)
            .Replace("\r\n", " | ", StringComparison.Ordinal)
            .Replace("\n", " | ", StringComparison.Ordinal)
            .Replace("\r", " | ", StringComparison.Ordinal);

        return s.Length <= maxChars ? s : s[..maxChars] + "…";
    }
}
