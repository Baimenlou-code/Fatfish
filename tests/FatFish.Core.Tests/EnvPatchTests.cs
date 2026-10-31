using FatFish.Core.Config;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 「只写改动过的键」的规则测试。
/// </summary>
/// <remarks>
/// 这是整个编辑器最该被守住的一条：写错的表现是
/// 「点了一次保存，文件里我写的注释、空行、分组全没了」，
/// 而界面上什么提示都没有 —— 用户只会以为自己手滑。
///
/// 所以这里的第一条用例就断言「什么都没改 = 一个字节都不变」。
/// </remarks>
public class EnvPatchTests
{
    private const string Original =
        "# ===== 主模型 =====\r\n" +
        "# 这个 key 是 8 月换的\r\n" +
        "FATFISH_API_KEY=sk-abc\r\n" +
        "\r\n" +
        "FATFISH_MODEL = deepseek-flash\r\n" +
        "export LEGACY=\"a b\"\r\n" +
        "MY_OWN_KEY=hello\r\n";

    private static (EnvDocument Doc, Dictionary<string, string?> Base) Load()
    {
        var doc = EnvDocument.Parse(Original);

        return (doc, doc.Snapshot()
            .ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoChanges_LeavesFileByteIdentical()
    {
        var (doc, baseline) = Load();

        // 表单把每个键都「读一遍再写回去」—— 这正是窗口保存时的真实行为
        var current = baseline.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        var changed = EnvPatch.Apply(doc, baseline, current);

        Assert.Empty(changed);
        Assert.Equal(Original, doc.Serialize());
    }

    [Fact]
    public void ChangedValue_IsUpdatedInPlace()
    {
        var (doc, baseline) = Load();

        var current = new Dictionary<string, string?>(baseline, StringComparer.OrdinalIgnoreCase)
        {
            ["FATFISH_MODEL"] = "deepseek-v4-pro",
        };

        var changed = EnvPatch.Apply(doc, baseline, current);

        Assert.Equal("FATFISH_MODEL", string.Join(",", changed));

        var text = doc.Serialize();

        Assert.Contains("FATFISH_MODEL=deepseek-v4-pro", text);
        Assert.Contains("# ===== 主模型 =====", text);   // 注释还在
        Assert.Contains("export LEGACY=\"a b\"", text);  // 旧写法没被规范化
        Assert.Contains("\r\n\r\n", text);               // 空行还在
    }

    [Fact]
    public void ClearedValue_RemovesTheKey()
    {
        var (doc, baseline) = Load();

        var current = new Dictionary<string, string?>(baseline, StringComparer.OrdinalIgnoreCase)
        {
            ["FATFISH_MODEL"] = "   ", // 用户把输入框清空了（可能带空格）
        };

        var changed = EnvPatch.Apply(doc, baseline, current);
        var text = doc.Serialize();

        Assert.Contains("FATFISH_MODEL", changed);
        Assert.DoesNotContain("FATFISH_MODEL", text);
        Assert.Contains("FATFISH_API_KEY=sk-abc", text);
    }

    [Fact]
    public void EmptyNewKey_IsNotCreated()
    {
        var (doc, baseline) = Load();

        var current = new Dictionary<string, string?>(baseline, StringComparer.OrdinalIgnoreCase)
        {
            ["BRAND_NEW"] = "",
        };

        var changed = EnvPatch.Apply(doc, baseline, current);

        Assert.Empty(changed);
        Assert.Equal(Original, doc.Serialize());
    }

    [Fact]
    public void FilledNewKey_IsAppended()
    {
        var (doc, baseline) = Load();

        var current = new Dictionary<string, string?>(baseline, StringComparer.OrdinalIgnoreCase)
        {
            ["LIQIU_API_KEY"] = "lk-123",
        };

        var changed = EnvPatch.Apply(doc, baseline, current);

        Assert.Contains("LIQIU_API_KEY", changed);
        Assert.EndsWith("LIQIU_API_KEY=lk-123\r\n", doc.Serialize());
    }

    [Fact]
    public void ValueWithSpaces_IsQuotedSoItReadsBack()
    {
        var (doc, baseline) = Load();

        var current = new Dictionary<string, string?>(baseline, StringComparer.OrdinalIgnoreCase)
        {
            ["VERIFIER_MODEL"] = " some model ",
        };

        EnvPatch.Apply(doc, baseline, current);

        // 读回来必须是「去掉首尾空格之后」的那个值
        Assert.Equal("some model", EnvDocument.Parse(doc.Serialize()).Get("VERIFIER_MODEL"));
    }

    [Fact]
    public void UnknownKeys_AreUntouchedEvenIfNotInSchema()
    {
        var (doc, baseline) = Load();

        Assert.True(doc.Contains("MY_OWN_KEY"));

        var current = new Dictionary<string, string?>(baseline, StringComparer.OrdinalIgnoreCase)
        {
            ["FATFISH_API_KEY"] = "sk-changed",
        };

        EnvPatch.Apply(doc, baseline, current);

        Assert.Contains("MY_OWN_KEY=hello", doc.Serialize());
    }
}
