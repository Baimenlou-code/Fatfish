using FatFish.Core.Config;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 配置图谱与体检测试。
/// </summary>
/// <remarks>
/// 这里守的是两件容易被忽略、但一错就出大事的事：
///   1. <b>定义本身要自洽</b>：键名唯一、分组存在、默认值能通过自己的校验。
///      最后一条最有用 —— 它是「界面上写着默认 8192，代码里其实用 131072」的防线；
///   2. <b>体检要能抓到真实的组合问题</b>：两个 key 值不同、审查员换了模型没换地址。
///      这些全部来自本仓库真实踩过的坑，不是凭空加的检查。
/// </remarks>
public class EnvSchemaTests
{
    // ------------------------------------------------------------------
    // 定义自洽性
    // ------------------------------------------------------------------

    [Fact]
    public void AllKeys_AreUnique()
    {
        var dup = EnvSchema.Fields
            .GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(dup);
    }

    [Fact]
    public void AllKeys_FollowEnvNamingConvention()
    {
        foreach (var f in EnvSchema.Fields)
        {
            Assert.True(EnvValueCodec.IsValidKey(f.Key), $"键名不合法：{f.Key}");
            Assert.Equal(f.Key.ToUpperInvariant(), f.Key); // 与 Python 版 / 环境变量习惯一致
        }
    }

    [Fact]
    public void AllFields_BelongToDeclaredGroups()
    {
        var groups = EnvSchema.Groups.Select(g => g.Name).ToHashSet();

        foreach (var f in EnvSchema.Fields)
            Assert.Contains(f.Group, groups);
    }

    [Fact]
    public void EveryGroup_HasAtLeastOneField()
    {
        foreach (var g in EnvSchema.Groups.Where(g => g.Name != EnvSchema.GroupOther))
            Assert.NotEmpty(EnvSchema.InGroup(g.Name));
    }

    [Fact]
    public void Defaults_PassTheirOwnValidation()
    {
        foreach (var f in EnvSchema.Fields.Where(f => f.Default is not null))
        {
            var err = EnvSchema.Validate(f, f.Default!);
            Assert.True(err is null, $"{f.Key} 的默认值 '{f.Default}' 通不过自己的校验：{err}");
        }
    }

    [Fact]
    public void ChoiceFields_DefaultsAreInChoices()
    {
        foreach (var f in EnvSchema.Fields.Where(f => f.Kind == EnvFieldKind.Choice && f.Default is not null))
            Assert.Contains(f.Default!, f.Choices);
    }

