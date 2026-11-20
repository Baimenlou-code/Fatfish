using FatFish.Core.Agent;
using FatFish.Core.Llm;
using System.Linq;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 历史裁剪的下刀点。
/// </summary>
/// <remarks>
/// ★ 这批测试的来历：2026-11-05 发现旧 <c>TrimHistory</c> 找的是「第一条 user」，
///   而 <c>_history[1]</c> 恒为第一条 user —— 于是 <c>removed</c> 永远是 0，
///   六个调用点每轮都在调、每一次都什么也没做，历史只增不减。
///   这个错误**不会抛异常、不会报错**，只会让上下文和账单慢慢膨胀，
///   所以只能靠测试钉住。
/// ★ 最要紧的一条断言是「下刀点绝不能落在 tool 消息上」：
///   assistant(tool_calls) 与随后的 tool 结果必须成组出现，
///   切开会让服务端以 "tool must follow tool_calls" 拒绝整个请求 ——
///   那比「没裁成」严重得多。
/// ★ 预算一律由实际 <c>EstimateTokens()</c> 反推，不手写数字：
///   手算 token 会把「估算公式改了」误报成「裁剪逻辑坏了」。
/// </remarks>
public sealed class TrimHistoryTests
{
    /// <summary>造一条 200 字符的消息（角色可选：system / user / tool）。</summary>
    private static ChatMessage Big(string role, string callId = "call_1")
        => role switch
        {
            "system" => ChatMessage.System(new string('s', 200)),
            "tool" => ChatMessage.ToolResult(callId, new string('t', 200)),
            _ => ChatMessage.User(new string('u', 200)),
        };

    /// <summary>让「保留从 index 开始往后」刚好装得下、再多一条就超的预算。</summary>
    private static int BudgetKeepingFrom(IReadOnlyList<ChatMessage> history, int index)
        => history.Skip(index).Sum(m => m.EstimateTokens());

    [Fact]
    public void OnlySystem_DoesNotTrim()
    {
        var history = new List<ChatMessage> { Big("system") };

        Assert.False(AgentLoop.PlanTrim(history, 1).Any);
    }

    [Fact]
    public void ZeroBudget_DisablesTrimming()
    {
        var history = new List<ChatMessage>
        {
            Big("system"), Big("user"), Big("user"), Big("user"),
        };

        Assert.False(AgentLoop.PlanTrim(history, 0).Any);
    }

    [Fact]
    public void UnderBudget_DoesNotTrim()
    {
        var history = new List<ChatMessage>
        {
            Big("system"), Big("user"), Big("user"),
        };

        var budget = history.Sum(m => m.EstimateTokens()) + 1;

        Assert.False(AgentLoop.PlanTrim(history, budget).Any);
    }

    [Fact]
    public void OverBudget_CutsAtUserBoundary_AndDropsTheOldest()
    {
        // [system, user, tool, user, tool]
        var history = new List<ChatMessage>
        {
            Big("system"), Big("user"), Big("tool"), Big("user"), Big("tool"),
        };

        // 预算刚好只装得下 index 3、4 —— 再多保留 index 2 就会超
        var plan = AgentLoop.PlanTrim(history, BudgetKeepingFrom(history, 3));

        Assert.True(plan.Any);
        Assert.Equal(3, plan.Cut);
        Assert.Equal(2, plan.DroppedCount);
        Assert.Equal("user", history[plan.Cut].Role);
        Assert.True(plan.DroppedTokens > 0);
    }

    [Fact]
    public void CutNeverLandsOnToolMessage()
    {
        // [system, user, tool, tool, user] —— 预算算出来的起点正好落在 tool 上
        var history = new List<ChatMessage>
        {
            Big("system"), Big("user"), Big("tool"), Big("tool"), Big("user"),
        };

        var plan = AgentLoop.PlanTrim(history, BudgetKeepingFrom(history, 3));

        Assert.True(plan.Any);
        Assert.Equal(4, plan.Cut);

        // 关键断言：保留窗口的第一条绝不能是 tool，否则 tool_calls 被切断
        Assert.NotEqual("tool", history[plan.Cut].Role);
    }

    [Fact]
    public void AfterTrimming_FirstKeptMessageIsNeverTool()
    {
        // 回归断言：无论预算取多少，保留窗口的首条都不是 tool
        var history = new List<ChatMessage>
        {
            Big("system"), Big("user"), Big("tool"), Big("user"),
            Big("tool"), Big("tool"), Big("user"), Big("tool"),
        };

        var total = history.Sum(m => m.EstimateTokens());

        for (var budget = 1; budget <= total; budget += Math.Max(1, total / 40))
        {
            var plan = AgentLoop.PlanTrim(history, budget);

            if (!plan.Any)
                continue;

            Assert.InRange(plan.Cut, 2, history.Count - 1);
            Assert.NotEqual("tool", history[plan.Cut].Role);
        }
    }
}
