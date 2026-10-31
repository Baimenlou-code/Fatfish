using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FatFish.Core.Plugins;

namespace FatFish.App;

/// <summary>
/// 插件栏的界面接线（partial 扩展）。
///
/// 为什么不去改 MainWindow.xaml / MainWindow.xaml.cs：
///   · 那两个文件一个 41K、一个 57K，改动面越小越安全；
///   · 顶栏按钮可以用代码插进去 —— <c>SidebarButton</c> 是 x:Name 字段，
///     它的 Parent 就是那一排工具栏 StackPanel，往它前面 Insert 一个按钮即可，
///     样式与图标全部复用现有资源（ToolButton / IconFont / IconAmber）；
///   · <c>/qq on</c> 这类命令用<b>窗口级隧道处理器</b>拦截，见
///     <see cref="OnPluginCommandPreviewKeyDown"/> 里的说明 —— 同样不需要改大文件。
///
/// 与主窗口共享的实例成员：
///   · <c>_workspaceSession</c>  取工作台根
///   · <c>_plugins</c>           PluginHost（真实插件的装载中心）
///   · <c>Log(string)</c>        写进底部日志面板
///   · <c>StatusText</c>         状态栏文字（绑定属性）
///   · <c>InputBox</c>           输入框
/// </summary>
public partial class MainWindow
{
    private bool _pluginButtonInstalled;
    private bool _pluginCommandHooked;

    /// <summary>
    /// 界面渲染完成后，把「插件」按钮插到顶栏「侧边栏」按钮前面，挂上命令拦截，
    /// 再装好右侧的「过程」面板、审批栏、以及 QQ 链接层。
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        InstallPluginButton();
        InstallPluginCommandHook();
        InstallProcessPane();
        InstallApprovalBar();

        // 设置根后绑定：PluginHost 在窗口构建早期就起来了，那时工作台根往往还没定。
        // 不绑的话「启用/禁用」「记住选中的模型」整体不可用，但插件本身照常装载。
        string? root = _workspaceSession?.Root;

        if (!string.IsNullOrWhiteSpace(root) && _plugins is not null)
        {
            try
            {
                _plugins.BindSettingsRoot(root);
                Log("[插件] 设置根已绑定：" + root);

                // 启动后台发现：每个声明了 settings 的插件问一次 /models。
                // 放后台是因为它要联网；失败只写一行日志，绝不拖慢启动或弹窗。
                PluginDiscovery.StartBackground(_plugins, root, Log);
            }
            catch (Exception ex)
            {
                Log("[插件] 绑定设置根失败：" + ex.GetType().Name + " - " + ex.Message);
            }
        }

