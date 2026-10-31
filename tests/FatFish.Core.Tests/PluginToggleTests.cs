using System.Text;
using FatFish.Core.Plugins;
using FatFish.Core.Tools;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 插件的启用 / 禁用与热插拔（<see cref="PluginHost.SetEnabled"/>）。
/// </summary>
/// <remarks>
/// 这一组测试守的是一条容易被"优化掉"的规则：**全部禁用 ≠ 装载失败**。
/// 宿主里有"熔断"逻辑（一个工具都没装出来时保留旧快照，防止把清单写到一半的人
/// 整组工具弄没），它和"用户主动关掉所有插件"在结果上完全一样 —— 都是 0 个工具。
/// 若不分清，全部禁用会静默失效：界面上显示关掉了，模型却还能调。
///
/// 另一条同样重要的约定：被禁用的插件**仍然留在 Manifests 里**。
/// 插件栏要靠它把"已禁用"的那些也列出来 —— 否则用户禁用之后
/// 再也点不到"启用"，只能去手改 json。
/// </remarks>
public sealed class PluginToggleTests
{
    private static string TempRoot()
        => Path.Combine(Path.GetTempPath(), "ff-toggle-" + Guid.NewGuid().ToString("n"));

    private static void WritePlugin(string pluginsDir, string id)
    {
        var dir = Path.Combine(pluginsDir, id);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "{{id}} 插件",
              "version": "1.0.0",
              "description": "热插拔测试用",
              "tools": [
                {
                  "name": "{{id}}_tool",
                  "description": "只声明，不调用",
                  "parameters": { "type": "object", "properties": {} },
                  "run": {
                    "type": "http",
                    "url": "https://{{id}}.example.com/v1/ping",
                    "hosts": ["{{id}}.example.com"]
                  }
                }
              ],
              "settings": {
                "configKeys": ["{{id}}_API_KEY"],
                "keyEnv": "{{id}}_API_KEY",
                "baseEnv": "{{id}}_BASE_URL",
                "modelEnv": "{{id}}_MODEL",
                "modelsPath": "/models"
              }
            }
            """, Encoding.UTF8);
    }

    private static PluginHost Start(string root, string pluginsDir)
        => PluginHost.Start(new PluginHostOptions
        {
            Directories = [new PluginDirectory(pluginsDir, false)],
            SettingsRoot = root,
        });

    [Fact]
    public void Disable_RemovesToolsAndRisks_AndEnableBringsThemBack()
    {
        var root = TempRoot();

        try
        {
            var pluginsDir = Path.Combine(root, "plugins");
            WritePlugin(pluginsDir, "alpha");
            WritePlugin(pluginsDir, "beta");

            var host = Start(root, pluginsDir);

            Assert.Equal(2, host.Manifests.Count);
            Assert.Equal(2, host.Snapshot().Count);

            // ---- 禁用 alpha ----
            host.SetEnabled("alpha", false);

            Assert.Single(host.Snapshot());
            Assert.Null(host.Lookup("alpha_tool"));
            Assert.NotNull(host.Lookup("beta_tool"));
            Assert.False(host.SettingsFor("alpha").Enabled);

            // 仍然列在 Manifests 里（插件栏要能显示"已禁用"并可再启用）
            Assert.Equal(2, host.Manifests.Count);
            Assert.NotNull(host.FindPlugin("alpha"));

            // ---- 再启用回来 ----
            host.SetEnabled("alpha", true);

            Assert.Equal(2, host.Snapshot().Count);
            Assert.NotNull(host.Lookup("alpha_tool"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void DisableEverything_IsHonored_NotRevertedByTheCircuitBreaker()
    {
        var root = TempRoot();

        try
        {
            var pluginsDir = Path.Combine(root, "plugins");
            WritePlugin(pluginsDir, "alpha");
            WritePlugin(pluginsDir, "beta");

            var host = Start(root, pluginsDir);
            Assert.Equal(2, host.Snapshot().Count);

            host.SetEnabled("alpha", false);
            var report = host.SetEnabled("beta", false);

            // 关键断言：确实是 0 个工具，而不是"熔断保住了旧快照"
            Assert.Empty(host.Snapshot());
            Assert.Empty(host.Risks);

            // 重载被判定为成功（不是"重载失败，继续用上一次快照"）
            Assert.True(report.Reloaded);
            Assert.Contains("禁用", report.Message);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void EnabledState_PersistsAcrossHostRestart()
    {
        var root = TempRoot();

        try
        {
            var pluginsDir = Path.Combine(root, "plugins");
            WritePlugin(pluginsDir, "alpha");

            var first = Start(root, pluginsDir);
            first.SetEnabled("alpha", false);

            // 新起一个宿主（相当于重启程序）：禁用必须还在
            var second = Start(root, pluginsDir);

            Assert.Empty(second.Snapshot());
            Assert.False(second.SettingsFor("alpha").Enabled);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void BindSettingsRoot_TurnsSettingsOnAfterConstruction()
    {
        var root = TempRoot();

        try
        {
            var pluginsDir = Path.Combine(root, "plugins");
            WritePlugin(pluginsDir, "alpha");

            // 模拟 App 启动早期：根还不知道
            var host = PluginHost.Start(new PluginHostOptions
            {
                Directories = [new PluginDirectory(pluginsDir, false)],
            });

            Assert.False(host.SupportsSettings);
            Assert.Throws<InvalidOperationException>(() => host.SetEnabled("alpha", false));

            // 根确定之后补绑定
            host.BindSettingsRoot(root);

            Assert.True(host.SupportsSettings);
            host.SetEnabled("alpha", false);
            Assert.Empty(host.Snapshot());
        }
        finally
        {
            TryDelete(root);
        }
    }

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
