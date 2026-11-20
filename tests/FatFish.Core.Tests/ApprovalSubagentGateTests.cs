using System;
using System.IO;
using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 「开子代理需人工确认」—— 顶栏「审批」菜单底部那个勾选框（默认开）。
///
/// <para>
/// 这条规则和敏感文件是同一级的硬线，而不是「插件高危工具」那种会被更高档位
/// 顺手放行的一条：开一个子代理 = 放出一段你看不见的连续自主执行。
/// 所以它必须在**任何非严格档位**下都拦，且不接受「本次会话放行」。
/// </para>
///
/// <para>
/// 同时锁住反面：<c>subagent_status</c> 这类轮询/控制工具**不能**进这条线 ——
/// 主循环每隔几秒查一次状态，一旦也要弹窗，用户会在五分钟内把整个机制关掉。
/// </para>
/// </summary>
public class ApprovalSubagentGateTests : IDisposable
{
    private readonly string _root;
    private readonly PathGuard _guard;

    public ApprovalSubagentGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_subagent_gate_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
        _guard = new PathGuard(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响测试结论
        }
    }

    private ApprovalPolicy NewPolicy(ApprovalLevel level)
        => new(_guard) { Level = level };

    [Theory]
    [InlineData(ApprovalLevel.ReadFree)]
    [InlineData(ApprovalLevel.WriteFree)]
    [InlineData(ApprovalLevel.SensitiveByPassword)]
    public void SubagentRun_RequiresApproval_AtEveryNonStrictLevel(ApprovalLevel level)
    {
        var verdict = NewPolicy(level).NeedsApproval("subagent_run", null);

        Assert.True(verdict.Need, $"{level} 档也必须拦下一次「开子代理」");
        Assert.Equal(ApprovalKind.Manual, verdict.Kind);   // 它是人工确认，不是口令闸门
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Fact]
    public void SubagentRun_CannotBeOneKeyed()
    {
        // 「本次会话放行」放行一次 = 放行一整段自主执行，所以连这个口子也不给
        Assert.True(NewPolicy(ApprovalLevel.WriteFree).NeverAutoApproves("subagent_run", null));
    }

    [Fact]
    public void SwitchOff_LetsWriteFreePassIt()
    {
        var policy = NewPolicy(ApprovalLevel.WriteFree);
        policy.RequireApprovalForSubagents = false;

        Assert.False(policy.NeedsApproval("subagent_run", null).Need);
        Assert.False(policy.NeverAutoApproves("subagent_run", null));
    }

    [Fact]
    public void SwitchOn_IsTheDefault()
    {
        // 默认必须是「要审批」：读不到配置 / 老版本没有这个键时，失败即更严
        Assert.True(NewPolicy(ApprovalLevel.WriteFree).RequireApprovalForSubagents);
    }

    [Theory]
    [InlineData("subagent_status")]
    [InlineData("subagent_say")]
    [InlineData("subagent_stop")]
    public void ControlTools_AreNotForced(string tool)
    {
        var policy = NewPolicy(ApprovalLevel.WriteFree);

        Assert.False(policy.NeedsApproval(tool, null).Need,
            $"{tool} 是查询/控制，不该每次弹窗（否则轮询会把人烦死）");
        Assert.False(policy.NeverAutoApproves(tool, null));
    }

    [Fact]
    public void StrictLevel_StillRequiresForEverything()
    {
        // 最严档本来就连只读都要批；这里只是确认子代理规则没有把它弄反
        Assert.True(NewPolicy(ApprovalLevel.Strict).NeedsApproval("subagent_run", null).Need);
        Assert.True(NewPolicy(ApprovalLevel.Strict).NeedsApproval("ws_read", null).Need);
    }
}