        // QQ 跟随模式：只装不自动上线。上线由 /qq on 或点插件栏那个条目触发，
        // 免得每次开程序都悄悄把 QQ 消息接进来（那会打断正在做别的事的用户）。
        InstallQqLink();
    }

    private void InstallPluginButton()
    {
        if (_pluginButtonInstalled)
            return;

        try
        {
            if (SidebarButton?.Parent is not Panel toolbar)
                return;

            var icon = new TextBlock
            {
                Text = "\uE71D",          // Segoe MDL2：AllApps，像一排小方块
                FontFamily = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = TryFindResource("IconAmber") as Brush ?? Brushes.Goldenrod,
            };

            var button = new Button
            {
                Name = "PluginsButton",
                Content = icon,
                ToolTip =
                    "插件栏\n" +
                    "点开＝列出插件：QQ 桥接 / 每个已装插件（⚙ 进设置）\n" +
                    "· 插件行：点一行就是启动 / 停止；⚙ 里可启用/禁用（热插拔，立刻生效）\n" +
                    "· 生图插件：plugins/liqiu-image，key 在 .env\n" +
                    "· 底部预留「＋ 添加插件」槽位：把插件文件夹拷进 plugins/ 即可\n" +
                    "命令兼容：/qq on ｜ /qq off ｜ /qq status",
            };

            if (TryFindResource("ToolButton") is Style style)
                button.Style = style;

            button.Click += OnPluginsClick;

            int index = toolbar.Children.IndexOf(SidebarButton);
            toolbar.Children.Insert(index < 0 ? 0 : index, button);
            _pluginButtonInstalled = true;
            Log("插件栏已就绪（顶栏「插件」按钮）");
        }
        catch (Exception ex)
        {
            Log("插件栏按钮安装失败：" + ex.GetType().Name + " - " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- /qq 命令

    private void InstallPluginCommandHook()
    {
        if (_pluginCommandHooked)
            return;

        PreviewKeyDown += OnPluginCommandPreviewKeyDown;
        _pluginCommandHooked = true;
        Log("插件命令已接线：/qq on ｜ /qq off ｜ /qq status");
    }

    /// <summary>
    /// 在回车「发送给模型」之前拦截 <c>/qq</c> 命令。
    ///
    /// <para>
    /// <b>为什么挂在窗口上、而不是 InputBox 上：</b>
    /// WPF 的 Preview（隧道）事件是从根节点往下走的，窗口级处理器<b>必然先于</b>
    /// InputBox 自己的 PreviewKeyDown 执行。而 InputBox 那个处理器（MainWindow.xaml.cs
    /// 里的 OnInputKeyDown）一旦跑起来就会置 Handled 并直接调 OnSendClick —— 消息就发出去了。
    /// 所以必须抢在它前面：在这里置 Handled，它就不会被调用，也就不会误发给模型。
    /// </para>
    ///
    /// <para>
    /// 这样做的额外好处：完全不用改 MainWindow.xaml.cs（57K），
    /// 出问题时删掉本文件里的两处钩子即可回到原状。
    /// </para>
    /// </summary>
    private void OnPluginCommandPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 输入法组合中按键会报成 Key.ImeProcessed，天然放行，不会误触发。
        if (e.Key != Key.Enter)
            return;

        // Shift+Enter 是换行，别抢。
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return;

        // 只在焦点确实在输入框里、且内容以 /qq 开头时才接管。
        if (InputBox is null || !InputBox.IsKeyboardFocusWithin)
            return;

        string text = InputBox.Text;
        if (!text.TrimStart().StartsWith("/qq", StringComparison.OrdinalIgnoreCase))
            return;

        e.Handled = true;
        InputBox.Clear();
        _ = DispatchPluginCommandAsync(text);
    }

    /// <summary>
    /// 真正去跑命令。fire-and-forget，所以异常必须在这里兜住 ——
    /// 否则会变成一个没人观察的 Task 异常，界面上只表现为「按回车没反应」。
    /// </summary>
    private async Task DispatchPluginCommandAsync(string text)
    {
        try
        {
            await HandlePluginCommandAsync(text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            EmitPluginMessage("插件命令出错：" + ex.GetType().Name + " - " + ex.Message);
        }
    }

    /// <summary>
    /// 顶栏「插件」按钮：每次点击都重新探测，然后从按钮下方弹出菜单。
    /// 与 Python 版 [PLUGIN-BAR v1] 行为一致：点一行 = 启动 / 停止。
    /// </summary>
    private async void OnPluginsClick(object sender, RoutedEventArgs e)
    {
        string? root = _workspaceSession?.Root;
        if (string.IsNullOrWhiteSpace(root))
        {
            Log("插件栏：工作台根未知，无法定位 qq_bridge");
            return;
        }

        try
        {
            // 统一入口：这里会把「QQ 链接层」接进去（上线 / 下线 / 状态查询）。
            var service = CreatePluginService(root);
            ContextMenu menu = await service.BuildMenuAsync(EmitPluginMessage).ConfigureAwait(true);

            // 真实插件（PluginHost 里的那些）append 在后面：
            // 它们不是进程，没有"启停端口"可探，语义是"启用/禁用"。
            AppendCorePluginsMenu(menu, root);

            menu.PlacementTarget = sender as UIElement;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            EmitPluginMessage("插件栏出错：" + ex.GetType().Name + " - " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 真实插件

    /// <summary>
    /// 把 PluginHost 装载到的插件逐个列进菜单：每个插件一个 ⚙ 子菜单
    /// （启用/禁用、设置…、打开目录），底部再留一个「＋ 添加插件」槽位。
    /// </summary>
    private void AppendCorePluginsMenu(ContextMenu menu, string root)
    {
        PluginHost? host = _plugins;

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "插件（热插拔 · ⚙ 进设置）",
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        });

        if (host is null)
        {
            menu.Items.Add(new MenuItem { Header = "插件宿主未初始化", IsEnabled = false, Opacity = 0.6 });
        }
        else
        {
            var manifests = host.Manifests;

            if (manifests.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = "（还没有装插件：把插件文件夹放进 plugins/）",
                    IsEnabled = false,
                    Opacity = 0.6,
                    FontSize = 11,
                });
            }

            foreach (var manifest in manifests)
            {
                bool enabled = host.SettingsFor(manifest.Id).Enabled;
                int toolCount = manifest.Tools.Count;

                var header = new StackPanel();
                header.Children.Add(new TextBlock
                {
                    Text = $"⚙  {manifest.Name}    {(enabled ? "● 启用中" : "○ 已禁用")}",
                    FontWeight = FontWeights.SemiBold,
                });
                header.Children.Add(new TextBlock
                {
                    Text = $"{manifest.Id} · {toolCount} 个工具" +
                           (manifest.Settings is null ? " · 未声明设置" : ""),
                    FontSize = 11,
                    Opacity = 0.7,
                    Margin = new Thickness(0, 1, 0, 0),
                });

                var item = new MenuItem { Header = header };
                PluginManifest captured = manifest;

                var toggle = new MenuItem { Header = enabled ? "禁用（立刻生效）" : "启用（立刻生效）" };
                toggle.Click += (_, _) => TogglePlugin(captured, !enabled);
                item.Items.Add(toggle);

                var settingsItem = new MenuItem
                {
                    Header = "设置…",
                    IsEnabled = manifest.Settings is not null,
                    ToolTip = manifest.Settings is null
                        ? "这个插件没有声明 settings 块，没有可改的配置"
                        : null,
                };
                settingsItem.Click += (_, _) => OpenPluginSettings(captured);
                item.Items.Add(settingsItem);

                var reveal = new MenuItem { Header = "打开插件目录" };
                reveal.Click += (_, _) => RevealFolder(captured.DirectoryPath);
                item.Items.Add(reveal);

                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());

        // ★ 预留槽位：以后可以在这里挂"从文件夹安装 / 从 zip 安装"。
        //   现在先做到"点开就停在 plugins/ 目录"，把安装这件事交给资源管理器。
        var add = new MenuItem { Header = "＋ 添加插件（打开 plugins 目录）" };
        add.Click += (_, _) => RevealFolder(Path.Combine(root, "plugins"));
        menu.Items.Add(add);

        var reload = new MenuItem { Header = "⟳ 重载插件（热插拔）" };
        reload.Click += (_, _) =>
        {
            try
            {
                var report = host is null ? null : host.Reload(force: true);
                EmitPluginMessage(report?.Summary ?? "插件宿主未初始化");
            }
            catch (Exception ex)
            {
                EmitPluginMessage("重载插件失败：" + ex.GetType().Name + " - " + ex.Message);
            }
        };
        menu.Items.Add(reload);
    }

    private void TogglePlugin(PluginManifest manifest, bool enable)
    {
        try
        {
            if (_plugins is null)
            {
                EmitPluginMessage("插件宿主未初始化");
                return;
            }

            var report = _plugins.SetEnabled(manifest.Id, enable);
            EmitPluginMessage($"{manifest.Name} → {(enable ? "已启用" : "已禁用")}；{report.Summary}");
        }
        catch (Exception ex)
        {
            EmitPluginMessage("切换插件状态失败：" + ex.GetType().Name + " - " + ex.Message);
        }
    }

    private void OpenPluginSettings(PluginManifest manifest)
    {
        try
        {
            string? root = _workspaceSession?.Root;

            if (_plugins is null || string.IsNullOrWhiteSpace(root))
            {
                EmitPluginMessage("插件宿主 / 工作台根不可用，打不开设置");
                return;
            }

            var window = new PluginSettingsWindow(_plugins, manifest, root, this, EmitPluginMessage);
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            EmitPluginMessage("打开插件设置失败：" + ex.GetType().Name + " - " + ex.Message);
        }
    }

    private void RevealFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                EmitPluginMessage("目录未知");
                return;
            }

            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            EmitPluginMessage("已打开：" + path);
        }
        catch (Exception ex)
        {
            EmitPluginMessage("打开目录失败：" + ex.GetType().Name + " - " + ex.Message);
        }
    }

    /// <summary>
    /// 插件栏的所有反馈走这里：底部日志 + 状态栏各一份。
    /// 可能从后台线程被调用，所以不直接碰控件，统一 Dispatcher 回 UI 线程。
    /// </summary>
    private void EmitPluginMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        void Apply()
        {
            Log("[插件] " + message);
            StatusText = "[插件] " + message;
        }

        if (Dispatcher.CheckAccess())
            Apply();
        else
            Dispatcher.BeginInvoke(new Action(Apply));
    }

    /// <summary>
    /// 命令兼容层：<c>/qq on</c>、<c>/qq off</c>、<c>/qq status</c> 走插件栏同一套逻辑。
    /// 返回 true 表示这条输入已被当作插件命令处理掉，不应再发给模型。
    ///
    /// 已由 <see cref="OnPluginCommandPreviewKeyDown"/> 接到输入框的回车上。
    /// </summary>
    private async Task<bool> HandlePluginCommandAsync(string text)
    {
        string? root = _workspaceSession?.Root;
        if (string.IsNullOrWhiteSpace(root))
        {
            EmitPluginMessage("工作台根未知，无法定位 qq_bridge");
            return false;
        }

        var service = CreatePluginService(root);
        bool handled = await service.TryHandleCommandAsync(text, EmitPluginMessage).ConfigureAwait(true);

        if (handled)
        {
            // 光看端口会误以为「能用」—— 桥接进程在跑但跟随模式没开时，群里照样不回。
            // 所以 status 之后补一句链接层的真实状态。
            string arg = text.Trim()
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                             .Skip(1)
                             .FirstOrDefault()
                             ?.ToLowerInvariant()
                         ?? "status";

            if (arg == "status")
                EmitPluginMessage("跟随模式：" + (_qqLink?.StatusLine ?? "链接层未安装"));
        }

        return handled;
    }
}
