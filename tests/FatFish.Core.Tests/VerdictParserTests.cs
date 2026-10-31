using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 裁决解析测试。
///
/// 这一组守的是一条容易被忽略、但后果很重的链路：
/// <b>解析失败 → 兜底成 supplement → 每一个动作都被打回</b>。
/// 用户看到的现象是「审查员总在找茬」，真实原因却是解析器读不懂它的输出 ——
/// 两种完全不同的故障，表现得一模一样。所以每种输出形态都必须有断言。
/// </summary>
public class VerdictParserTests
{
    // ==================================================================
    // ① JSON
    // ==================================================================

    [Fact]
    public void Json_Strict_IsParsed()
    {
        var v = VerdictParser.Parse(
            """{"verdict":"revise","severity":"high","reason":"越界","demands":["换路径"],"risks":["改到系统目录"]}""");

        Assert.Equal("revise", v.Value);
        Assert.Equal("high", v.Severity);
        Assert.Equal("越界", v.Reason);
        Assert.Equal("换路径", Assert.Single(v.Demands));
        Assert.Equal("改到系统目录", Assert.Single(v.Risks));
        Assert.False(v.Degraded);
        Assert.False(v.Approved);
    }

    [Fact]
    public void Json_InCodeFence_IsParsed()
    {
        var v = VerdictParser.Parse(
            "```json\n{\"verdict\":\"approve\",\"reason\":\"ok\"}\n```");

        Assert.Equal("approve", v.Value);
        Assert.True(v.Approved);
    }

    [Fact]
    public void Json_WithSurroundingProse_IsParsed()
    {
        var v = VerdictParser.Parse(
            "好的，我的结论是 {\"verdict\": \"supplement\", \"reason\": \"缺少回滚说明\"} 完毕。");

        Assert.Equal("supplement", v.Value);
        Assert.Equal("缺少回滚说明", v.Reason);
    }

    /// <summary>
    /// JSON 字符串里写**裸换行** —— 模型最常见的毛病，标准解析器必失败。
    /// 宽容解析必须能救回来；救不回来就会变成「每个动作都被打回」。
    /// </summary>
    [Fact]
    public void Json_WithRawNewlineInsideString_IsParsed()
    {
        var raw = "{\"verdict\":\"revise\",\"reason\":\"第一行\n第二行\"}";

        var v = VerdictParser.Parse(raw);

        Assert.Equal("revise", v.Value);
        Assert.Contains("第一行", v.Reason);
        Assert.Contains("第二行", v.Reason);
    }

    /// <summary>
    /// demands 写成字符串而不是数组。
    /// 只认数组的话，这一条要求会被静默丢掉 —— 回填文本里只剩理由，
    /// 执行者根本不知道要改什么。
    /// </summary>
    [Fact]
    public void Json_DemandsAsString_IsKept()
    {
        var v = VerdictParser.Parse(
            """{"verdict":"supplement","demands":"说明回滚方式"}""");

        Assert.Equal("说明回滚方式", Assert.Single(v.Demands));
    }

    /// <summary>括号出现在字符串里时，平衡扫描不能被它带偏。</summary>
    [Fact]
    public void Json_BracesInsideString_DoNotBreakBalance()
    {
        var v = VerdictParser.Parse(
            """{"verdict":"approve","reason":"这里有 } 一个大括号"}""");

        Assert.Equal("approve", v.Value);
        Assert.Contains("大括号", v.Reason);
    }

    // ==================================================================
    // ② JSON 关键字降级
    // ==================================================================

    [Fact]
    public void JsonKeywordFallback_Works()
    {
        var v = VerdictParser.Parse("verdict: \"supplement\" 请补充说明");

        Assert.Equal("supplement", v.Value);
        Assert.True(v.Degraded);
    }

