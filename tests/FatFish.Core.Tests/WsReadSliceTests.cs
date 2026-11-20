using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// ws_read 的按行切片（2026-11-05，P2）。
/// </summary>
/// <remarks>
/// ★ 为什么把切片抽成纯函数来测：分页的错法全都**不会报错**。
///   少返回一行、把 CRLF 的 \r 吃掉、末尾多出一个空行 ——
///   表现都是「模型看到内容、信心满满地去 ws_replace，然后匹配失败」，
///   而错误信息只会说「没找到要替换的原文」，完全指不到真正的原因。
/// ★ 最要紧的是 <see cref="CrlfFile_RoundTripsExactly"/>：
///   切片必须能**逐字还原**原文。改一个字节，精确替换就在 CRLF 文件上全线失灵。
/// </remarks>
public sealed class WsReadSliceTests
{
    private const int Big = 1_000_000;

    [Fact]
    public void WholeLfFile_RoundTripsExactly()
    {
        const string content = "a\nb\nc\n";

        var slice = WsReadTool.Slice(content, 1, Big, Big);

        Assert.Equal(content, slice.Text);
        Assert.Equal(3, slice.LineCount);
        Assert.False(slice.HasMore);
    }

    [Fact]
    public void CrlfFile_RoundTripsExactly()
    {
        const string content = "a\r\nb\r\nc\r\n";

        var slice = WsReadTool.Slice(content, 1, Big, Big);

        // \r 必须原样保留 —— 剥掉它，ws_replace 的精确匹配就会全盘失效
        Assert.Equal(content, slice.Text);
        Assert.Equal(3, slice.LineCount);
    }

    [Fact]
    public void NoTrailingNewline_RoundTripsExactly()
    {
        const string content = "a\nb";

        var slice = WsReadTool.Slice(content, 1, Big, Big);

        Assert.Equal(content, slice.Text);
        Assert.Equal(2, slice.LineCount);
    }

    [Fact]
    public void SecondPage_ReturnsRequestedLines()
    {
        const string content = "l1\nl2\nl3\nl4\n";

        var slice = WsReadTool.Slice(content, from: 2, limit: 2, maxChars: Big);

        Assert.Equal("l2\nl3", slice.Text);
        Assert.Equal(2, slice.From);
        Assert.Equal(3, slice.To);
        Assert.Equal(2, slice.ReturnedLines);
        Assert.True(slice.HasMore);
    }

    [Fact]
    public void LastPage_HasNoMore_AndKeepsTrailingNewline()
    {
        const string content = "l1\nl2\nl3\nl4\n";

        var slice = WsReadTool.Slice(content, from: 3, limit: 10, maxChars: Big);

        Assert.Equal("l3\nl4\n", slice.Text);   // 末尾换行要补回来
        Assert.Equal(4, slice.To);
        Assert.False(slice.HasMore);
    }

    [Fact]
    public void EmptyFile_HasNoLines()
    {
        var slice = WsReadTool.Slice(string.Empty, 1, Big, Big);

        Assert.Equal(0, slice.LineCount);
        Assert.Equal(string.Empty, slice.Text);
        Assert.False(slice.Any);
        Assert.False(slice.HasMore);
    }

    [Fact]
    public void OffsetBeyondEnd_ReturnsNothing()
    {
        var slice = WsReadTool.Slice("a\nb", from: 5, limit: 10, maxChars: Big);

        Assert.Equal(2, slice.LineCount);
        Assert.False(slice.Any);
        Assert.Equal(4, slice.To);          // From - 1
        Assert.Equal(string.Empty, slice.Text);
    }

    [Fact]
    public void CharBudget_DropsWholeLinesOnly()
    {
        // 三行各 100 字符；预算 250 → 只能装下两行（201 字符）
        var line = new string('x', 100);
        var content = $"{line}\n{line}\n{line}\n";

        var slice = WsReadTool.Slice(content, 1, Big, 250);

        Assert.Equal(2, slice.ReturnedLines);
        Assert.True(slice.HasMore);

        // 不能把半行返回：两行之间只有一个 \n，且总长正好 201
        Assert.Equal(201, slice.Text.Length);
        Assert.Equal(2, slice.Text.Split('\n').Length);
    }

    [Fact]
    public void SingleOversizedLine_IsStillReturned()
    {
        // 压成一行的 JSON：第一行必须放行，否则永远读不到内容
        var content = new string('y', 300);

        var slice = WsReadTool.Slice(content, 1, Big, maxChars: 10);

        Assert.True(slice.Any);
        Assert.Equal(300, slice.Text.Length);
        Assert.False(slice.HasMore);
    }

    [Fact]
    public void ZeroLimit_MeansUnlimited()
    {
        // limit = 0 表示「不限行数」，预算只由字符数决定
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 300; i++)
            sb.Append("line").Append(i).Append('\n');

        var slice = WsReadTool.Slice(sb.ToString(), 1, limit: 0, maxChars: Big);

        Assert.Equal(300, slice.ReturnedLines);
        Assert.False(slice.HasMore);
    }

    [Fact]
    public void CharBudget_StillBinds_WhenLimitIsUnlimited()
    {
        // 不限行数时，字符预算仍然要咬住，且只切整行
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 100; i++)
            sb.Append(new string('z', 100)).Append('\n');

        var slice = WsReadTool.Slice(sb.ToString(), 1, limit: 0, maxChars: 500);

        Assert.True(slice.HasMore);
        Assert.Equal(4, slice.ReturnedLines);   // 4 行 = 403 字符；第 5 行会到 504
    }

    [Fact]
    public void CharBudgetCut_MarksBudgetCut()
    {
        // 三行各 100 字符，预算 250 → 只装得下两行，是**预算**把它切了
        var line = new string('x', 100);

        var slice = WsReadTool.Slice($"{line}\n{line}\n{line}\n", 1, Big, 250);

        Assert.True(slice.BudgetCut);
        Assert.True(slice.HasMore);
    }

    [Fact]
    public void LimitCut_DoesNotMarkBudgetCut()
    {
        // 调用方自己要 2 行（limit），文件还有剩余 —— 这不是「内容被切」，
        // 是「你就要了这么多」。truncated 若为此报 true，模型会以为内容丢了（2026-11-06 修）。
        var slice = WsReadTool.Slice("l1\nl2\nl3\nl4\n", 1, limit: 2, maxChars: Big);

        Assert.False(slice.BudgetCut);
        Assert.True(slice.HasMore);
    }

    [Fact]
    public void WholeFile_DoesNotMarkBudgetCut()
    {
        var slice = WsReadTool.Slice("a\nb\n", 1, Big, Big);

        Assert.False(slice.BudgetCut);
        Assert.False(slice.HasMore);
    }

    [Fact]
    public void SingleOversizedLine_DoesNotMarkBudgetCut()
    {
        // 第一行总是放行；只有这一行，拿完就到底了，不算被预算切
        var slice = WsReadTool.Slice(new string('y', 300), 1, Big, maxChars: 10);

        Assert.False(slice.BudgetCut);
        Assert.False(slice.HasMore);
    }

    [Fact]
    public void CountLines_MatchesEditorConvention()
    {
        Assert.Equal(0, WsReadTool.CountLines(string.Empty));
        Assert.Equal(1, WsReadTool.CountLines("a"));
        Assert.Equal(1, WsReadTool.CountLines("a\n"));
        Assert.Equal(2, WsReadTool.CountLines("a\nb"));
        Assert.Equal(2, WsReadTool.CountLines("a\nb\n"));
    }
}
