using System.Text.Json;
using FatFish.Core.Plugins;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 插件「模型通道」的解析测试：key 自带接口地址、选择值带 key 序号。
/// </summary>
/// <remarks>
/// <b>这些测试锁的是什么：</b>
/// 同一句 <c>grok-imagine-image-2.0</c> 在中转和 xAI 官方都存在，但两家的
/// 地址、参数名、计价方式完全不同。整条链路上有三处「悄悄错位」的机会：
/// <list type="number">
/// <item>解析 key 库时把 <c>key@https://地址</c> 拆错 —— 拆错后地址进了 key，
/// Authorization 头就带着 URL 发出去，服务端只回 401，看起来像 key 过期。</item>
/// <item>拆分选择值时把模型名当成了带序号的 —— 模型名里合法的字符被吃掉，
/// 上游回 400「模型不存在」，而真因是本地改写。</item>
/// <item>注入时只换钥匙不换地址 —— 拿官方的 key 打中转的地址，回 404。</item>
/// </list>
/// 三者都不会崩、不会报错，只会得到一张看不懂的错误响应。
/// </remarks>
public class PluginChannelTests
{
    // ---------------------------------------------------------------- key@地址

    [Fact]
    public void Parse_KeyWithBase_SplitsIntoKeyAndBase()
    {
        var set = PluginKeySet.Parse("sk-aaa@https://onliqiu.com/v1, xai-bbb@https://api.x.ai/v1/", "TEST");

        Assert.Equal(2, set.Keys.Count);

        Assert.Equal("sk-aaa", set.Keys[0].Value);
        Assert.Equal("https://onliqiu.com/v1", set.Keys[0].Base);

        // 结尾的斜杠要吃掉，否则拼 URL 时会出现 //images/generations
        Assert.Equal("xai-bbb", set.Keys[1].Value);
        Assert.Equal("https://api.x.ai/v1", set.Keys[1].Base);
    }

    [Fact]
    public void Parse_KeyWithoutBase_HasNullBase()
    {
        var set = PluginKeySet.Parse("sk-aaa,sk-bbb", "TEST");

        Assert.All(set.Keys, k => Assert.Null(k.Base));
        Assert.Equal(2, set.Keys.Count);
    }

    [Fact]
    public void Parse_AtSignThatIsNotAUrl_IsKeptInsideKey()
    {
        // 只有右边看起来是 http(s) 才当分隔符：key 值绝不能被悄悄改短
        var set = PluginKeySet.Parse("weird@key_value", "TEST");

        Assert.Single(set.Keys);
        Assert.Equal("weird@key_value", set.Keys[0].Value);
        Assert.Null(set.Keys[0].Base);
    }

    [Fact]
    public void Parse_Mask_DoesNotLeakBaseOrWholeKey()
    {
        var set = PluginKeySet.Parse("xai-1234567890abcdef@https://api.x.ai/v1", "TEST");
        var key = set.Keys[0];

        Assert.DoesNotContain("api.x.ai", key.Mask);
        Assert.DoesNotContain("1234567890abcdef", key.Mask);
        Assert.StartsWith("xai-12", key.Mask);
    }

    // ---------------------------------------------------------------- 选择值

    [Fact]
    public void SplitSelection_WithIndex_ReturnsModelAndIndex()
    {
        var (model, index) = PluginModelSlots.SplitSelection("grok-imagine-image-2.0@3");

        Assert.Equal("grok-imagine-image-2.0", model);
        Assert.Equal(3, index);
    }

    [Fact]
    public void SplitSelection_WithoutIndex_KeepsWholeModel()
    {
        var (model, index) = PluginModelSlots.SplitSelection("gpt-image-2 default");

        Assert.Equal("gpt-image-2 default", model);
        Assert.Null(index);
    }

    [Theory]
    [InlineData("model@abc")]      // 右边不是数字 → 整个当模型名
    [InlineData("model@0")]        // 序号从 1 开始
    [InlineData("model@")]         // 空尾巴
    [InlineData("@3")]             // 没模型名
    public void SplitSelection_DoesNotInventIndex(string input)
    {
        var (model, index) = PluginModelSlots.SplitSelection(input);

        Assert.Null(index);
        Assert.False(string.IsNullOrWhiteSpace(model));
    }

    [Fact]
    public void JoinSelection_RoundTripsThroughSplit()
    {
        var joined = PluginModelSlots.JoinSelection("grok-imagine-image-2.0", 2);
        var (model, index) = PluginModelSlots.SplitSelection(joined);

        Assert.Equal("grok-imagine-image-2.0", model);
        Assert.Equal(2, index);
    }

    // ---------------------------------------------------------------- 注入

