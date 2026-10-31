using System.Text.Json;
using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 送审材料拼装测试。
///
/// 这一组守的是「审查员能看到什么」。它看不到的东西，等于不存在：
///   · 漏了动作清单 → 它在评一个自己没看见的动作；
///   · 漏了用户原声 → 用户亲手批准的动作会被判成越权；
///   · 漏了环境事实 → 它会把 Get-ChildItem 和 grep 判得一样糟；
///   · 预算切错位置 → 最关键的几段整段消失，而不是变短。
/// </summary>
public class VerifierPromptsTests
{
    private static VerifyOptions Options(int promptMax = 64_000) => new()
    {
        Endpoint = new VerifierEndpoint { ApiKey = "test" },
        PromptMaxChars = promptMax,
    };

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    // ==================================================================
    // 预算分配
    // ==================================================================

    [Fact]
    public void JoinTrim_UnderLimit_KeepsEverything()
    {
        var sections = new (int, string)[] { (9, "动作清单"), (6, "用户需求") };

        var text = VerifierPrompts.JoinTrim(sections, 1000);

        Assert.Contains("动作清单", text);
        Assert.Contains("用户需求", text);
        Assert.DoesNotContain("压缩", text);
    }

    [Fact]
    public void JoinTrim_OverLimit_RespectsLimit()
    {
        var sections = new (int, string)[]
        {
            (9, new string('A', 5000)),
            (6, new string('B', 5000)),
            (2, new string('C', 5000)),
        };

        var text = VerifierPrompts.JoinTrim(sections, 800);

        Assert.True(text.Length <= 800, $"实际长度 {text.Length} 超过上限 800");
    }

    /// <summary>
    /// ★ 超限时每段都必须留下种子，而不是把末尾几段整段丢掉。
    ///
    /// 丢掉末尾的后果很具体：末尾恰好是「用户真实操作记录」，
    /// 丢掉它，审查员就会把用户亲手批准的动作判成越权 —— 而且它不知道自己在瞎猜。
    /// </summary>
    [Fact]
    public void JoinTrim_OverLimit_EverySectionKeepsASeed()
    {
        var sections = new (int, string)[]
        {
            (9, "动作清单" + new string('A', 5000)),
            (8, "用户原声" + new string('B', 5000)),
            (2, "近期上下文" + new string('C', 5000)),
        };

        var text = VerifierPrompts.JoinTrim(sections, 1200);

        Assert.Contains("动作清单", text);
        Assert.Contains("用户原声", text);
        Assert.Contains("近期上下文", text);
        Assert.Contains("因总长限制已压缩", text);
    }

    [Fact]
    public void JoinTrim_HighPriorityKeepsMore()
    {
        var sections = new (int, string)[]
        {
            (9, new string('H', 8000)),
            (1, new string('L', 8000)),
        };

        var text = VerifierPrompts.JoinTrim(sections, 2000);

        var h = text.Count(c => c == 'H');
        var l = text.Count(c => c == 'L');

        Assert.True(h > l, $"高优先级应保留更多：H={h}, L={l}");
    }

