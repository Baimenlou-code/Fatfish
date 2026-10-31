using FatFish.Core.Llm;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 会修改进程环境变量的测试必须独占运行。
///
/// xUnit 默认让不同测试集合并行跑。这些用例会临时清空 / 改写
/// FATFISH_* 与 DEEPSEEK_*，如果不隔离，并行中的其它用例可能读到
/// 「刚好被这里清空」的键，于是随机失败 —— 而且失败点看起来毫无道理。
/// </summary>
[CollectionDefinition(EnvironmentMutatingCollectionDefinition.Name, DisableParallelization = true)]
public sealed class EnvironmentMutatingCollectionDefinition
{
    public const string Name = "EnvironmentMutating";
}

/// <summary>
/// LLM 配置的取值规则测试（2026-10-29 配置对齐那次改动）。
///
/// 守的是「两个版本共用一份 .env」这件事：
///   · 键名优先级 FATFISH_* → DEEPSEEK_*；
///   · 模型默认 deepseek-flash、max_tokens 131072、温度 0.7 —— 与 Python 版一致。
///
/// 这类偏差特别难发现：两边都能跑，回复看起来也正常，
/// 只有等到「长回答被截断」或「账单对不上」时才会回头查。
/// 所以宁可把每个默认值都钉死在测试里。
/// </summary>
[Collection(EnvironmentMutatingCollectionDefinition.Name)]
public class LlmOptionsTests : IDisposable
{
    /// <summary>本类会动到的全部键。改动前备份、Dispose 时原样还回去。</summary>
    private static readonly string[] Keys =
    [
        "FATFISH_API_KEY", "DEEPSEEK_API_KEY",
        "FATFISH_BASE_URL", "DEEPSEEK_BASE_URL",
        "FATFISH_MODEL", "DEEPSEEK_MODEL",
        "FATFISH_MAX_TOKENS", "MAX_REPLY_TOKENS", "DEEPSEEK_MAX_TOKENS",
        "FATFISH_TEMPERATURE", "DEEPSEEK_TEMPERATURE",
        "FATFISH_TIMEOUT", "DEEPSEEK_TIMEOUT",
    ];

    private readonly Dictionary<string, string?> _saved = new(StringComparer.Ordinal);

    public LlmOptionsTests()
    {
        foreach (var key in Keys)
        {
            _saved[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    public void Dispose()
    {
        foreach (var (key, value) in _saved)
            Environment.SetEnvironmentVariable(key, value);

        GC.SuppressFinalize(this);
    }

    // ==================================================================
    // 默认值：必须与 Python 版一致
    // ==================================================================

    [Fact]
    public void Defaults_MatchPythonVersion()
    {
        Environment.SetEnvironmentVariable("FATFISH_API_KEY", "sk-test");

        var o = LlmOptions.FromEnvironment();

        Assert.Equal("deepseek-flash", o.Model);
        Assert.Equal(131072, o.MaxTokens);
        Assert.Equal(0.7, o.Temperature, 6);
        Assert.Equal("https://api.deepseek.com", o.BaseUrl);
        Assert.Equal("FATFISH_API_KEY", o.ApiKeySource);
    }

    // ==================================================================
    // 键名优先级
    // ==================================================================

    /// <summary>
    /// 两个键同时存在时，必须用 FATFISH_API_KEY —— 那才是 .env 里与 Python 版
    /// 同一个账号的那个。这条如果坏了，两个版本就各花各的钱。
    /// </summary>
    [Fact]
    public void FatfishKey_TakesPrecedence()
    {
        Environment.SetEnvironmentVariable("FATFISH_API_KEY", "sk-fatfish");
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-deepseek");

        var o = LlmOptions.FromEnvironment();

        Assert.Equal("sk-fatfish", o.ApiKey);
        Assert.Equal("FATFISH_API_KEY", o.ApiKeySource);
    }

    [Fact]
    public void FallsBack_ToDeepseekKey()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-deepseek");

        var o = LlmOptions.FromEnvironment();

        Assert.Equal("sk-deepseek", o.ApiKey);
        Assert.Equal("DEEPSEEK_API_KEY", o.ApiKeySource);
    }

    [Fact]
    public void Throws_WhenNoKey()
    {
        Assert.Throws<InvalidOperationException>(() => LlmOptions.FromEnvironment());
    }

    [Fact]
    public void TryResolveApiKey_DoesNotThrow()
    {
        Assert.False(LlmOptions.TryResolveApiKey(out var key, out var source));
        Assert.Equal(string.Empty, key);
        Assert.Equal(string.Empty, source);

        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-d");

        Assert.True(LlmOptions.TryResolveApiKey(out key, out source));
        Assert.Equal("sk-d", key);
        Assert.Equal("DEEPSEEK_API_KEY", source);
    }

    [Fact]
    public void FatfishValues_WinOverDeepseekValues()
    {
        Environment.SetEnvironmentVariable("FATFISH_API_KEY", "sk-test");
        Environment.SetEnvironmentVariable("FATFISH_MODEL", "deepseek-v4-pro");
        Environment.SetEnvironmentVariable("DEEPSEEK_MODEL", "deepseek-chat");
        Environment.SetEnvironmentVariable("FATFISH_BASE_URL", "https://a.example.com");
        Environment.SetEnvironmentVariable("DEEPSEEK_BASE_URL", "https://b.example.com");

        var o = LlmOptions.FromEnvironment();

        Assert.Equal("deepseek-v4-pro", o.Model);
        Assert.Equal("https://a.example.com", o.BaseUrl);
    }

    // ==================================================================
    // 覆盖键
    // ==================================================================

    /// <summary>
    /// MAX_REPLY_TOKENS 是 Python 版的名字。认它，才能做到「一份 .env 两边都生效」。
    /// </summary>
    [Fact]
    public void MaxReplyTokens_IsHonored()
    {
        Environment.SetEnvironmentVariable("FATFISH_API_KEY", "sk-test");
        Environment.SetEnvironmentVariable("MAX_REPLY_TOKENS", "4096");

        Assert.Equal(4096, LlmOptions.FromEnvironment().MaxTokens);
    }

    [Fact]
    public void FatfishMaxTokens_BeatsMaxReplyTokens()
    {
        Environment.SetEnvironmentVariable("FATFISH_API_KEY", "sk-test");
        Environment.SetEnvironmentVariable("FATFISH_MAX_TOKENS", "2048");
        Environment.SetEnvironmentVariable("MAX_REPLY_TOKENS", "4096");

        Assert.Equal(2048, LlmOptions.FromEnvironment().MaxTokens);
    }

    // ==================================================================
    // 日志脱敏
    // ==================================================================

    [Fact]
    public void Describe_NeverLeaksFullKey()
    {
        Environment.SetEnvironmentVariable("FATFISH_API_KEY", "sk-abcdefghijklmnop");

        var text = LlmOptions.FromEnvironment().Describe();

        Assert.DoesNotContain("sk-abcdefghijklmnop", text);
        Assert.Contains("mnop", text);              // 尾 4 位留着，用于区分是哪个 key
        Assert.Contains("FATFISH_API_KEY", text);   // 来源必须写明
    }
}