    private static PluginSettingsSpec Spec(string slotId = "generate") => new()
    {
        ConfigKeys = ["LIQIU_API_KEY", "LIQIU_BASE_URL", "LIQIU_IMAGE_MODEL"],
        KeysEnv = "LIQIU_API_KEYS",
        KeyEnv = "LIQIU_API_KEY",
        BaseEnv = "LIQIU_BASE_URL",
        ModelSlots =
        [
            new PluginModelSlot
            {
                Id = slotId,
                Env = "LIQIU_IMAGE_MODEL",
                Tools = ["image_generate"],
            },
        ],
    };

    private static PluginKeySet Keys(string raw)
        => PluginKeySet.Parse(raw, "TEST");

    [Fact]
    public void BuildEnvOverrides_ExplicitChannel_SwitchesKeyAndBase()
    {
        var spec = Spec();

        var settings = new PluginSettings
        {
            SelectedModels = new Dictionary<string, string> { ["generate"] = "grok-imagine-image-2.0@3" },
        };

        var keys = Keys("sk-relay@https://onliqiu.com/v1, sk-two@https://onliqiu.com/v1, xai-official@https://api.x.ai/v1");

        var map = PluginModelSlots.BuildEnvOverrides(spec, settings, "image_generate", keys);

        // 交给脚本的模型名必须是干净的，不能带 @3
        Assert.Equal("grok-imagine-image-2.0", map["LIQIU_IMAGE_MODEL"]);

        // ★ 关键：钥匙和地址必须一起换。只换钥匙 = 拿官方 key 打中转地址 → 404
        Assert.Equal("xai-official", map["LIQIU_API_KEY"]);
        Assert.Equal("https://api.x.ai/v1", map["LIQIU_BASE_URL"]);
    }

    [Fact]
    public void BuildEnvOverrides_PlainModel_UsesLegacyKeyMap()
    {
        var spec = Spec();

        var settings = new PluginSettings
        {
            SelectedModels = new Dictionary<string, string> { ["generate"] = "gpt-image-2" },
            ModelKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["gpt-image-2"] = 1 },
        };

        var keys = Keys("sk-first@https://onliqiu.com/v1, sk-second@https://onliqiu.com/v1");

        var map = PluginModelSlots.BuildEnvOverrides(spec, settings, "image_generate", keys);

        Assert.Equal("gpt-image-2", map["LIQIU_IMAGE_MODEL"]);
        Assert.Equal("sk-first", map["LIQIU_API_KEY"]);
        Assert.Equal("https://onliqiu.com/v1", map["LIQIU_BASE_URL"]);
    }

    [Fact]
    public void BuildEnvOverrides_KeyWithoutBase_DoesNotOverrideBaseEnv()
    {
        var spec = Spec();

        var settings = new PluginSettings
        {
            SelectedModels = new Dictionary<string, string> { ["generate"] = "gpt-image-2@2" },
        };

        // 第二把 key 没有自带地址 → 不该覆盖 baseEnv（让 .env 的原值生效）
        var keys = Keys("sk-one, sk-two");

        var map = PluginModelSlots.BuildEnvOverrides(spec, settings, "image_generate", keys);

        Assert.Equal("sk-two", map["LIQIU_API_KEY"]);
        Assert.False(map.ContainsKey("LIQIU_BASE_URL"));
    }

    [Fact]
    public void BuildEnvOverrides_NoSelection_OverridesNothing()
    {
        var spec = Spec();
        var map = PluginModelSlots.BuildEnvOverrides(spec, new PluginSettings(), "image_generate", Keys("sk-one"));

        Assert.Empty(map);
    }

    [Fact]
    public void BuildEnvOverrides_UnknownTool_DoesNotMatchGenerateSlot()
    {
        var spec = Spec();

        var settings = new PluginSettings
        {
            SelectedModels = new Dictionary<string, string> { ["generate"] = "gpt-image-2" },
        };

        // image_edit 不在 generate 槽位的 tools 里，也没有兜底槽位 → 不覆盖
        var map = PluginModelSlots.BuildEnvOverrides(spec, settings, "image_edit", Keys("sk-one"));

        Assert.Empty(map);
    }

    // ---------------------------------------------------------------- 清单往返

    [Fact]
    public void ModelOption_SerializesWithoutComputedHost()
    {
        var option = new PluginModelOption
        {
            Model = "grok-imagine-image-2.0",
            KeyIndex = 3,
            Base = "https://api.x.ai/v1",
        };

        var json = JsonSerializer.Serialize(option);

        // Host 是给人看的计算属性；写进设置文件只是噪声，且将来会变成"改不动"的旧值
        Assert.DoesNotContain("host", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api.x.ai", json);
    }

    [Fact]
    public void ModelOption_ToString_DistinguishesChannels()
    {
        var relay = new PluginModelOption { Model = "m", KeyIndex = 2, Base = "https://onliqiu.com/v1" };
        var official = new PluginModelOption { Model = "m", KeyIndex = 3, Base = "https://api.x.ai/v1" };

        // 两个同名模型在下拉里必须是两个不同的字符串，否则用户没法选
        Assert.NotEqual(relay.ToString(), official.ToString());
        Assert.Contains("api.x.ai", official.ToString());
    }
}
