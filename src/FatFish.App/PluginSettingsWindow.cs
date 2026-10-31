using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FatFish.Core.Config;
using FatFish.Core.Net;
using FatFish.Core.Plugins;

namespace FatFish.App;

/// <summary>
/// 单个插件的设置窗口：启用开关 / key 库 / 模型选择。
///
/// <b>为什么用代码搭界面、而不是再来一个 XAML：</b>
/// 这个窗口只有四组控件，而 XAML 版本必须再走一遍主题资源
/// （ThemeManager 那 46 个键 + 换肤时重新解析 DynamicResource）。
/// 本工程里顶栏、审批栏、插件栏都是代码搭的，这里保持一致；
/// 颜色一律从主窗口的当前主题里取，取不到就退回系统色。
///
/// <b>密钥的规矩：</b>窗口里**只显示掩码**（前 6 后 4），
/// 想改 key 请点「编辑 .env」—— 那里有整套遮挡显示与引号规则。
/// 设置文件里永远不出现 key 本身（见 <see cref="PluginSettings"/> 的注释）。
///
/// <b>模型为什么是一个槽位一个下拉框：</b>
/// 同一个插件的不同业务可能要不同的模型（生图插件：文生图一个、改图一个）。
/// 槽位由清单声明（<see cref="PluginSettingsSpec.ModelSlots"/>），
/// 这份界面只负责「一个槽位摆一行」，不写死任何业务名。
///
/// <b>为什么下拉项不只写模型名：</b>
/// 同名模型可能由不同的 key（甚至不同的接口地址）提供 —— 中转一把、官方一把。
/// 两家的价格 / 速度 / 接口方言都不同，所以每一项都带上「key #n · 主机名」，
/// 用户选的是**通道**，不只是名字。
/// </summary>
internal sealed class PluginSettingsWindow : Window
{
    private readonly PluginHost _host;
    private readonly PluginManifest _manifest;
    private readonly string _root;
    private readonly Action<string> _log;

    private readonly CheckBox _enabled = new() { Content = "启用这个插件（禁用后它的工具不再注册给模型）" };
    private readonly ListBox _keys = new() { Height = 96 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _keyHint = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };

    /// <summary>槽位 id → 该槽位的模型下拉框。</summary>
    private readonly Dictionary<string, ComboBox> _slotBoxes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>下拉框里的显示文本 → 它代表的那条通道。</summary>
    /// <remarks>
    /// 用字典而不是"从字符串里解析出模型名"，是因为显示文本里带着
    /// <c>（key #2 · api.x.ai）</c> 这种给人看的装饰。反向解析一旦格式变动就会
    /// 悄悄错位（把装饰当成模型名的一部分发出去，上游只会回一个像"模型不存在"的 400）。
    /// 字典是单向的：显示什么、代表什么，保存时一查就知道。
    /// </remarks>
    private readonly Dictionary<string, PluginModelOption> _optionIndex = new(StringComparer.Ordinal);

    private PluginSettings _settings;

    public PluginSettingsWindow(PluginHost host, PluginManifest manifest, string root, Window owner, Action<string> log)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _root = root ?? string.Empty;
        _log = log ?? (_ => { });
        _settings = host.SettingsFor(manifest.Id);

        Title = "插件设置 · " + manifest.Name;
        Width = 720;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;
        Background = Look(owner, "BgPrimary") ?? SystemColors.WindowBrush;
        Foreground = Look(owner, "FgPrimary") ?? SystemColors.WindowTextBrush;

        Content = BuildLayout();

