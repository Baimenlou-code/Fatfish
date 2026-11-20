using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FatFish.Core.Config;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 这一组测试会改**进程环境变量**，所以必须独占运行、且不能和其它集合并行 ——
/// 否则相邻测试会把对方设的 FATFISH_ENV 读走，表现为随机失败。
/// </summary>
[CollectionDefinition("config-runtime", DisableParallelization = true)]
public sealed class ConfigRuntimeCollection
{
}

/// <summary>
/// 配置预设 + 热重载。
///
/// <b>为什么这些测试值得写</b>：
///   热重载最容易的实现错误是「调用了看起来正确的 API，但语义错了」——
///   比如复用 <see cref="EnvFile.Load"/>（它「已存在不覆盖」），
///   结果是「重载成功、值没变」，而且不报错。下面
///   <see cref="Reload_OverridesExistingEnvironmentVariable"/> 就是钉住这一条的。
/// </summary>
[Collection("config-runtime")]
public sealed class ConfigPresetTests : IDisposable
{
    private readonly string _root;
    private readonly string? _oldEnvPath;

    public ConfigPresetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_cfg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _oldEnvPath = Environment.GetEnvironmentVariable("FATFISH_ENV");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FATFISH_ENV", _oldEnvPath);

        // 把本测试可能碰过的变量清掉，别污染同进程里的其它测试
        foreach (var key in new[]
                 {
                     "FATFISH_TEST_OVERRIDE", "FATFISH_TEST_STALE", "FATFISH_TEST_PRESET_ONLY",
                     "FATFISH_MODEL", "FATFISH_BASE_URL", "FATFISH_API_KEY",
                 })
        {
            Environment.SetEnvironmentVariable(key, null);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不该让测试失败
        }
    }

    // ------------------------------------------------------------------
    // 预设库
    // ------------------------------------------------------------------

    [Fact]
    public void Store_SaveThenList_KeepsEverythingNeededToSwitch()
    {
        var store = new ConfigPresetStore(_root);

        var path = store.Save("grok 4.7", new Dictionary<string, string?>
        {
            ["FATFISH_MODEL"] = "grok-4.7",
            ["FATFISH_BASE_URL"] = "https://api.x.ai/v1",
            ["FATFISH_API_KEY"] = "xai-abcdefghijklmnop",
        });

        Assert.True(File.Exists(path));

        var only = Assert.Single(store.List());

        Assert.Equal("grok 4.7", only.Name);
        Assert.Equal("grok-4.7", only.Model);
        Assert.Equal("https://api.x.ai/v1", only.BaseUrl);
        Assert.True(only.HasApiKey);
        Assert.Contains("api.x.ai", only.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Store_SaveThenLoad_RoundTripsValues()
    {
        var store = new ConfigPresetStore(_root);

        store.Save("p1", new Dictionary<string, string?>
        {
            ["FATFISH_MODEL"] = "grok-4.6",
            ["NET_BACKENDS"] = "tavily", // 预设允许携带「和换模型无关」的自定义键
        });

        var loaded = store.Load("p1");

        Assert.NotNull(loaded);
        Assert.Equal("grok-4.6", loaded!["FATFISH_MODEL"]);
        Assert.Equal("tavily", loaded["NET_BACKENDS"]);
    }

    [Fact]
    public void Store_LoadDropsActivePresetKey()
    {
        // 预设文件里如果有人手写了 FATFISH_PRESET，不能让它自己把自己再指到别处
        var dir = Path.Combine(_root, "config", "presets");
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "selfref.env"),
            "FATFISH_MODEL=grok-4.7\nFATFISH_PRESET=other\n");

        var loaded = new ConfigPresetStore(_root).Load("selfref");

        Assert.NotNull(loaded);
        Assert.False(loaded!.ContainsKey(ConfigPresetStore.ActiveKey));
        Assert.Equal("grok-4.7", loaded["FATFISH_MODEL"]);
    }

    [Fact]
    public void Store_LoadMissing_ReturnsNull()
    {
        // 返回 null 而不是空字典：调用方要能区分「名字写错了」和「预设是空的」
        Assert.Null(new ConfigPresetStore(_root).Load("does-not-exist"));
    }

    [Theory]
    [InlineData("..\\..\\evil")]
    [InlineData("../../evil")]
    [InlineData("a/b")]
    public void Store_Sanitize_NeverEscapesPresetDirectory(string name)
    {
        var store = new ConfigPresetStore(_root);
        var path = Path.GetFullPath(store.PathFor(name));

        Assert.StartsWith(Path.GetFullPath(store.Directory), path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, Path.GetFileName(path));
    }

    [Fact]
    public void Store_Delete_MovesFileIntoBackup()
    {
        var store = new ConfigPresetStore(_root);

        store.Save("throwaway", new Dictionary<string, string?> { ["FATFISH_MODEL"] = "x" });

        Assert.True(store.Delete("throwaway"));
        Assert.False(store.Exists("throwaway"));

        var backups = Directory.GetFiles(Path.Combine(_root, "_backup"), "throwaway.env.*.bak");
        Assert.Single(backups);

        Assert.False(store.Delete("throwaway")); // 删第二次要老实返回 false
    }

    [Fact]
    public void Store_Capture_TakesOnlyConnectionKeysAndSkipsEmpties()
    {
        var captured = ConfigPresetStore.Capture(new Dictionary<string, string>
        {
            ["FATFISH_MODEL"] = "grok-4.7",
            ["FATFISH_BASE_URL"] = "  ",       // 空白 = 没配，不该写进预设
            ["VERIFIER_MODEL"] = "deepseek-flash",
            ["UI_THEME"] = "dark",             // 与「换模型」无关，不抓
        });

        Assert.Equal("grok-4.7", captured["FATFISH_MODEL"]);
        Assert.Equal("deepseek-flash", captured["VERIFIER_MODEL"]);
        Assert.False(captured.ContainsKey("FATFISH_BASE_URL"));
        Assert.False(captured.ContainsKey("UI_THEME"));
    }

    // ------------------------------------------------------------------
    // 预设激活：写 .env
    // ------------------------------------------------------------------

    [Fact]
    public void SetActivePreset_WritesThenRemovesTheKey()
    {
        var envPath = Path.Combine(_root, ".env");
        File.WriteAllText(envPath, "FATFISH_MODEL=deepseek-flash\n");
        Environment.SetEnvironmentVariable("FATFISH_ENV", envPath);

        EnvRuntime.SetActivePreset(_root, "grok-4.7 (api.x.ai)", out _);

        Assert.Equal("grok-4.7 (api.x.ai)", EnvRuntime.ActivePresetName(_root));

        // 写进去的是「预设名」，不是乱码，且没有破坏原有键
        var doc = EnvDocument.Load(envPath);
        Assert.Equal("grok-4.7 (api.x.ai)", doc.Get(ConfigPresetStore.ActiveKey));
        Assert.Equal("deepseek-flash", doc.Get("FATFISH_MODEL"));

        EnvRuntime.SetActivePreset(_root, null, out _);

        Assert.Null(EnvRuntime.ActivePresetName(_root));
        Assert.Null(EnvDocument.Load(envPath).Get(ConfigPresetStore.ActiveKey));
    }

    // ------------------------------------------------------------------
    // 热重载
    // ------------------------------------------------------------------

    [Fact]
    public void Reload_OverridesExistingEnvironmentVariable()
    {
        // 这条是整个功能的地基：启动时 Load 是「已存在不覆盖」，
        // 若热重载也走那条路，用户会看到「重载成功」而值一个都没变。
        const string key = "FATFISH_TEST_OVERRIDE";

        var envPath = Path.Combine(_root, ".env");
        File.WriteAllText(envPath, key + "=from-file\n");
        Environment.SetEnvironmentVariable("FATFISH_ENV", envPath);

        Environment.SetEnvironmentVariable(key, "from-process");

        var result = EnvRuntime.Reload(_root);

        Assert.Equal("from-file", Environment.GetEnvironmentVariable(key));
        Assert.Contains(key, result.Changed);
        Assert.Equal(envPath, result.EnvPath);
    }

    [Fact]
    public void Reload_ClearsKeyThatDisappearedFromFile()
    {
        const string key = "FATFISH_TEST_STALE";

        var envPath = Path.Combine(_root, ".env");
        File.WriteAllText(envPath, key + "=x\n");
        Environment.SetEnvironmentVariable("FATFISH_ENV", envPath);

        EnvRuntime.Reload(_root);
        Assert.Equal("x", Environment.GetEnvironmentVariable(key));

        // 用户把这一行删了 —— 语义应是「回到程序默认」，不是「继续用旧值」
        File.WriteAllText(envPath, "# 这一行被删掉了\n");

        var second = EnvRuntime.Reload(_root);

        Assert.Null(Environment.GetEnvironmentVariable(key));
        Assert.Contains(key, second.Removed);
    }

    [Fact]
    public void Reload_AppliesPresetOverEnvFile()
    {
        const string key = "FATFISH_TEST_PRESET_ONLY";

        var envPath = Path.Combine(_root, ".env");
        File.WriteAllText(envPath, key + "=from-env\n");
        Environment.SetEnvironmentVariable("FATFISH_ENV", envPath);

        var store = new ConfigPresetStore(_root);
        store.Save("p", new Dictionary<string, string?> { [key] = "from-preset" });

        EnvRuntime.SetActivePreset(_root, "p", out _);

        var result = EnvRuntime.Reload(_root);

        Assert.Equal("p", result.PresetName);
        Assert.Equal("from-preset", Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void Reload_MissingPreset_WarnsButKeepsGoing()
    {
        var envPath = Path.Combine(_root, ".env");
        File.WriteAllText(envPath, "FATFISH_MODEL=deepseek-flash\nFATFISH_PRESET=nope\n");
        Environment.SetEnvironmentVariable("FATFISH_ENV", envPath);

        var result = EnvRuntime.Reload(_root);

        Assert.Equal("nope", result.PresetName);
        Assert.Contains(result.Warnings, w => w.Contains("不存在", StringComparison.Ordinal));
        Assert.Equal("deepseek-flash", Environment.GetEnvironmentVariable("FATFISH_MODEL"));
    }

    [Fact]
    public void Compose_DoesNotTouchProcessEnvironment()
    {
        // Compose 是「只读一遍会得到什么」，不能有副作用 —— 否则「预览」和「应用」就没区别了
        const string key = "FATFISH_TEST_OVERRIDE";

        var envPath = Path.Combine(_root, ".env");
        File.WriteAllText(envPath, key + "=composed\n");
        Environment.SetEnvironmentVariable("FATFISH_ENV", envPath);
        Environment.SetEnvironmentVariable(key, "untouched");

        var (values, _, _, _) = EnvRuntime.Compose(_root);

        Assert.Equal("composed", values[key]);
        Assert.Equal("untouched", Environment.GetEnvironmentVariable(key));
    }

    // ------------------------------------------------------------------
    // 启动装载（2026-11-17）
    // ------------------------------------------------------------------

    /// <summary>
    /// 钉住用户的真实抱怨：「每次启动都要手动切预设」。
    /// 启动路径原先是 <see cref="EnvFile.LoadNearest"/> —— 只读 .env、<b>不看 FATFISH_PRESET</b>，
    /// 于是 .env 的裸值（或上次 Relaunch 继承下来的旧值）成了实际生效的配置，
    /// 预设只在手动点菜单（热重载）时才生效一次。
    /// </summary>
    [Fact]
    public void ApplyStartup_AppliesActivePresetWithoutManualSwitch()
    {
        const string key = "FATFISH_MODEL";

        var envPath = Path.Combine(_root, ".env");
        File.WriteAllText(envPath, key + "=from-env\n" + ConfigPresetStore.ActiveKey + "=work\n");
        Environment.SetEnvironmentVariable("FATFISH_ENV", envPath);

        new ConfigPresetStore(_root).Save("work", new Dictionary<string, string?>
        {
            [key] = "from-preset",
        });

        // 上一次进程留下的值（Relaunch 会继承）：不能被「已存在所以不覆盖」挡住
        Environment.SetEnvironmentVariable(key, "stale-from-parent");

        var result = EnvRuntime.ApplyStartup(_root);

        Assert.Equal("work", result.PresetName);
        Assert.Equal("from-preset", Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void ApplyStartup_WithoutPreset_UsesEnvFileAndWarnsNothing()
    {
        var envPath = Path.Combine(_root, ".env");
        File.WriteAllText(envPath, "FATFISH_MODEL=plain\n");
        Environment.SetEnvironmentVariable("FATFISH_ENV", envPath);

        var result = EnvRuntime.ApplyStartup(_root);

        Assert.Null(result.PresetName);
        Assert.Equal("plain", Environment.GetEnvironmentVariable("FATFISH_MODEL"));
        Assert.Empty(result.Warnings);
    }
}
