using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 审批栏偏好的落盘测试（logs/ui/approval.json）。
/// </summary>
/// <remarks>
/// ★ 为什么这些测试值得写：这段逻辑的失败方式是<b>静默</b>的 ——
///   文件读坏了就退回默认值，用户看到的只是「重启之后设置自己变回去了」，
///   而那时已经离现场很远，查不出是哪个字段、什么原因。
///
/// ★ 两条硬约束：
///   1. <b>旧文件必须还能读</b>（只有 require_subagent_approval、没有 round_limit 的那些）——
///      改了字段结构就让人丢掉原来的设置，是最容易被忽略的回归；
///   2. <b>手改的离谱数字要被挡住</b>：round_limit=99999 如果被原样读回来，
///      一次跑偏的对话就能烧掉整晚的 token，而它的生效路径正是「读回来就用」。
/// </remarks>
public class ApprovalPrefsTests : IDisposable
{
    private readonly string _root;

    public ApprovalPrefsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_prefs_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论
        }

        GC.SuppressFinalize(this);
    }

    private string PrefsPath => Path.Combine(_root, "logs", "ui", ApprovalPrefs.FileName);

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
        File.WriteAllText(PrefsPath, json);
    }

    // ---------- 读取 ----------

    [Fact]
    public void Load_MissingFile_ReturnsStricterDefaults()
    {
        var prefs = ApprovalPrefs.Load(PrefsPath);

        Assert.True(prefs.RequireSubagentApproval);          // 失败即更严
        Assert.Equal(ApprovalPolicy.DefaultRoundLimit, prefs.RoundLimit);
    }

    /// <summary>
    /// 旧版文件（只有开子代理那一个字段）必须仍然能读，
    /// 且回合上限落在默认的 25 —— 而不是 0（不限）。
    /// </summary>
    [Fact]
    public void Load_LegacyFileWithoutRoundLimit_KeepsDefaultLimit()
    {
        WriteRaw("{\"require_subagent_approval\": false}");

        var prefs = ApprovalPrefs.Load(PrefsPath);

        Assert.False(prefs.RequireSubagentApproval);
        Assert.Equal(ApprovalPolicy.DefaultRoundLimit, prefs.RoundLimit);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(25)]
    [InlineData(35)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(0)]
    public void Load_ValidRoundLimit_IsAccepted(int limit)
    {
        WriteRaw($"{{\"require_subagent_approval\": true, \"round_limit\": {limit}}}");

        Assert.Equal(limit, ApprovalPrefs.Load(PrefsPath).RoundLimit);
    }

    /// <summary>
    /// 白名单之外的值一律退回默认：手改 json 不该能绕过菜单的取值范围。
    /// </summary>
    [Theory]
    [InlineData(99999)]
    [InlineData(1)]
    [InlineData(-5)]
    [InlineData(7)]
    public void Load_OutOfRangeRoundLimit_FallsBackToDefault(int limit)
    {
        WriteRaw($"{{\"round_limit\": {limit}}}");

        Assert.Equal(ApprovalPolicy.DefaultRoundLimit, ApprovalPrefs.Load(PrefsPath).RoundLimit);
    }

    [Fact]
    public void Load_BrokenJson_ReturnsDefaults()
    {
        WriteRaw("{ this is not json ");

        var prefs = ApprovalPrefs.Load(PrefsPath);

        Assert.True(prefs.RequireSubagentApproval);
        Assert.Equal(ApprovalPolicy.DefaultRoundLimit, prefs.RoundLimit);
    }

    /// <summary>
    /// 顶层不是对象（比如有人把文件写成一个数组）时也要退回默认，而不是抛异常。
    /// </summary>
    [Fact]
    public void Load_NonObjectRoot_ReturnsDefaults()
    {
        WriteRaw("[1, 2, 3]");

        var prefs = ApprovalPrefs.Load(PrefsPath);

        Assert.True(prefs.RequireSubagentApproval);
        Assert.Equal(ApprovalPolicy.DefaultRoundLimit, prefs.RoundLimit);
    }

    // ---------- 写入 ----------

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var error = ApprovalPrefs.Save(PrefsPath, new ApprovalPrefs.Snapshot(false, 100));
        Assert.Null(error);

        var prefs = ApprovalPrefs.Load(PrefsPath);

        Assert.False(prefs.RequireSubagentApproval);
        Assert.Equal(100, prefs.RoundLimit);
    }

    /// <summary>
    /// 两个字段必须一起写。只写其中一个会把另一个抹掉 ——
    /// 表现是「改了回合上限，子代理开关自己变回默认」，而且只有下次启动才看得见。
    /// </summary>
    [Fact]
    public void Save_WritesBothFields_SoOneDoesNotEraseTheOther()
    {
        Assert.Null(ApprovalPrefs.Save(PrefsPath, new ApprovalPrefs.Snapshot(false, 35)));
        Assert.Null(ApprovalPrefs.Save(PrefsPath, new ApprovalPrefs.Snapshot(false, 50)));

        var prefs = ApprovalPrefs.Load(PrefsPath);

        Assert.False(prefs.RequireSubagentApproval);   // 没被子代理开关的默认值覆盖
        Assert.Equal(50, prefs.RoundLimit);            // 后写的赢
    }

    [Fact]
    public void Save_CreatesMissingDirectories()
    {
        Assert.False(Directory.Exists(Path.Combine(_root, "logs", "ui")));

        var error = ApprovalPrefs.Save(PrefsPath, new ApprovalPrefs.Snapshot(true, 15));

        Assert.Null(error);
        Assert.True(File.Exists(PrefsPath));
    }

    /// <summary>写入后不应留下 .tmp 这类半成品。</summary>
    [Fact]
    public void Save_LeavesNoTempFile()
    {
        Assert.Null(ApprovalPrefs.Save(PrefsPath, new ApprovalPrefs.Snapshot(true, 25)));

        var leftovers = Directory.GetFiles(Path.GetDirectoryName(PrefsPath)!, "*.tmp");
        Assert.Empty(leftovers);
    }
}