        Loaded += (_, _) =>
        {
            RefreshKeys();
            RefreshModels(knownOnly: true);

            // 启动时自动查一次（离线/没配 key 时静默失败，只更新状态栏文字）
            if (_manifest.Settings is { CanDiscoverModels: true })
                _ = DiscoverAsync(announce: false);
        };
    }

    // ---------------------------------------------------------------- 界面

    private ScrollViewer BuildLayout()
    {
        var stack = new StackPanel { Margin = new Thickness(18) };

        stack.Children.Add(new TextBlock
        {
            Text = _manifest.Name,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"{_manifest.Id} · v{_manifest.Version} · {_manifest.Tools.Count} 个工具",
            Opacity = 0.65,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ---- 启用 ----
        stack.Children.Add(Section("状态"));
        _enabled.IsChecked = _settings.Enabled;
        _enabled.Margin = new Thickness(0, 0, 0, 6);
        stack.Children.Add(_enabled);

        stack.Children.Add(new TextBlock
        {
            Text = "禁用后立刻生效（热插拔）：模型下一刻就看不到这些工具，不需要重启。",
            FontSize = 11,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = TextWrapping.Wrap,
        });

        // ---- 模型（按槽位）----
        var spec = _manifest.Settings;
        var slots = PluginModelSlots.Effective(spec);

        if (spec is { CanDiscoverModels: true } && slots.Count > 0)
        {
            stack.Children.Add(Section(slots.Count > 1 ? "模型（按用途分开选）" : "模型"));

            foreach (var slot in slots)
            {
                var box = new ComboBox { IsEditable = true, MinWidth = 360 };
                _slotBoxes[slot.Id] = box;

                stack.Children.Add(new TextBlock
                {
                    Text = slot.CoversAll
                        ? $"{slot.Display}\u3000写回 {slot.Env}（兜底：没被别的槽位认领的工具都用它）"
                        : $"{slot.Display}\u3000写回 {slot.Env}\u3000适用：{string.Join(" / ", slot.Tools)}",
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                });

                stack.Children.Add(box);

                stack.Children.Add(new TextBlock
                {
                    Text = $"留空＝沿用 .env 里 {slot.Env} 的现有值（也就是上次的选择）。",
                    FontSize = 11,
                    Opacity = 0.6,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };

            var refresh = new Button { Content = "⟳ 查询可用模型", Padding = new Thickness(10, 3, 10, 3) };
            refresh.Click += async (_, _) => await DiscoverAsync(announce: true).ConfigureAwait(true);
            row.Children.Add(refresh);

            stack.Children.Add(row);

            stack.Children.Add(new TextBlock
            {
                // 实测（2026-10-30）：/models 只回 id / object / owned_by / display_name，
                // 没有任何"这个模型能干什么"的字段 —— 所以宿主没法自动判断谁能走 /images/edits。
                Text = "同名模型若有多把 key（或不同接口地址）提供，会**分别列出**，选哪条就走哪条：" +
                       "key 决定用哪把钥匙，主机名决定打到哪个地址、用哪套参数方言。" +
                       "「key #n」后面的主机名来自该 key 自带的地址（.env 里写成 key@https://地址）；" +
                       "没有自带地址的 key 沿用插件级地址，列表里只显示序号。" +
                       "接口不区分「生成用」和「改图用」的模型，各槽位的候选是同一份列表；" +
                       "哪个模型真能走 /images/edits 只能实测（失败时接口会回 400 并说明原因）。",
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(0, 8, 0, 14),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // ---- key 库 ----
        stack.Children.Add(Section("Key 库"));

        stack.Children.Add(_keys);

        _keyHint.Margin = new Thickness(0, 6, 0, 8);
        stack.Children.Add(_keyHint);

        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };

        var editEnv = new Button { Content = "编辑 .env…", Padding = new Thickness(10, 3, 10, 3) };
        editEnv.Click += (_, _) => OpenEnvFile();
        keyRow.Children.Add(editEnv);

        var reloadKeys = new Button { Content = "重新读取", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        reloadKeys.Click += (_, _) =>
        {
            Core.Config.EnvFile.LoadNearest(_root);
            RefreshKeys();
        };
        keyRow.Children.Add(reloadKeys);

        stack.Children.Add(keyRow);

        // ---- 底部 ----
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var save = new Button { Content = "保存", Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        save.Click += (_, _) => Save();
        buttons.Children.Add(save);

        var close = new Button { Content = "关闭", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);

        stack.Children.Add(_status);
        stack.Children.Add(buttons);

        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 6),
    };

    // ---------------------------------------------------------------- 行为

    private void RefreshKeys()
    {
        _keys.Items.Clear();

        var spec = _manifest.Settings;
        var set = PluginKeySet.FromEnvironment(spec);

        if (set.IsEmpty)
        {
            _keys.Items.Add("（这个插件还没有配置 key）");
            _keyHint.Text = spec?.KeysEnv is { } multi
                ? $"在 .env 里填 {spec.KeyEnv ?? multi} 或 {multi}。多个 key 用英文逗号分隔，**第一个是默认**。" +
                  "某把 key 若要用自己的接口地址，写成 key@https://地址。"
                : "在 .env 里配置这个插件的 key。";
            return;
        }

        foreach (var key in set.Keys)
        {
            _keys.Items.Add(key.Base is null
                ? $"{key.Index}. {key.Mask}\u3000来自 {key.Source}"
                : $"{key.Index}. {key.Mask}\u3000来自 {key.Source}\u3000地址 {key.Base}");
        }

        _keyHint.Text = "只显示掩码（前 6 后 4）。要改 key 请点「编辑 .env…」；" +
                        $"多个 key 写在 {spec?.KeysEnv ?? "(未声明 keysEnv)"} 里，用英文逗号分隔。" +
                        "带 @地址 的 key 会连地址一起用（用于同时接中转与官方）。";
    }

    private void RefreshModels(bool knownOnly)
    {
        var spec = _manifest.Settings;
        var slots = PluginModelSlots.Effective(spec);

        _optionIndex.Clear();

        // 优先用「通道」列表（模型 × key × 地址）；老设置文件没有它时退回纯模型名。
        foreach (var option in _settings.ModelOptions)
            _optionIndex[option.ToString()] = option;

        foreach (var (slotId, box) in _slotBoxes)
        {
            box.Items.Clear();

            if (_optionIndex.Count > 0)
            {
                foreach (var label in _optionIndex.Keys)
                    box.Items.Add(label);
            }
            else
            {
                foreach (var m in _settings.KnownModels)
                    box.Items.Add(m);
            }

            var slot = slots.FirstOrDefault(s => string.Equals(s.Id, slotId, StringComparison.OrdinalIgnoreCase));

            // 已选中的值（可能是手填的、不在列表里）→ 没有就退回 .env 里的现值
            var stored = slot is null
                ? null
                : PluginModelSlots.ModelFor(spec, _settings, slot) ?? Core.Config.EnvFile.Get(slot.Env);

            box.Text = DisplayFor(stored);
        }

        if (knownOnly && _settings.KnownModels.Count == 0)
            _status.Text = _settings.ModelsFetchedAt is null
                ? "还没查过模型列表：点「查询可用模型」，或直接手填模型名。"
                : $"上次查询失败：{_settings.LastDiscoveryError}";
    }

    /// <summary>把设置里存的值翻译成下拉框要显示的文本。</summary>
    /// <remarks>
    /// 存的值可能是 <c>model@3</c>（指定了通道）。这里找到对应的那一项，
    /// 显示它完整的标签；找不到就原样显示 —— 宁可显示得难看，
    /// 也不能把用户手填的模型名悄悄改掉。
    /// </remarks>
    private string DisplayFor(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return string.Empty;

        var (model, keyIndex) = PluginModelSlots.SplitSelection(stored);

        if (keyIndex is { } index)
        {
            var exact = _settings.ModelOptions.FirstOrDefault(o =>
                o.KeyIndex == index && string.Equals(o.Model, model, StringComparison.OrdinalIgnoreCase));

            if (exact is not null)
                return exact.ToString();
        }

        var sameModel = _settings.ModelOptions.FirstOrDefault(o =>
            string.Equals(o.Model, model, StringComparison.OrdinalIgnoreCase));

        return sameModel?.ToString() ?? stored!;
    }

    private async Task DiscoverAsync(bool announce)
    {
        var spec = _manifest.Settings;

        if (spec is not { CanDiscoverModels: true })
            return;

        var baseUrl = Core.Config.EnvFile.Get(spec.BaseEnv ?? string.Empty) ?? spec.DefaultBase;
        var keys = PluginKeySet.FromEnvironment(spec);

        if (keys.IsEmpty)
        {
            SetStatus("没有 key，查不了模型列表。先在 .env 里配 key。");
            return;
        }

        if (announce)
            SetStatus("正在查询可用模型…");

        var result = await ModelCatalogAll
            .FetchAsync(keys, baseUrl, spec.ModelsPath)
            .ConfigureAwait(true);

        if (!result.Ok)
        {
            _settings = _settings with { LastDiscoveryError = result.Describe() };

            // 只改"发现结果"，不动用户已选的模型
            _host.SaveSettings(_manifest.Id, _settings);

            SetStatus($"查询失败：{result.Describe()}");
            return;
        }

        _settings = _settings with
        {
            KnownModels = result.Models,
            ModelOptions = result.Options,
            ModelKeys = result.ModelKeys,
            ModelsFetchedAt = DateTimeOffset.Now,
            LastDiscoveryError = null,
        };

        _host.SaveSettings(_manifest.Id, _settings);

        // 重建下拉内容（含「模型 × key × 地址」的完整通道列表），并保留已选中的那一项。
        RefreshModels(knownOnly: false);

        var slots = PluginModelSlots.Effective(spec);

        SetStatus("查到 " + result.Describe() +
                  $"（{slots.Count} 个槽位共用这份列表），用时约 {result.PerKey.Sum(per => per.ElapsedMs)} ms。");
    }

    private void Save()
    {
        var spec = _manifest.Settings;
        var slots = PluginModelSlots.Effective(spec);
        var picked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in slots)
        {
            if (!_slotBoxes.TryGetValue(slot.Id, out var box))
                continue;

            var value = ResolveSelection(box.Text?.Trim());

            if (!string.IsNullOrWhiteSpace(value))
                picked[slot.Id] = value!;
        }

        // 老字段仍写第一个槽位的值：日志 / 旧代码读它时不会断档。
        var legacy = slots.Count > 0 && picked.TryGetValue(slots[0].Id, out var firstModel)
            ? firstModel
            : null;

        var updated = _settings with
        {
            Enabled = _enabled.IsChecked == true,
            SelectedModels = picked,
            SelectedModel = legacy,
        };

        if (_host.SaveSettings(_manifest.Id, updated) is { } error)
        {
            SetStatus("保存失败：" + error);
            return;
        }

        _settings = updated;

        try
        {
            // 立刻重载：启用/禁用当场生效，模型选择也在下一次取工具时用上
            var report = _host.Reload(force: true);
            SetStatus("已保存。" + report.Summary);
            _log($"[插件] {_manifest.Id} 设置已保存；{report.Summary}");
        }
        catch (Exception ex)
        {
            SetStatus("已保存，但重载失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把下拉框里的文本变成要存进设置的值。
    /// </summary>
    /// <remarks>
    /// <b>为什么只有重复模型才带 <c>@序号</c>：</b>
    /// 唯一的模型不需要额外信息，存 <c>gpt-image-2</c> 就够了，用户在 .env 里
    /// 看到它也不会困惑；只有当同名模型有多条通道时，才必须记下"选的是哪条"，
    /// 否则下一次发请求会随机落到其中一条。
    ///
    /// 手填的模型名（不在列表里）**原样保存**，不猜、不补、不改写。
    /// </remarks>
    private string? ResolveSelection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (_optionIndex.TryGetValue(text!, out var option))
        {
            return HasMultipleChannels(option.Model)
                ? PluginModelSlots.JoinSelection(option.Model, option.KeyIndex)
                : option.Model;
        }

        return text;
    }

    private bool HasMultipleChannels(string model)
        => _settings.ModelOptions.Count(o => string.Equals(o.Model, model, StringComparison.OrdinalIgnoreCase)) > 1;

    private void OpenEnvFile()
    {
        try
        {
            var (path, _) = Core.Config.EnvFile.LoadNearest(_root);

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SetStatus("没找到 .env 文件（工作台根：" + _root + "）");
                return;
            }

            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("打不开 .env：" + ex.Message);
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    private static Brush? Look(Window owner, string key)
        => owner?.TryFindResource(key) as Brush;
}
