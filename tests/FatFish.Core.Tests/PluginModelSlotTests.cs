using System.Text;
using FatFish.Core.Plugins;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 模型槽位（<c>settings.modelSlots</c>）的解析与注入规则。
/// </summary>
/// <remarks>
/// <b>为什么这些测试值得写：</b>
/// 槽位出错的方式全都是"静默"的 —— 写错工具名，那个槽位永远不生效；
/// 写错 env 键，选的模型被写进一个没人读的变量；拿错钥匙，上游回一个
/// 看起来像"模型名写错"的 400。这三种都不会在装载时报错，
/// 也都不会在界面上显形，只能靠这里钉住。
/// </remarks>
public sealed class PluginModelSlotTests
{
    // ---------------------------------------------------------------- 清单解析

    private static string WriteManifest(string dir, string json)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "plugin.json");
        File.WriteAllText(path, json, Encoding.UTF8);
        return path;
    }

    /// <summary>两个工具 + 两个槽位的清单。</summary>
    private static string TwoToolManifest(string id, string settingsJson)
        => $$"""
           {
             "id": "{{id}}",
             "name": "演示生图插件",
             "version": "1.0.0",
             "description": "测试用",
             "settings": {{settingsJson}},
             "tools": [
               {
                 "name": "demo_draw",
                 "description": "文生图",
                 "parameters": { "type": "object", "properties": {} },
                 "run": { "type": "http", "url": "https://api.example.com/v1/a", "hosts": ["api.example.com"] }
               },
               {
                 "name": "demo_edit",
                 "description": "改图",
                 "parameters": { "type": "object", "properties": {} },
                 "run": { "type": "http", "url": "https://api.example.com/v1/b", "hosts": ["api.example.com"] }
               }
             ]
           }
           """;

    private static PluginParseOutcome Parse(string id, string settingsJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ff-slots-" + Guid.NewGuid().ToString("n"), id);

        try
        {
            var path = WriteManifest(dir, TwoToolManifest(id, settingsJson));
            return PluginManifestParser.ParseFile(path);
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(dir)!);
        }
    }

    [Fact]
    public void Slots_AreParsedWithLabelEnvAndTools()
    {
        var outcome = Parse("demo-slots", """
            {
              "configKeys": ["DEMO_API_KEY", "DEMO_BASE", "DEMO_GEN_MODEL", "DEMO_EDIT_MODEL"],
              "keyEnv": "DEMO_API_KEY",
              "baseEnv": "DEMO_BASE",
              "modelSlots": [
                { "id": "generate", "label": "文生图", "env": "DEMO_GEN_MODEL", "tools": ["demo_draw"] },
                { "id": "edit", "env": "DEMO_EDIT_MODEL", "tools": ["demo_edit"] }
              ],
              "defaultBase": "https://api.example.com/v1"
            }
            """);

        Assert.True(outcome.Ok);

        var slots = outcome.Manifest!.Settings!.ModelSlots;

        Assert.Equal(2, slots.Count);
        Assert.Equal("generate", slots[0].Id);
        Assert.Equal("文生图", slots[0].Label);
        Assert.Equal("DEMO_GEN_MODEL", slots[0].Env);
        Assert.Equal(["demo_draw"], slots[0].Tools);

        // 没写 label 时用 id 当显示名
        Assert.Equal("edit", slots[1].Display);
        Assert.Equal("DEMO_EDIT_MODEL", slots[1].Env);
    }

    [Fact]
    public void Slots_BadId_DropsSettingsBlockButKeepsTools()
    {
        // 大写开头的 id 不合法。约定：设置块丢弃、留 Error，但工具照常装载 ——
        // 一个设置块的笔误不该让整组画图工具从模型眼里消失。
        var outcome = Parse("demo-bad-id", """
            { "modelSlots": [ { "id": "Generate", "env": "DEMO_GEN_MODEL" } ] }
            """);

        Assert.True(outcome.Ok);
        Assert.Null(outcome.Manifest!.Settings);
        Assert.Equal(2, outcome.Manifest!.Tools.Count);
        Assert.Contains(outcome.Diagnostics, d => d.Level == PluginDiagnosticLevel.Error);
    }

    [Fact]
    public void Slots_SameEnvTwice_IsRejected()
    {
        // 两个槽位写同一个 env：后写入的会覆盖前一个，选择等于失效。
        var outcome = Parse("demo-dup-env", """
            {
              "modelSlots": [
                { "id": "generate", "env": "DEMO_MODEL" },
                { "id": "edit", "env": "DEMO_MODEL" }
              ]
            }
            """);

        Assert.Null(outcome.Manifest!.Settings);
        Assert.Contains(outcome.Diagnostics, d => d.Level == PluginDiagnosticLevel.Error);
    }

    [Fact]
    public void Slots_MissingEnv_IsRejected()
    {
        var outcome = Parse("demo-no-env", """
            { "modelSlots": [ { "id": "edit", "label": "改图" } ] }
            """);

        Assert.Null(outcome.Manifest!.Settings);
        Assert.Contains(outcome.Diagnostics, d => d.Level == PluginDiagnosticLevel.Error);
    }

    [Fact]
    public void Slots_ModelEnvNotUsedByAnySlot_Warns()
    {
        // modelEnv 与 modelSlots 并存时以 modelSlots 为准；这条警告就是为了
        // 让"我明明改了 modelEnv 却没生效"有个可查的线索。
        var outcome = Parse("demo-legacy-env", """
            {
              "modelEnv": "DEMO_OLD_MODEL",
              "modelSlots": [ { "id": "generate", "env": "DEMO_GEN_MODEL" } ]
            }
            """);

        Assert.NotNull(outcome.Manifest!.Settings);
        Assert.Contains(outcome.Diagnostics,
            d => d.Level == PluginDiagnosticLevel.Warning && d.Message.Contains("DEMO_OLD_MODEL"));
    }

    // ---------------------------------------------------------------- 生效槽位

    private static PluginSettingsSpec SpecWith(params PluginModelSlot[] slots) => new()
    {
        ConfigKeys = ["DEMO_API_KEY", "DEMO_GEN_MODEL", "DEMO_EDIT_MODEL"],
        KeyEnv = "DEMO_API_KEY",
        ModelEnv = "DEMO_GEN_MODEL",
        ModelSlots = slots,
    };

    [Fact]
    public void NoSlots_SynthesizesDefaultFromModelEnv()
    {
        var spec = new PluginSettingsSpec
        {
            ConfigKeys = ["DEMO_MODEL"],
            ModelEnv = "DEMO_MODEL",
        };

        var slots = PluginModelSlots.Effective(spec);

        Assert.Single(slots);
        Assert.Equal(PluginModelSlots.DefaultSlotId, slots[0].Id);
        Assert.True(slots[0].CoversAll);

        // 兜底槽位：任何工具都必须能命中（Effective 每次会新造实例，只能比 id 不能比引用）
        Assert.Equal(PluginModelSlots.DefaultSlotId, PluginModelSlots.For(spec, "anything")!.Id);
        Assert.Equal(PluginModelSlots.DefaultSlotId, PluginModelSlots.For(spec, null)!.Id);
    }

    [Fact]
    public void For_PicksSlotByToolName()
    {
        var spec = SpecWith(
            new PluginModelSlot { Id = "generate", Env = "DEMO_GEN_MODEL", Tools = ["demo_draw"] },
            new PluginModelSlot { Id = "edit", Env = "DEMO_EDIT_MODEL", Tools = ["demo_edit"] });

        Assert.Equal("generate", PluginModelSlots.For(spec, "demo_draw")!.Id);
        Assert.Equal("edit", PluginModelSlots.For(spec, "demo_edit")!.Id);

        // 没有任何兜底槽位时，不认识的工具就不该被覆盖
        Assert.Null(PluginModelSlots.For(spec, "demo_other"));
    }

    [Fact]
    public void For_FallsBackToCatchAllSlot()
    {
        var spec = SpecWith(
            new PluginModelSlot { Id = "edit", Env = "DEMO_EDIT_MODEL", Tools = ["demo_edit"] },
            new PluginModelSlot { Id = "rest", Env = "DEMO_GEN_MODEL" });

        Assert.Equal("edit", PluginModelSlots.For(spec, "demo_edit")!.Id);
        Assert.Equal("rest", PluginModelSlots.For(spec, "demo_draw")!.Id);
        Assert.Equal("rest", PluginModelSlots.For(spec, "whatever")!.Id);
    }

    // ---------------------------------------------------------------- 注入

    private static readonly PluginKeySet TwoKeys = PluginKeySet.Parse("sk-first,sk-second", "DEMO_API_KEYS");

    [Fact]
    public void Overrides_UsePerSlotModel_AndItsOwnKey()
    {
        // 真实场景：gpt 系模型只由 #1 提供，grok 系只由 #2 提供。
        var spec = SpecWith(
            new PluginModelSlot { Id = "generate", Env = "DEMO_GEN_MODEL", Tools = ["demo_draw"] },
            new PluginModelSlot { Id = "edit", Env = "DEMO_EDIT_MODEL", Tools = ["demo_edit"] });

        var settings = new PluginSettings
        {
            SelectedModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generate"] = "gpt-image-2 default",
                ["edit"] = "grok-imagine-image",
            },
            ModelKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-image-2 default"] = 1,
                ["grok-imagine-image"] = 2,
            },
        };

        var draw = PluginModelSlots.BuildEnvOverrides(spec, settings, "demo_draw", TwoKeys);

        Assert.Equal("gpt-image-2 default", draw["DEMO_GEN_MODEL"]);
        Assert.Equal("sk-first", draw["DEMO_API_KEY"]);
        Assert.False(draw.ContainsKey("DEMO_EDIT_MODEL"));

        var edit = PluginModelSlots.BuildEnvOverrides(spec, settings, "demo_edit", TwoKeys);

        Assert.Equal("grok-imagine-image", edit["DEMO_EDIT_MODEL"]);
        Assert.Equal("sk-second", edit["DEMO_API_KEY"]);

        // ★ 改图绝不能顺手把生图槽位的键也覆盖掉：那会让 /images/generations 也跟着变
        Assert.False(edit.ContainsKey("DEMO_GEN_MODEL"));
    }

    [Fact]
    public void Overrides_LegacySelectedModel_AppliesToFirstSlotOnly()
    {
        // 老设置文件里只有 selectedModel（槽位功能之前存的），不该丢：
        // 当成"第一个槽位的选择"，其余槽位仍沿用 .env。
        var spec = SpecWith(
            new PluginModelSlot { Id = "generate", Env = "DEMO_GEN_MODEL", Tools = ["demo_draw"] },
            new PluginModelSlot { Id = "edit", Env = "DEMO_EDIT_MODEL", Tools = ["demo_edit"] });

        var settings = new PluginSettings { SelectedModel = "flux-1.1-pro" };

        var draw = PluginModelSlots.BuildEnvOverrides(spec, settings, "demo_draw", TwoKeys);

        Assert.Equal("flux-1.1-pro", draw["DEMO_GEN_MODEL"]);

        // 没有 ModelKeys 记录 → 不覆盖 key（沿用 .env 的默认那把）
        Assert.False(draw.ContainsKey("DEMO_API_KEY"));

        Assert.Empty(PluginModelSlots.BuildEnvOverrides(spec, settings, "demo_edit", TwoKeys));
    }

    [Fact]
    public void Overrides_NothingSelected_ReturnsEmpty()
    {
        // "未选择则沿用上次"= 什么都不覆盖，让 .env 里的值原样生效。
        var spec = SpecWith(
            new PluginModelSlot { Id = "generate", Env = "DEMO_GEN_MODEL", Tools = ["demo_draw"] });

        var result = PluginModelSlots.BuildEnvOverrides(spec, PluginSettings.Default, "demo_draw", TwoKeys);

        Assert.Empty(result);
    }

    [Fact]
    public void Overrides_WorksForManifestParsedFromDisk()
    {
        // 端到端：清单 → 解析 → 注入，确认 id / env / tools 一路没被改写。
        var outcome = Parse("demo-e2e", """
            {
              "keyEnv": "DEMO_API_KEY",
              "modelSlots": [
                { "id": "generate", "env": "DEMO_GEN_MODEL", "tools": ["demo_draw"] },
                { "id": "edit", "env": "DEMO_EDIT_MODEL", "tools": ["demo_edit"] }
              ],
              "defaultBase": "https://api.example.com/v1"
            }
            """);

        var spec = outcome.Manifest!.Settings!;

        var settings = new PluginSettings
        {
            SelectedModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["edit"] = "grok-imagine-image-quality",
            },
        };

        var edit = PluginModelSlots.BuildEnvOverrides(spec, settings, "demo_edit", TwoKeys);

        Assert.Equal("grok-imagine-image-quality", edit["DEMO_EDIT_MODEL"]);
        Assert.Empty(PluginModelSlots.BuildEnvOverrides(spec, settings, "demo_draw", TwoKeys));
    }

    // ---------------------------------------------------------------- 工具

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }
}
