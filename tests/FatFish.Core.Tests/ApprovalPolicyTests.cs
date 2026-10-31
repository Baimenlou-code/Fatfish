using System.Text.Json;
using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// ApprovalPolicy 的判定测试。
///
/// 这里有两组**回归测试**，对应两个曾经真实存在、且被静默了一段时间的缺陷：
///
///   缺陷一：<c>NeedsApproval</c> 只查 <c>ApprovalRequired</c>，不查 <c>HighRiskTools</c>。
///           ws_run_cmd / ws_run_python 只登记在后者里，于是走到最后一步
///           <c>NotNeeded</c> —— 「能突破工作台边界」的工具恰好是唯一免审批的。
///           当时工具还没实现，所以谁也看不出来；一挂上就是敞开的门。
///
///   缺陷二：<c>NeverAutoApproves</c> 在 <c>AutoMode.All</c> 分支直接 <c>return false</c>，
///           把整张 <c>NeverAutoApprove</c> 名单作废了。
///           而默认配置正是 AutoMode.All，所以 ws_delete / ws_run_cmd 这些
///           「必须亲手点」的工具，在用户选过一次「本会话放行」后就会被自动放行 ——
///           与属性注释写的语义完全相反。
///
/// 两个缺陷都不会抛异常、不会报错，只会让闸门悄悄失效。所以只能用测试锁住。
/// </summary>
public class ApprovalPolicyTests : IDisposable
{
    private readonly string _root;
    private readonly PathGuard _guard;

    public ApprovalPolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_policy_" + Guid.NewGuid().ToString("N")[..12]);
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

    private ApprovalPolicy NewPolicy(
        AutoApproveMode autoMode = AutoApproveMode.All,
        ApproveScope scope = ApproveScope.Writes)
        => new(_guard) { AutoMode = autoMode, Scope = scope, Level = ApprovalLevel.ReadFree };

    private static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            map[key] = JsonSerializer.SerializeToElement(value);
        return map;
    }

    // ==================================================================
    // NeedsApproval
    // ==================================================================

    [Theory]
    [InlineData("ws_write")]
    [InlineData("ws_append")]
    [InlineData("ws_replace")]
    [InlineData("ws_delete")]
    // 下面两个是缺陷一的回归项
    [InlineData("ws_run_cmd")]
    [InlineData("ws_run_python")]
    public void NeedsApproval_WritesAndHighRisk_AreRequired(string tool)
    {
        var verdict = NewPolicy().NeedsApproval(tool, null);

        Assert.True(verdict.Need, $"{tool} 必须报批");
    }

    [Theory]
    [InlineData("ws_read")]
    [InlineData("ws_list")]
    [InlineData("ws_search")]
    public void NeedsApproval_ReadOnlyTools_AreNotRequired(string tool)
    {
        Assert.False(NewPolicy().NeedsApproval(tool, null).Need);
    }

    [Fact]
    public void NeedsApproval_ReadSensitiveFile_IsRequired()
    {
        var verdict = NewPolicy().NeedsApproval("ws_read", Args(("path", ".env")));

        Assert.True(verdict.Need);
        Assert.Contains("敏感", verdict.Reason);
    }

    [Fact]
    public void NeedsApproval_ReadNormalFile_IsNotRequired()
    {
        Assert.False(NewPolicy().NeedsApproval("ws_read", Args(("path", "src/main.cs"))).Need);
    }

    [Fact]
    public void NeedsApproval_ApproveScopeAll_MakesReadsRequireApproval()
    {
        var verdict = NewPolicy(scope: ApproveScope.All)
            .NeedsApproval("ws_read", Args(("path", "notes.txt")));

        Assert.True(verdict.Need);
    }

    [Fact]
    public void NeedsApproval_HighRiskReason_IsHumanReadable()
    {
        var verdict = NewPolicy().NeedsApproval("ws_run_cmd", null);

        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
        Assert.Contains("命令", verdict.Reason);
    }

    // ==================================================================
    // NeverAutoApproves —— 「一键放行」的豁免名单
    // ==================================================================

    [Theory]
    // 缺陷二的回归项：默认 AutoMode.All 下，硬性拒绝名单必须仍然生效
    [InlineData("ws_run_cmd")]
    [InlineData("ws_run_python")]
    [InlineData("ws_bg_cmd")]
    [InlineData("ws_bg_python")]
    [InlineData("ws_bg_kill")]
    [InlineData("ws_delete")]
    public void NeverAutoApproves_UnderDefaultMode_DenyListWins(string tool)
    {
        Assert.True(NewPolicy().NeverAutoApproves(tool, null),
            $"{tool} 在默认配置下也不该被一键放行");
    }

    [Fact]
    public void NeverAutoApproves_UnderDefaultMode_OrdinaryWritesCanBeOneKeyed()
    {
        // ws_write 不在拒绝名单里：用户选了「本会话放行」之后就该真的省事
        Assert.False(NewPolicy().NeverAutoApproves("ws_write", null));
    }

    [Fact]
    public void NeverAutoApproves_ModeNone_BlocksEverything()
    {
        var policy = NewPolicy(autoMode: AutoApproveMode.None);

        Assert.True(policy.NeverAutoApproves("ws_write", null));
        Assert.True(policy.NeverAutoApproves("ws_read", null));
    }

    [Fact]
    public void NeverAutoApproves_SensitivePath_BlocksEvenOrdinaryWrite()
    {
        // 往 .env 写同样是「碰密钥」，不能因为它是 ws_write 就放松
        Assert.True(NewPolicy().NeverAutoApproves("ws_write", Args(("path", ".env"))));
        Assert.True(NewPolicy().NeverAutoApproves("ws_write", Args(("path", "config/prod.env"))));
        Assert.True(NewPolicy().NeverAutoApproves("ws_write", Args(("path", "id_rsa"))));
    }

    [Fact]
    public void NeverAutoApproves_SelectiveMode_BlocksHighRisk()
    {
        var policy = NewPolicy(autoMode: AutoApproveMode.Selective);

        Assert.True(policy.NeverAutoApproves("ws_run_cmd", null));
        Assert.False(policy.NeverAutoApproves("ws_write", null));
    }

    // ==================================================================
    // 敏感判定本身
    // ==================================================================

    [Theory]
    [InlineData(".env", true)]
    [InlineData(".env.local", true)]
    [InlineData("deploy/prod.env", true)]
    [InlineData("id_rsa", true)]
    [InlineData("server.key", true)]
    [InlineData("cert.pem", true)]
    [InlineData("src/main.cs", false)]
    [InlineData("README.md", false)]
    [InlineData("environment.md", false)]
    public void CheckSensitive_MatchesExpected(string path, bool expected)
    {
        Assert.Equal(expected, _guard.CheckSensitive(path).IsSensitive);
    }
}
