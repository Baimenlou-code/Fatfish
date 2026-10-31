using FatFish.Core.Llm;
using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 核验配置解析测试。
///
/// 这一组的重点不是「能不能读到值」，而是**边界值的方向**：
///   · <c>VERIFIER_MAX_TOKENS=0</c> 在 Python 版里表示「不覆盖」——
///     照字面赋 0 会让审查员一个字都说不出来，然后每次都解析失败 → 全部打回；
///   · <c>VERIFY_MAX_RETRIES=0</c> 却是**有效值**（表示一次都不打回）。
/// 同样是 0，一个要回落、一个要采用。写错了不会报错，只会悄悄地变得很严或很松。
/// </summary>
public class VerifyOptionsTests
{
    private static readonly LlmOptions Main = new()
    {
        ApiKey = "sk-main-0000000000000000",
        ApiKeySource = "FATFISH_API_KEY",
        BaseUrl = "https://main.example.com",
        Model = "main-model",
    };

    private static VerifyOptions Load(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal);
        return VerifyOptions.FromLookup(k => map.GetValueOrDefault(k), Main);
    }

    // ==================================================================
    // 默认值
    // ==================================================================

    [Fact]
    public void Defaults_MatchPython()
    {
        var o = Load();

        Assert.Equal(VerifyMode.Auto, o.Mode);
        Assert.True(o.Strict);
        Assert.Equal(2, o.MaxRetries);
        Assert.Equal(4, o.MaxSupplements);
        Assert.Equal(VerifyFailMode.Open, o.FailMode);
        Assert.False(o.VerifyFinalAnswer);
        Assert.True(o.IsEnabled);

        // 审查员留空 → 沿用主模型
        Assert.Equal("main-model", o.Endpoint.Model);
        Assert.Equal(Main.ApiKey, o.Endpoint.ApiKey);
        Assert.Equal(Main.ApiKeySource, o.Endpoint.ApiKeySource);

        Assert.Equal(0.2, o.Endpoint.Temperature);
        Assert.Equal(2000, o.Endpoint.MaxTokens);
        Assert.Equal(120, o.Endpoint.TimeoutSeconds);
    }

    // ==================================================================
    // 模式
    // ==================================================================

    [Theory]
    [InlineData("off", VerifyMode.Off)]
    [InlineData("OFF", VerifyMode.Off)]
    [InlineData("0", VerifyMode.Off)]
    [InlineData("false", VerifyMode.Off)]
    [InlineData("all", VerifyMode.All)]
    [InlineData("auto", VerifyMode.Auto)]
    [InlineData("", VerifyMode.Auto)]
    [InlineData("垃圾值", VerifyMode.Auto)]
    public void Mode_Parsing(string raw, VerifyMode expected)
    {
        Assert.Equal(expected, Load(("VERIFY_MODE", raw)).Mode);
    }

    [Fact]
    public void Mode_Off_IsDisabled()
    {
        var o = Load(("VERIFY_MODE", "off"));

        Assert.False(o.IsEnabled);
        Assert.Contains("OFF", o.Describe());
    }

    // ==================================================================
    // 0 的两种语义
    // ==================================================================

    [Fact]
    public void MaxTokens_Zero_MeansNoOverride()
    {
        // 与 Python 版一致：0 = 不覆盖（否则审查员输出为空，全部解析失败）
        Assert.Equal(2000, Load(("VERIFIER_MAX_TOKENS", "0")).Endpoint.MaxTokens);
    }

    [Fact]
    public void MaxRetries_Zero_IsValidValue()
    {
        // 这里 0 是有效值：一次都不打回
        Assert.Equal(0, Load(("VERIFY_MAX_RETRIES", "0")).MaxRetries);
    }

    [Fact]
    public void MaxSupplements_Zero_IsValidValue()
    {
        Assert.Equal(0, Load(("VERIFY_MAX_SUPPLEMENTS", "0")).MaxSupplements);
    }

    [Fact]
    public void PromptChars_BelowFloor_FallsBack()
    {
        // 下限 2000：给 100 会让送审材料短到审查员什么都看不见
        Assert.Equal(64_000, Load(("VERIFIER_PROMPT_CHARS", "100")).PromptMaxChars);
    }

    // ==================================================================
    // 审查员端点
    // ==================================================================

    [Fact]
    public void VerifierModel_Overrides()
    {
        Assert.Equal("other-model", Load(("VERIFIER_MODEL", "other-model")).Endpoint.Model);
    }

    [Fact]
    public void VerifierKey_ValidKey_IsUsed()
    {
        var o = Load(("VERIFIER_API_KEY", "vk-abcdefghijklmnopqrstuvwxyz"));

        Assert.Equal("vk-abcdefghijklmnopqrstuvwxyz", o.Endpoint.ApiKey);
        Assert.Equal("VERIFIER_API_KEY", o.Endpoint.ApiKeySource);
    }

    [Theory]
    // 带空格 → 不像 key（多半是复制时多带了东西）
    [InlineData("vk abcdefghijklmnopqrstuvwxyz")]
    // 填了个 URL（最容易犯的错：把 BASE_URL 的值抄到 API_KEY）
    [InlineData("https://api.example.com")]
    // 太短（被截断了）
    [InlineData("sk-1234")]
    public void VerifierKey_Malformed_FallsBackToMain(string bad)
    {
        var o = Load(("VERIFIER_API_KEY", bad));

        Assert.Equal(Main.ApiKey, o.Endpoint.ApiKey);
        Assert.Equal(Main.ApiKeySource, o.Endpoint.ApiKeySource);
    }

    [Fact]
    public void VerifierKey_Empty_FallsBackToMain()
    {
        var o = Load(("VERIFIER_API_KEY", ""));

        Assert.Equal(Main.ApiKey, o.Endpoint.ApiKey);
    }

    [Fact]
    public void Temperature_IsClamped()
    {
        Assert.Equal(2.0, Load(("VERIFIER_TEMPERATURE", "5")).Endpoint.Temperature);
        Assert.Equal(0.0, Load(("VERIFIER_TEMPERATURE", "-3")).Endpoint.Temperature);
    }

    [Fact]
    public void ChatUrl_HandlesAllForms()
    {
        var e = new VerifierEndpoint { ApiKey = "x", BaseUrl = "https://a.example.com" };
        Assert.Equal("https://a.example.com/chat/completions", e.ChatUrl);

        var e2 = new VerifierEndpoint { ApiKey = "x", BaseUrl = "https://a.example.com/v1" };
        Assert.Equal("https://a.example.com/v1/chat/completions", e2.ChatUrl);

        var e3 = new VerifierEndpoint { ApiKey = "x", BaseUrl = "https://a.example.com/chat/completions" };
        Assert.Equal("https://a.example.com/chat/completions", e3.ChatUrl);
    }

    // ==================================================================
    // ShouldVerify
    // ==================================================================

    [Theory]
    // auto：只核验有副作用的
    [InlineData("auto", "ws_write", true, true)]
    [InlineData("auto", "ws_run_cmd", true, true)]
    [InlineData("auto", "ws_read", false, false)]
    [InlineData("auto", "ws_list", false, false)]
    // all：连只读也核验
    [InlineData("all", "ws_read", false, true)]
    [InlineData("all", "ws_write", true, true)]
    // off：什么都不核验
    [InlineData("off", "ws_write", true, false)]
    [InlineData("off", "ws_read", false, false)]
    public void ShouldVerify_ByMode(string mode, string tool, bool sideEffects, bool expected)
    {
        var o = Load(("VERIFY_MODE", mode));

        Assert.Equal(expected, o.ShouldVerify(tool, sideEffects));
    }

    /// <summary>
    /// ★ 判定靠工具自己声明的 HasSideEffects，而不是一张手写名单。
    ///
    /// 这条锁住的是 Python 版的一个真实缺口：<c>SIDE_EFFECT_TOOLS</c> 是手写集合，
    /// 后来新增的 ws_bg_cmd / ws_bg_python 没被加进去 ——
    /// 于是「把危险命令包进后台任务」是一条能绕过 AI 核验的免费通道，且完全不报错。
    /// </summary>
    [Fact]
    public void ShouldVerify_NewSideEffectTool_IsCoveredWithoutEditingAnyList()
    {
        var o = Load(("VERIFY_MODE", "auto"));

        // 一个本次代码里根本没出现过的工具名照样会被核验 —— 因为它自称有副作用
        Assert.True(o.ShouldVerify("ws_future_dangerous_tool", hasSideEffects: true));
    }

    // ==================================================================
    // 描述
    // ==================================================================

    [Fact]
    public void Describe_ContainsScopeAndModel()
    {
        var text = Load(("VERIFY_MODE", "all")).Describe();

        Assert.Contains("ALL", text);
        Assert.Contains("main-model", text);
    }

    [Fact]
    public void Describe_NeverLeaksFullKey()
    {
        var o = Load(("VERIFIER_API_KEY", "vk-abcdefghijklmnopqrstuvwxyz"));

        Assert.DoesNotContain("vk-abcdefghijklmnopqrstuvwxyz", o.Endpoint.Describe());
        Assert.Contains("***wxyz", o.Endpoint.Describe());
    }

    [Fact]
    public void Disabled_HasNoEndpointRequirement()
    {
        var o = VerifyOptions.Disabled(Main);

        Assert.False(o.IsEnabled);
        Assert.False(o.ShouldVerify("ws_write", true));
    }
}