    [Fact]
    public void JoinTrim_EmptySections_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, VerifierPrompts.JoinTrim(Array.Empty<(int, string)>(), 100));
        Assert.Equal(string.Empty, VerifierPrompts.JoinTrim(new[] { (9, "") }, 100));
    }

    // ==================================================================
    // 截断
    // ==================================================================

    /// <summary>
    /// 按字符截断不能切开代理对（emoji 占两个 UTF-16 码元）。
    ///
    /// 切开后会留下一个孤立的高代理：屏幕上是个豆腐块，
    /// 更糟的是送进 JSON 序列化会直接抛 ArgumentException ——
    /// 而这种崩溃只在「内容里恰好有 emoji 且恰好超限」时发生，极难复现。
    /// </summary>
    [Fact]
    public void SafeTruncate_DoesNotSplitSurrogatePair()
    {
        const string fish = "🐟🐟🐟";   // 6 个 char

        var cut = VerifierPrompts.SafeTruncate(fish, 3);

        Assert.Equal(2, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
        Assert.Equal("🐟", cut);
    }

    [Fact]
    public void SafeTruncate_WithinLimit_ReturnsOriginal()
    {
        Assert.Equal("abc", VerifierPrompts.SafeTruncate("abc", 10));
    }

    [Fact]
    public void SafeTruncate_ZeroOrNegative_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, VerifierPrompts.SafeTruncate("abc", 0));
        Assert.Equal(string.Empty, VerifierPrompts.SafeTruncate("abc", -5));
    }

    [Fact]
    public void Clip_AddsTruncationMarkerWithOriginalLength()
    {
        var text = VerifierPrompts.Clip(new string('x', 100), 10);

        Assert.Contains("已截断", text);
        Assert.Contains("100", text);
    }

    [Fact]
    public void Clip_NormalizesCrlf()
    {
        Assert.DoesNotContain("\r", VerifierPrompts.Clip("a\r\nb", 100));
    }

    // ==================================================================
    // 动作渲染
    // ==================================================================

    [Fact]
    public void DescribeAction_Write_ShowsPathAndContent()
    {
        var text = VerifierPrompts.DescribeAction(
            "ws_write", Args(new { path = "a.txt", content = "内容" }), Options());

        Assert.Contains("write", text);
        Assert.Contains("a.txt", text);
        Assert.Contains("内容", text);
    }

    /// <summary>
    /// ws_replace 必须同时给出 old 与 new。
    /// 只看 new 无法判断「会不会把别处改坏」—— 而那是这个工具唯一的风险点。
    /// </summary>
    [Fact]
    public void DescribeAction_Replace_ShowsBothOldAndNew()
    {
        var text = VerifierPrompts.DescribeAction(
            "ws_replace", Args(new { path = "a.cs", old = "旧代码", @new = "新代码" }), Options());

        Assert.Contains("旧代码", text);
        Assert.Contains("新代码", text);
    }

    [Fact]
    public void DescribeAction_RunCmd_ShowsCwdAndTimeout()
    {
        var text = VerifierPrompts.DescribeAction(
            "ws_run_cmd", Args(new { command = "dir", cwd = "sub", timeout = 60 }), Options());

        Assert.Contains("dir", text);
        Assert.Contains("cwd=sub", text);
        Assert.Contains("timeout=60", text);
    }

    [Fact]
    public void DescribeAction_BackgroundCmd_IsRenderedAsSideEffect()
    {
        var text = VerifierPrompts.DescribeAction(
            "ws_bg_cmd", Args(new { command = "python train.py", desc = "训练" }), Options());

        Assert.Contains("bg cmd", text);
        Assert.Contains("train.py", text);
    }

    [Fact]
    public void DescribeAction_Unknown_FallsBackToJson()
    {
        var text = VerifierPrompts.DescribeAction(
            "ws_future", Args(new { foo = "bar" }), Options());

        Assert.Contains("ws_future", text);
        Assert.Contains("bar", text);
    }

    [Fact]
    public void DescribeActionOneLine_IsCompact()
    {
        var line = VerifierPrompts.DescribeActionOneLine(
            "ws_run_cmd", Args(new { command = new string('x', 300) }));

        Assert.Contains("ws_run_cmd", line);
        Assert.True(line.Length < 200);
    }

    // ==================================================================
    // 送审材料整体
    // ==================================================================

    private static ReviewRequest SampleRequest() => new()
    {
        Actions = new[] { ("ws_write", Args(new { path = "notes.md", content = "hello" })) },
        UserGoal = "帮我写个说明文件",
        AiPlan = "我打算新建 notes.md",
        ContextText = "[user] 帮我写个说明文件",
        UserEvents = "[21:00:00] 用户输入：帮我写个说明文件",
        ExtraNote = @"当前工作台根：E:\work" + "\n本轮第 1 次送审",
    };

    [Fact]
    public void BuildActionPrompt_ContainsAllKeySections()
    {
        var text = VerifierPrompts.BuildActionPrompt(SampleRequest(), Options());

        Assert.Contains("用户的原始需求", text);
        Assert.Contains("执行者本轮的自述", text);
        Assert.Contains("待核验的动作清单", text);
        Assert.Contains("运行环境事实", text);
        Assert.Contains("机器预检结果", text);
        Assert.Contains("送审上下文", text);
        Assert.Contains("用户真实操作记录", text);
        Assert.Contains("请依据你的职责独立审查", text);
    }

    [Fact]
    public void BuildActionPrompt_IncludesActionBody()
    {
        var text = VerifierPrompts.BuildActionPrompt(SampleRequest(), Options());

        Assert.Contains("notes.md", text);
        Assert.Contains("hello", text);
    }

    [Fact]
    public void BuildActionPrompt_OmitsEmptyOptionalSections()
    {
        var req = SampleRequest() with { UserEvents = "", ExtraNote = "", ContextText = "" };

        var text = VerifierPrompts.BuildActionPrompt(req, Options());

        Assert.DoesNotContain("用户真实操作记录", text);
        Assert.DoesNotContain("送审上下文", text);
        Assert.DoesNotContain("近期上下文", text);
    }

    [Fact]
    public void BuildActionPrompt_RespectsBudget()
    {
        var req = SampleRequest() with
        {
            AiPlan = new string('P', 20_000),
            ContextText = new string('C', 20_000),
        };

        var text = VerifierPrompts.BuildActionPrompt(req, Options(promptMax: 3000));

        Assert.True(text.Length <= 3000, $"实际 {text.Length}");
        // 收尾指令永远不能被裁掉 —— 它是唯一告诉审查员「要干什么」的一句
        Assert.Contains("请依据你的职责独立审查", text);
    }

    [Fact]
    public void BuildAnswerPrompt_ContainsReplyAndAsk()
    {
        var text = VerifierPrompts.BuildAnswerPrompt("这是最终答复", "用户问题", "", Options());

        Assert.Contains("这是最终答复", text);
        Assert.Contains("即将交付的最终答复", text);
        Assert.Contains("请独立复核该答复", text);
    }

    /// <summary>
    /// 人格里必须留着三句支柱。它们看着像普通描述，实际上是行为的全部依据：
    /// 删掉「默认放行」→ 每次调用都被打回；删掉「不要反复加码」→ 死循环。
    /// </summary>
    [Fact]
    public void SystemPrompt_KeepsCoreRules()
    {
        Assert.Contains("默认放行，只拦真问题", VerifierPrompts.VerifierSystem);
        Assert.Contains("不要反复加码", VerifierPrompts.VerifierSystem);
        Assert.Contains("用户真实操作记录", VerifierPrompts.VerifierSystem);
        Assert.Contains("命令方言必查", VerifierPrompts.VerifierSystem);
        Assert.Contains("非模型转述、不可伪造", VerifierPrompts.VerifierSystem);
    }
}