    // ==================================================================
    // ③ 简短文本协议
    // ==================================================================

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData("yes")]
    [InlineData("通过")]
    [InlineData("通过。")]
    [InlineData("放行")]
    [InlineData("同意")]
    public void TextProtocol_ApproveForms(string raw)
    {
        Assert.Equal("approve", VerdictParser.Parse(raw).Value);
    }

    [Theory]
    [InlineData("n")]
    [InlineData("N")]
    [InlineData("no")]
    [InlineData("变更")]
    [InlineData("驳回")]
    [InlineData("拒绝")]
    public void TextProtocol_ReviseForms(string raw)
    {
        Assert.Equal("revise", VerdictParser.Parse(raw).Value);
    }

    [Fact]
    public void TextProtocol_SupplementWithLabels()
    {
        var v = VerdictParser.Parse(
            "补充\n理由：看不出会不会覆盖原文件\n要求：说明是否会备份");

        Assert.Equal("supplement", v.Value);
        Assert.Equal("看不出会不会覆盖原文件", v.Reason);
        Assert.Equal("说明是否会备份", Assert.Single(v.Demands));
    }

    [Fact]
    public void TextProtocol_ReviseWithAllLabels()
    {
        var v = VerdictParser.Parse(
            "变更\n" +
            "理由：目标是驱动器根目录，改动面过大。\n" +
            "要求：改用具体的数据子目录。\n" +
            "风险：可能误改系统文件。\n" +
            "备选：先只读探查再动手。");

        Assert.Equal("revise", v.Value);
        Assert.Equal("目标是驱动器根目录，改动面过大。", v.Reason);
        Assert.Equal("改用具体的数据子目录。", Assert.Single(v.Demands));
        Assert.Equal("可能误改系统文件。", Assert.Single(v.Risks));
        Assert.Equal("先只读探查再动手。", Assert.Single(v.Alternatives));
    }

    /// <summary>
    /// 「要求」出现多行时**全部收集**。
    /// 只取第一条会让「要求 2」凭空消失，而执行者只会照第一条改，
    /// 下一轮又因为第二条被打回 —— 看起来像审查员在无限加码。
    /// </summary>
    [Fact]
    public void TextProtocol_MultipleDemands_AreAllKept()
    {
        var v = VerdictParser.Parse(
            "变更\n要求：先备份原文件\n要求：改成增量替换");

        Assert.Equal(2, v.Demands.Count);
        Assert.Contains("先备份原文件", v.Demands);
        Assert.Contains("改成增量替换", v.Demands);
    }

    /// <summary>
    /// ★ 极简简写用**整行相等**判定。
    /// 若改成子串匹配，"yaml" 会被读成 y（放行），"node" 会被读成 n（变更）——
    /// 一个词的误判就足以让一批危险动作直接过闸。
    /// </summary>
    [Theory]
    [InlineData("yaml")]
    [InlineData("node")]
    [InlineData("yes we can")]
    public void TextProtocol_DoesNotSubstringMatchBriefWords(string raw)
    {
        var v = VerdictParser.Parse(raw);

        // 解析不出任何裁决 → 保守 supplement，绝不是 approve
        Assert.Equal("supplement", v.Value);
        Assert.True(v.Degraded);
    }

    // ==================================================================
    // ④ 兜底
    // ==================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("胡说八道没有结构")]
    [InlineData("我觉得还行吧")]
    public void Unparseable_IsSupplementNotApprove(string raw)
    {
        var v = VerdictParser.Parse(raw);

        Assert.Equal("supplement", v.Value);
        Assert.False(v.Approved);
    }

    [Fact]
    public void Unparseable_ProvidesFallbackDemand()
    {
        var v = VerdictParser.Parse("完全无法解析");

        Assert.NotEmpty(v.Demands);
        Assert.Contains("回滚", string.Join("", v.Demands));
    }

    [Fact]
    public void Null_IsSupplement()
    {
        Assert.Equal("supplement", VerdictParser.Parse(null).Value);
    }

    // ==================================================================
    // severity
    // ==================================================================

    [Fact]
    public void Severity_HighKeyword()
    {
        Assert.Equal("high", VerdictParser.Parse("变更\n理由：这是高风险操作").Severity);
    }

    [Fact]
    public void Severity_ApproveDefaultsToLow()
    {
        Assert.Equal("low", VerdictParser.Parse("y").Severity);
    }

    [Fact]
    public void Severity_NonApproveDefaultsToMedium()
    {
        Assert.Equal("medium", VerdictParser.Parse("变更\n理由：路径不对").Severity);
    }

    // ==================================================================
    // 小工具
    // ==================================================================

    [Theory]
    [InlineData("前缀 {\"a\":1} 后缀", "{\"a\":1}")]
    [InlineData("没有括号", null)]
    [InlineData("{\"a\":{\"b\":2}}", "{\"a\":{\"b\":2}}")]
    public void ExtractBalancedJson_Works(string input, string? expected)
    {
        Assert.Equal(expected, VerdictParser.ExtractBalancedJson(input));
    }

    [Fact]
    public void SplitInline_CollapsesNewlines()
    {
        Assert.Contains(" | ", VerdictParser.SplitInline("a\nb", 100));
    }

    [Fact]
    public void Label_IsHumanReadable()
    {
        Assert.Equal("需变更方案", new Verdict { Value = "revise" }.Label);
        Assert.Equal("通过", new Verdict { Value = "approve" }.Label);
    }
}
