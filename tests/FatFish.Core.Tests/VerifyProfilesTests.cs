using System.Text.Json;
using FatFish.Core.Llm;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 审查员的业务分域（2026-11-05）。
/// </summary>
/// <remarks>
/// ★ 分域做两件事：**决定送哪些材料**、**给哪一套判据**。
///   前者省 token（近期上下文动辄几千字，对「这条命令危不危险」毫无帮助），
///   后者压幻觉（材料越杂，审查员越容易把不相干的东西串成一个故事）。
/// ★ 这批测试钉的是「哪一域要什么」这张表本身 ——
///   表写错了不会报错，只会让审查员在不该看见的材料里自由发挥。
/// </remarks>
public sealed class VerifyProfilesTests : IDisposable
{
    private readonly string _root;
    private readonly ToolContext _ctx;

    public VerifyProfilesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_vp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        var guard = new PathGuard(_root);

        _ctx = new ToolContext
        {
            Guard = guard,
            Tickets = new ReadTicketStore(guard),
            Approval = new ApprovalPolicy(guard),
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static (string Name, JsonElement Args) Act(string name, object? args = null)
        => (name, JsonSerializer.SerializeToElement(args ?? new { }));

    private static ToolCall Call(string name, string arguments)
        => new() { Id = "c1", Name = name, Arguments = arguments };

    // ---------------- 归域 ----------------

    [Fact]
    public void Classify_AllFileWrites_IsFileWrite()
    {
        // 注意：ws_read 是只读工具，进不了核验（ShouldVerify 只看有副作用的），
        // 所以它不参与归域。这里只列真正会被送审的文件类动作。
        var domain = VerifyProfiles.Classify(new[] { Act("ws_write"), Act("ws_replace") });

        Assert.Equal(VerifyDomain.FileWrite, domain);
    }

    [Fact]
    public void Classify_MixedBatches_IsMixed()
    {
        var domain = VerifyProfiles.Classify(new[] { Act("ws_write"), Act("ws_run_cmd") });

        Assert.Equal(VerifyDomain.Mixed, domain);
    }

    [Fact]
    public void Classify_Empty_IsMixed()
    {
        Assert.Equal(VerifyDomain.Mixed, VerifyProfiles.Classify(Array.Empty<(string, JsonElement)>()));
    }

    [Theory]
    [InlineData("ws_run_cmd", VerifyDomain.Exec)]
    [InlineData("ws_bg_python", VerifyDomain.Exec)]
    [InlineData("ws_delete", VerifyDomain.FileWrite)]
    [InlineData("ws_mkdir", VerifyDomain.WorkspaceShape)]
    [InlineData("web_fetch", VerifyDomain.Net)]
    [InlineData("subagent_run", VerifyDomain.Subagent)]
    [InlineData("some_plugin_tool", VerifyDomain.Plugin)]
    public void Of_MapsToolNames(string tool, VerifyDomain expected)
    {
        Assert.Equal(expected, VerifyProfiles.Of(tool));
    }

    // ---------------- 材料裁剪 ----------------

    [Theory]
    [InlineData(VerifyDomain.Exec, 4)]
    [InlineData(VerifyDomain.FileWrite, 4)]
    [InlineData(VerifyDomain.Plugin, 4)]
    [InlineData(VerifyDomain.Net, 2)]
    [InlineData(VerifyDomain.WorkspaceShape, 0)]
    [InlineData(VerifyDomain.Subagent, 16)]
    [InlineData(VerifyDomain.Mixed, 16)]
    public void ContextMessages_PerDomainIsDeliberate(VerifyDomain domain, int expected)
    {
        // 这张表就是「给几条上下文」的全部政策。改它等于改审查员看见的世界。
        Assert.Equal(expected, VerifyProfiles.For(domain).ContextMessages);
    }

    [Fact]
    public void WorkspaceShape_NeedsNoRecentContext()
    {
        // 建目录 / 换根 / 重载插件：看用户原声就够
        Assert.False(VerifyProfiles.For(VerifyDomain.WorkspaceShape).WantContext);
    }

    [Fact]
    public void ExecAndFileWrite_DoWantSomeContext()
    {
        // 多轮任务里「用户到底要干什么」常常不在最后一句里，这两类必须给一点上下文。
        // （第一版把它们的上下文砍光了，是被用户纠正回来的。）
        Assert.True(VerifyProfiles.For(VerifyDomain.Exec).WantContext);
        Assert.True(VerifyProfiles.For(VerifyDomain.FileWrite).WantContext);
    }

    [Fact]
    public void LimitContext_KeepsOnlyNewestMessages()
    {
        string[] context = ["[user] a", "[assistant] b", "[user] c", "[assistant] d"];

        // Net 只要 2 条 → 取最后两条**消息**
        var cut = VerifyProfiles.LimitContext(context, VerifyProfiles.For(VerifyDomain.Net));

        Assert.Equal("[user] c\n[assistant] d", cut);
    }

    [Fact]
    public void LimitContext_CountsMessages_NotLines()
    {
        // 回归（2026-11-06）：一条多行回答只是**一条**消息。
        // 旧实现按 '\n' 数「最后 N 行」，配 2 条时会把这段回答切成两半，
        // 用户的原始要求整个丢掉 —— 那正是这个功能存在的理由。
        string[] context =
        [
            "[user] 把那个配置改一下",
            "[assistant] 我先看目录\n然后读配置\n再改第 3 行\n最后编译",
        ];

        var cut = VerifyProfiles.LimitContext(context, VerifyProfiles.For(VerifyDomain.Net));

        Assert.Equal(
            "[user] 把那个配置改一下\n[assistant] 我先看目录\n然后读配置\n再改第 3 行\n最后编译",
            cut);
    }

    [Fact]
    public void LimitContext_Zero_ReturnsEmpty()
    {
        string[] context = ["[user] a", "[assistant] b"];

        Assert.Equal(
            string.Empty,
            VerifyProfiles.LimitContext(context, VerifyProfiles.For(VerifyDomain.WorkspaceShape)));
    }

    [Fact]
    public void LimitContext_ShortEnough_PassesThrough()
    {
        string[] context = ["[user] 只改这一行"];

        Assert.Equal(
            context[0],
            VerifyProfiles.LimitContext(context, VerifyProfiles.For(VerifyDomain.Exec)));
    }

    [Fact]
    public void Net_DoesNotWantUserEvents()
    {
        // 联网检索基本不涉及「用户是否亲自授权」
        Assert.False(VerifyProfiles.For(VerifyDomain.Net).WantUserEvents);
    }

    [Fact]
    public void EveryDomain_HasShortInstruction()
    {
        foreach (var domain in Enum.GetValues<VerifyDomain>())
        {
            var profile = VerifyProfiles.For(domain);

            Assert.False(string.IsNullOrWhiteSpace(profile.Instruction), $"{domain} 缺少判据");
            Assert.False(string.IsNullOrWhiteSpace(profile.Name), $"{domain} 缺少短名");

            // 送审上下文那一节只有 800 字符预算，判据必须短
            Assert.True(profile.Instruction.Length < 400, $"{domain} 的判据过长：{profile.Instruction.Length}");
        }
    }

    // ---------------- 读凭证覆盖率（P6'） ----------------

    [Fact]
    public void CoverageNote_PartialRead_ReportsNumbers()
    {
        var full = Path.Combine(_root, "a.txt");
        File.WriteAllText(full, "x");

        _ctx.Tickets.Issue(full, totalLines: 100, fromLine: 1, toLine: 40);

        var note = VerifyProfiles.CoverageNote(
            new[] { Call("ws_replace", "{\"path\":\"a.txt\"}") }, _ctx);

        Assert.Contains("ws_replace", note);
        Assert.Contains("40/100", note);
    }

    [Fact]
    public void CoverageNote_NoTicket_SaysNeverRead()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");

        var note = VerifyProfiles.CoverageNote(
            new[] { Call("ws_write", "{\"path\":\"a.txt\"}") }, _ctx);

        Assert.Contains("未读过", note);
    }

    [Fact]
    public void CoverageNote_ExecTools_ProducesNothing()
    {
        // 覆盖率只对文件类动作有意义
        var note = VerifyProfiles.CoverageNote(
            new[] { Call("ws_run_cmd", "{\"command\":\"echo hi\"}") }, _ctx);

        Assert.Equal(string.Empty, note);
    }

    [Fact]
    public void CoverageNote_CompleteRead_SaysComplete()
    {
        var full = Path.Combine(_root, "a.txt");
        File.WriteAllText(full, "x");

        _ctx.Tickets.Issue(full);   // 整份读取

        var note = VerifyProfiles.CoverageNote(
            new[] { Call("ws_write", "{\"path\":\"a.txt\"}") }, _ctx);

        Assert.Contains("已完整读取", note);
    }
}
