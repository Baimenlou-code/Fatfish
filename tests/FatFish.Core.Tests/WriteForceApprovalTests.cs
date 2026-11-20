using System.Text.Json;
using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 「强行覆盖未读全的文件」这道审批闸（2026-11-05，P5）。
/// </summary>
/// <remarks>
/// ★ 两道断言缺一不可：
///   1. 带 confirm_partial_overwrite 的 ws_write **在任何档位都要人工确认** ——
///      包括「除敏感外免批」这种本该放行一切的档位；
///   2. 它**永不给一键放行** —— 否则用户点过一次「本批别问了」，
///      后面所有强行覆盖都会静默通过，这道闸等于不存在。
/// ★ 这两条都是「不报错就失灵」的类型：少写一条，行为只是悄悄变松。
/// </remarks>
public sealed class WriteForceApprovalTests
{
    private static ApprovalPolicy Policy(ApprovalLevel level)
    {
        var root = Path.Combine(Path.GetTempPath(), "fatfish_fa_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        return new ApprovalPolicy(new PathGuard(root)) { Level = level };
    }

    private static IReadOnlyDictionary<string, JsonElement> Args(bool force)
        => new Dictionary<string, JsonElement>
        {
            ["path"] = JsonSerializer.SerializeToElement("a.txt"),
            ["confirm_partial_overwrite"] = JsonSerializer.SerializeToElement(force),
        };

    [Theory]
    [InlineData(ApprovalLevel.ReadFree)]
    [InlineData(ApprovalLevel.WriteFree)]
    [InlineData(ApprovalLevel.SensitiveByPassword)]
    public void ForceOverwrite_AlwaysNeedsApproval(ApprovalLevel level)
    {
        var verdict = Policy(level).NeedsApproval("ws_write", Args(force: true));

        Assert.True(verdict.Need);
        Assert.Contains("没有完整读过", verdict.Reason);
    }

    [Theory]
    [InlineData(ApprovalLevel.ReadFree)]
    [InlineData(ApprovalLevel.WriteFree)]
    [InlineData(ApprovalLevel.SensitiveByPassword)]
    public void ForceOverwrite_NeverAutoApproved(ApprovalLevel level)
    {
        Assert.True(Policy(level).NeverAutoApproves("ws_write", Args(force: true)));
    }

    [Fact]
    public void WithoutFlag_WriteFreeStillAllows()
    {
        // 回归：这道闸不能顺带把普通的 ws_write 也拦下来
        var verdict = Policy(ApprovalLevel.WriteFree).NeedsApproval("ws_write", Args(force: false));

        Assert.False(verdict.Need);
        Assert.False(Policy(ApprovalLevel.WriteFree).NeverAutoApproves("ws_write", Args(force: false)));
    }

    [Fact]
    public void NullArgs_DoesNotCrash()
    {
        Assert.False(Policy(ApprovalLevel.WriteFree).NeedsApproval("ws_write", null).Need);
    }
}
