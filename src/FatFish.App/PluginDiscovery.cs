using System;
using System.Linq;
using System.Threading.Tasks;
using FatFish.Core.Config;
using FatFish.Core.Net;
using FatFish.Core.Plugins;

namespace FatFish.App;

/// <summary>
/// 启动时的模型发现：对每个声明了 <c>settings</c> 的插件，
/// **逐把 key** 问一次 <c>GET {base}/models</c>，把并集与「模型 → key 序号」
/// 一起记进插件设置文件。
/// </summary>
/// <remarks>
/// <b>为什么逐把 key 而不是"第一把通了就收工"：</b>
/// 2026-10-30 实测同一个中转站的两把 key 可见模型完全不重叠 ——
/// 一把只有 <c>gpt-image-2</c>，另一把只有 <c>grok-imagine-*</c>。
/// 只查第一把，用户就永远选不到另一半模型。
///
/// <b>为什么放后台、失败只写日志：</b>
/// 这件事要联网。启动时网络不通 / key 过期 / 中转站抽风都很常见，
/// 而它们都不该让程序变慢或弹窗。查到就存，查不到就留一句原因，
/// 下次打开设置窗口会显示"上次查询失败：…"。
/// </remarks>
internal static class PluginDiscovery
{
    /// <summary>后台把所有插件的模型列表刷一遍。不会抛异常。</summary>
    public static void StartBackground(PluginHost host, string root, Action<string> log)
    {
        if (host is null || string.IsNullOrWhiteSpace(root))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // 复制一份清单：后台跑的时候插件可能被禁用/重载，
                // 直接遍历会读到被换掉的快照。
                var targets = host.Manifests
                    .Where(m => m.Settings is { CanDiscoverModels: true })
                    .ToList();

                if (targets.Count == 0)
                    return;

                int ok = 0;
                int failed = 0;

                foreach (var manifest in targets)
                {
                    var spec = manifest.Settings!;
                    var baseUrl = EnvFile.Get(spec.BaseEnv ?? string.Empty) ?? spec.DefaultBase;
                    var keys = PluginKeySet.FromEnvironment(spec);

                    if (keys.IsEmpty)
                        continue;

                    var result = await ModelCatalogAll
                        .FetchAsync(keys, baseUrl, spec.ModelsPath)
                        .ConfigureAwait(false);

                    var current = host.SettingsFor(manifest.Id);

                    // 只更新"发现结果"这一类字段：用户选中的模型、启用状态一律不碰
                    var updated = result.Ok
                        ? current with
                        {
                            KnownModels = result.Models,
                            ModelKeys = result.ModelKeys,
                            ModelsFetchedAt = DateTimeOffset.Now,
                            LastDiscoveryError = null,
                        }
                        : current with { LastDiscoveryError = result.Describe() };

                    host.SaveSettings(manifest.Id, updated);

                    if (result.Ok)
                    {
                        ok++;
                        log($"[插件] {manifest.Id}：{result.Describe()}");

                        foreach (var per in result.PerKey)
                        {
                            log(per.Ok
                                ? $"[插件]   #{per.KeyIndex} → {per.Models.Count} 个：{string.Join(", ", per.Models.Take(6))}"
                                : $"[插件]   #{per.KeyIndex} → 失败：{per.Error}");
                        }
                    }
                    else
                    {
                        failed++;
                        log($"[插件] {manifest.Id}：模型查询失败 —— {result.Describe()}");
                    }
                }

                if (ok + failed > 0)
                    log($"[插件] 启动发现完成：{ok} 个成功 / {failed} 个失败");
            }
            catch (Exception ex)
            {
                // 后台任务里的异常没人接，会静默消失；这里至少留一行
                log("[插件] 启动发现出错：" + ex.GetType().Name + " - " + ex.Message);
            }
        });
    }
}
