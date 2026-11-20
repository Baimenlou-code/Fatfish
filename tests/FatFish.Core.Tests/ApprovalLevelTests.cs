using System.Text.Json;
using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 审批档位（<see cref="ApprovalLevel"/>）的回归测试。
/// </summary>
/// <remarks>
/// ★ 为什么单独一个文件：
///   2026-11 加入「审批档位」时默认值是 <see cref="ApprovalLevel.Strict"/>，
///   而此前 18 条测试都按「读免批」的语义写 —— 它们没有显式指定档位，
///   于是全部变红（错误信息都是「审批档位=严格」）。
///   修复方向是给那些测试显式指定宽松档，<b>而不是</b>把默认值改松：
///   默认「最严」是有意的，写歪的代价是用户每次读文件都弹窗。
///
///   但这样一来，「默认值是 Strict」这条契约就没有任何测试锁住了 ——
///   将来谁把默认值改成 ReadFree，测试会全绿，而线上会静默变松。
///   所以本文件专门锁住这条契约。
/// </remarks>
public class ApprovalLevelTests : IDisposable
{
    private readonly string _root;
    private readonly PathGuard _guard;

    public ApprovalLevelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_level_" + Guid.NewGuid().ToString("N")[..12]);
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
        }

        GC.SuppressFinalize(this);
    }

    private static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            map[key] = JsonSerializer.SerializeToElement(value);
        return map;
    }

    /// <summary>新建策略时**不**指定档位，用默认值。</summary>
    private ApprovalPolicy Default()
        => new(_guard) { AutoMode = AutoApproveMode.All, Scope = ApproveScope.Writes };

    /// <summary>默认档位必须是 Strict —— 这是全仓最容易被顺手改掉的一条契约。</summary>
    [Fact]
    public void Level_DefaultsToStrict()
    {
        Assert.Equal(ApprovalLevel.Strict, Default().Level);
    }

    /// <summary>Strict 档连只读都要点一次，且原因要写得清楚。</summary>
    [Theory]
    [InlineData("ws_read")]
    [InlineData("ws_list")]
    [InlineData("ws_search")]
    [InlineData("ws_where")]
    [InlineData("ws_write")]
    [InlineData("ws_run_cmd")]
    public void StrictLevel_RequiresEveryTool(string tool)
    {
        var verdict = Default().NeedsApproval(tool, null);

        Assert.True(verdict.Need, $"{tool} 在最严档必须报批");
        Assert.Contains("严格", verdict.Reason);
    }

    /// <summary>把档位调到读免批之后，同一批只读工具就该放行 —— 证明差异确实来自档位。</summary>
    [Theory]
    [InlineData("ws_read")]
    [InlineData("ws_list")]
    [InlineData("ws_search")]
    public void ReadFreeLevel_LetsReadOnlyToolsThrough(string tool)
    {
        var policy = Default();
        policy.Level = ApprovalLevel.ReadFree;

        Assert.False(policy.NeedsApproval(tool, null).Need);
    }

    /// <summary>放宽到读免批之后，写与执行仍然要报批。</summary>
    [Theory]
    [InlineData("ws_write")]
    [InlineData("ws_delete")]
    [InlineData("ws_run_cmd")]
    [InlineData("ws_run_python")]
    public void ReadFreeLevel_StillRequiresWritesAndExec(string tool)
    {
        var policy = Default();
        policy.Level = ApprovalLevel.ReadFree;

        Assert.True(policy.NeedsApproval(tool, null).Need);
    }

    /// <summary>
    /// 敏感文件是「非严格档位里最硬的一条线」：即使调到 WriteFree 也必须报批。
    /// </summary>
    [Fact]
    public void WriteFreeLevel_StillRequiresSensitiveFile()
    {
        var policy = Default();
        policy.Level = ApprovalLevel.WriteFree;

        var verdict = policy.NeedsApproval("ws_read", Args(("path", ".env")));

        Assert.True(verdict.Need);
        Assert.Contains("敏感", verdict.Reason);
    }

    /// <summary>档位 4：敏感文件改用密码代替人工确认，而不是直接放行。</summary>
    [Fact]
    public void SensitiveByPasswordLevel_UsesPasswordInsteadOfApproval()
    {
        var policy = Default();
        policy.Level = ApprovalLevel.SensitiveByPassword;

        var verdict = policy.NeedsApproval("ws_read", Args(("path", ".env")));

        Assert.True(verdict.Need);
        Assert.Equal(ApprovalKind.Password, verdict.Kind);
    }

    /// <summary>
    /// 档位序号的相对大小有意义（数值越大越宽松）。
    /// 改动枚举顺序会让所有 <c>level &gt;= X</c> 的比较悄悄改变含义。
    /// </summary>
    [Fact]
    public void LevelOrder_IsFromStrictToLoosest()
    {
        Assert.True(ApprovalLevel.Strict < ApprovalLevel.ReadFree);
        Assert.True(ApprovalLevel.ReadFree < ApprovalLevel.WriteFree);
        Assert.True(ApprovalLevel.WriteFree < ApprovalLevel.SensitiveByPassword);
    }
}