    // ------------------------------------------------------------------
    // 单键校验
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("not-a-url", true)]
    [InlineData("ftp://x.com", true)]
    [InlineData("https://api.deepseek.com", false)]
    [InlineData("http://127.0.0.1:8080/v1", false)]
    public void Url_Validation(string value, bool shouldFail)
    {
        var field = EnvSchema.Find("FATFISH_BASE_URL")!;

        Assert.Equal(shouldFail, EnvSchema.Validate(field, value) is not null);
    }

    [Theory]
    [InlineData("131072", null)]
    [InlineData("abc", "要填整数")]
    [InlineData("10", "不能小于 256")]
    [InlineData("2000000", "不能大于 1048576")]
    public void Integer_Validation(string value, string? expect)
    {
        var err = EnvSchema.Validate(EnvSchema.Find("FATFISH_MAX_TOKENS")!, value);

        if (expect is null)
            Assert.Null(err);
        else
            Assert.Contains(expect, err);
    }

    [Fact]
    public void RequiredField_RejectsEmpty()
    {
        var field = EnvSchema.Find("FATFISH_API_KEY")!;

        Assert.NotNull(EnvSchema.Validate(field, ""));
        Assert.Null(EnvSchema.Validate(field, "  sk-x  "));
    }

    [Fact]
    public void CustomChoice_AllowsUnknownModelName()
    {
        var field = EnvSchema.Find("FATFISH_MODEL")!;

        Assert.Null(EnvSchema.Validate(field, "some-new-model"));
    }

    // ------------------------------------------------------------------
    // 体检
    // ------------------------------------------------------------------

    private static EnvDocument Doc(params string[] lines) => EnvDocument.Parse(string.Join("\n", lines));

    [Fact]
    public void Health_NoKeyAtAll_IsError()
    {
        var issues = EnvHealth.Inspect(Doc("FATFISH_MODEL=deepseek-flash"));

        Assert.Contains(issues, i => i.Level == EnvIssueLevel.Error && i.Message.Contains("一个 API Key 都没有"));
    }

    [Fact]
    public void Health_TwoDifferentKeys_IsWarning()
    {
        var issues = EnvHealth.Inspect(Doc(
            "FATFISH_API_KEY=sk-aaa",
            "DEEPSEEK_API_KEY=sk-bbb"));

        Assert.Contains(issues, i =>
            i.Level == EnvIssueLevel.Warning && i.Message.Contains("实际生效的是前者"));
    }

    [Fact]
    public void Health_SameKeyTwice_IsNotWarning()
    {
        var issues = EnvHealth.Inspect(Doc(
            "FATFISH_API_KEY=sk-aaa",
            "DEEPSEEK_API_KEY=sk-aaa"));

        Assert.DoesNotContain(issues, i => i.Message.Contains("实际生效的是前者"));
    }

    [Fact]
    public void Health_VerifierModelWithoutBaseUrl_IsWarning()
    {
        var issues = EnvHealth.Inspect(Doc(
            "FATFISH_API_KEY=sk-aaa",
            "VERIFIER_MODEL=some-other-model"));

        Assert.Contains(issues, i =>
            i.Level == EnvIssueLevel.Warning && i.Key == "VERIFIER_BASE_URL");
    }

    [Fact]
    public void Health_VerifyAllWithSameModel_IsWarning()
    {
        var issues = EnvHealth.Inspect(Doc(
            "FATFISH_API_KEY=sk-aaa",
            "VERIFY_MODE=all"));

        Assert.Contains(issues, i => i.Message.Contains("每轮对话会多花一次同等的 token"));
    }

    [Fact]
    public void Health_DuplicateKey_IsWarning()
    {
        var issues = EnvHealth.Inspect(Doc(
            "FATFISH_API_KEY=sk-aaa",
            "FATFISH_MODEL=deepseek-flash",
            "FATFISH_MODEL=deepseek-chat"));

        Assert.Contains(issues, i => i.Message.Contains("出现了多次"));
    }

    [Fact]
    public void Health_UnknownKeys_AreNoted_AndStillSurviveRoundTrip()
    {
        var doc = Doc(
            "FATFISH_API_KEY=sk-aaa",
            "MY_OWN_KEY=hello");

        var issues = EnvHealth.Inspect(doc);

        Assert.Contains(issues, i => i.Level == EnvIssueLevel.Note && i.Message.Contains("MY_OWN_KEY"));

        // 关键：体检报告里提到了它，但保存时不许动它
        doc.Set("FATFISH_MODEL", "deepseek-v4-pro");
        Assert.Contains("MY_OWN_KEY=hello", doc.Serialize());
    }

    [Fact]
    public void Health_BrokenLines_AreWarnedButPreserved()
    {
        var doc = Doc(
            "FATFISH_API_KEY=sk-aaa",
            "这不是一行配置");

        Assert.Contains(EnvHealth.Inspect(doc), i => i.Message.Contains("认不出来"));
        Assert.Contains("这不是一行配置", doc.Serialize());
    }

    [Fact]
    public void Health_GoodConfig_HasNoErrors()
    {
        var issues = EnvHealth.Inspect(Doc(
            "FATFISH_API_KEY=sk-aaa",
            "FATFISH_MODEL=deepseek-flash",
            "VERIFY_MODE=auto",
            "LIQIU_API_KEY=lk-xxx"));

        Assert.Equal(0, EnvHealth.Count(issues, EnvIssueLevel.Error));
    }
}
