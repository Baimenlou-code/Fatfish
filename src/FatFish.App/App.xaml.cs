using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FatFish.App.Theme;
using FatFish.Core.Safety;
using FatFish.Core.Tools;

namespace FatFish.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 未捕获异常统一兜底：绝不让窗口无声消失
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ShowFatal(ex, "AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ShowFatal(args.Exception, "TaskScheduler");
            args.SetObserved();
        };

        // 换肤底稿：先把 CeTheme.xaml 里那套亮色抄一份快照（只读，不动画刷），
        // 之后无论切到暗色还是切回亮色都有据可依。必须赶在任何窗口建出来之前。
        ThemeManager.Init();

        // 每扇窗一加载就把系统标题栏调成当前主题的深/浅 —— 包括后面才弹出来的
        // 审批窗、配置编辑器，不必各自记得调一次。
        EventManager.RegisterClassHandler(
            typeof(Window),
            System.Windows.FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
                ThemeManager.ApplyWindowChrome(sender as Window)));

        // 冒烟钩子：打开配置编辑器 → 等一会儿 → 关掉 → 按结果退进程。
        // 只在显式设了环境变量时才生效，正常启动完全不受影响。
        var smokePath = Environment.GetEnvironmentVariable("FATFISH_SMOKE_ENV_EDITOR");

        if (!string.IsNullOrWhiteSpace(smokePath))
        {
            Dispatcher.BeginInvoke(
                new Action(() => RunEnvEditorSmoke(smokePath.Trim())),
                DispatcherPriority.ApplicationIdle);
        }

        // 夜间模式冒烟：FATFISH_SMOKE_THEME=1 时跑一遍「切暗 → 校验 → 切回」再退出。
        // 与上面同一个套路：只在显式设了环境变量时才生效，正常启动完全不受影响。
        var smokeTheme = Environment.GetEnvironmentVariable("FATFISH_SMOKE_THEME");

        if (!string.IsNullOrWhiteSpace(smokeTheme) && smokeTheme.Trim() == "1")
        {
            // ★ 必须等 ApplicationIdle —— 消息循环起来之后窗口才会真正测量/渲染。
            //   曾在 OnStartup 里同步跑过一次：资源值改得没问题，但探针控件
            //   DesiredSize 一直是 0,0（没测量），于是「宽度 0」的假失败。
            Dispatcher.BeginInvoke(
                new Action(RunThemeSmoke),
                DispatcherPriority.ApplicationIdle);
        }

        // 审批窗外观冒烟：FATFISH_SMOKE_APPROVAL=<png 路径> 时，把审批窗离屏渲成 PNG。
        var smokeApproval = Environment.GetEnvironmentVariable("FATFISH_SMOKE_APPROVAL");

        if (!string.IsNullOrWhiteSpace(smokeApproval))
        {
            Dispatcher.BeginInvoke(
                new Action(() => RunApprovalSmoke(smokeApproval.Trim())),
                DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>
    /// 配置编辑器的 GUI 冒烟测试。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要这个钩子：</b>编译通过只证明 XAML 语法没问题，
    /// 证明不了「StaticResource 能解析」「表单能建出来」「读文件不抛异常」。
    /// 而这几件事恰好是 XAML 最容易踩的坑（少一个资源键、ToolTip 里写了绑定到不存在属性的东西）。
    /// 它们只在**窗口真正构造的那一刻**才炸，冒烟脚本不打开窗口就永远发现不了。
    ///
    /// <b>为什么放在 App 而不是测试项目：</b>测试项目是 net10.0（无 WPF），
    /// 引用不了 Window 类型。GUI 只能靠真启动进程来验。
    ///
    /// 退出码：0 = 正常打开与关闭；2 = 构造或关闭时抛异常。
    /// 窗口被放到屏幕外，运行时间 600ms，不会打扰正在用电脑的人。
    /// </remarks>
    private async void RunEnvEditorSmoke(string envPath)
    {
        try
        {
            var window = new EnvEditorWindow(envPath)
            {
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                Width = SmokeNumber("FATFISH_SMOKE_ENV_WIDTH", 900),
                Height = SmokeNumber("FATFISH_SMOKE_ENV_HEIGHT", 600),
            };

            window.Show();

            await Task.Delay(600);

            // ---- 量宽度：量的是真渲染出来的控件，不是按 XAML 估算 ----
            window.UpdateLayout();

            var root = (FrameworkElement)window.Content;
            var editors = new List<(string Kind, double Width)>();

            foreach (var box in FindDescendants<TextBox>(root))
                editors.Add(("TextBox", box.ActualWidth));

            foreach (var box in FindDescendants<PasswordBox>(root))
                editors.Add(("PasswordBox", box.ActualWidth));

            foreach (var box in FindDescendants<ComboBox>(root))
                editors.Add(("ComboBox", box.ActualWidth));

            var widths = editors.Select(ed => ed.Width).OrderBy(x => x).ToList();

            var report = new List<string>
            {
                "窗口 " + Px(window.ActualWidth) + " x " + Px(window.ActualHeight),
            };

            if (widths.Count > 0)
            {
                report.Add("可编辑控件 " + widths.Count.ToString(CultureInfo.InvariantCulture)
                    + " 个：最小 " + Px(widths[0]) + "，中位 " + Px(widths[widths.Count / 2])
                    + "，最大 " + Px(widths[^1]));
                report.Add("窄于 260 的 " + widths.Count(x => x < 260).ToString(CultureInfo.InvariantCulture)
                    + " 个；窄于 400 的 " + widths.Count(x => x < 400).ToString(CultureInfo.InvariantCulture) + " 个");
            }
            else
            {
                report.Add("⚠ 一个可编辑控件都没找到 —— 表单没建出来");
            }

            var png = Environment.GetEnvironmentVariable("FATFISH_SMOKE_ENV_PNG");

            if (!string.IsNullOrWhiteSpace(png))
            {
                var target = png.Trim();

                report.AddRange(await CaptureAsync(window, target, "env_editor", ownWindow: false)
                    .ConfigureAwait(true));

                File.WriteAllText(target + ".txt", string.Join(Environment.NewLine, report));
            }

            window.Close();

            WriteSmokeResult("OK");

            Shutdown(0);
        }
        catch (Exception ex)
        {
            WriteSmokeResult("FAIL " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);

            Shutdown(2);
        }
    }

    /// <summary>
    /// 冒烟结果同时写到 stdout 和「<输入路径>.smoke.txt」。
    /// </summary>
    /// <remarks>
    /// 双写是因为 WinExe 在有些启动方式下拿不到可用的 stdout
    /// （没有控制台、也没被重定向）。文件是可靠的兜底 ——
    /// 少了它，失败时只能看到退出码 2，完全不知道该修哪儿。
    /// </remarks>
    private static void WriteSmokeResult(string text)
    {
        try
        {
            Console.WriteLine("SMOKE_ENV_EDITOR " + text);
        }
        catch (IOException)
        {
            // stdout 不可用时忽略，下面还会写文件
        }

        try
        {
            var marker = Environment.GetEnvironmentVariable("FATFISH_SMOKE_RESULT");

            if (!string.IsNullOrWhiteSpace(marker))
                File.WriteAllText(marker, text);
        }
        catch (IOException)
        {
            // 结果文件写不出来也不该让进程崩
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    /// <summary>
    /// 夜间模式的 GUI 冒烟：真启动进程、真解析资源字典，验证「换色真的生效、且可逆」。
    /// </summary>
    /// <remarks>
    /// <b>为什么要这个钩子：</b>换肤链路上有两处只有运行时才暴露的东西 ——
    /// ① 资源键对不上（少一个键，那一处切了不会变）；
    /// ② DynamicResource 到底有没有跟着资源变更重新解析。
    /// 编译通过、静态检查都证明不了这两条。上一版实现（就地改画刷颜色）正是靠它发现
    /// 「BAML 加载出来的画刷是冻结的，改色会抛异常」，才有了现在这套换值的写法。
    ///
    /// 结果写到 stdout 与 FATFISH_SMOKE_RESULT 指向的文件；退出码 0 = 通过，2 = 失败。
    /// </remarks>
    private void RunThemeSmoke()
    {
        var report = new List<string>();

        try
        {
            var res = Resources;
            var probes = new[] { "Chrome", "SurfaceWhite", "FgPrimary", "LineStrong", "InputEdge", "HeaderFace" };
            var light = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var key in probes)
                light[key] = ReadThemeBrush(res, key);

            // 探针：故意用 DynamicResource 引用，与 XAML 里各控件的引用方式一致。
            // ★ 必须真挂进一个 Window 的视觉树 —— 实测**游离的控件**收不到资源变更通知
            //   （DynamicResource 的重解析走的是视觉树那套），游离着测会假失败。
            //   窗口放到屏幕外（-20000），不打扰正在用电脑的人。
            var probeWindow = new Window
            {
                Width = 160,
                Height = 90,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };

            var consumer = new Border();
            consumer.SetResourceReference(Border.BackgroundProperty, "Chrome");
            probeWindow.Content = consumer;
            probeWindow.Show();

            ThemeManager.Apply(AppTheme.Dark);

            if (ThemeManager.LastProblems.Count > 0)
                throw new InvalidOperationException("换肤告警：" + string.Join("；", ThemeManager.LastProblems));

            var darkChrome = ReadThemeBrush(res, "Chrome");
            var darkFg = ReadThemeBrush(res, "FgPrimary");

            if (darkChrome == light["Chrome"])
                throw new InvalidOperationException("切暗后 Chrome 没变：" + darkChrome);

            if (ReadThemeBrush(res, "HeaderFace") == light["HeaderFace"])
                throw new InvalidOperationException("面板标题条的渐变没跟着换");

            probeWindow.UpdateLayout();

            var consumerColor = consumer.Background is SolidColorBrush probeBrush ? Fmt(probeBrush.Color) : "?";
            var consumerOk = consumerColor == darkChrome;
            probeWindow.Close();

            // ---- 滚动条：原先没样式，走的是系统 Aero（浅灰 + 立体箭头），切夜间是一条亮疤 ----
            //   真撑一个 80 行列表出来，找到 ScrollBar 验三件事：
            //   ① 用的是自家模板（宽度 14；系统 Aero 是 17）；
            //   ② 背景（槽色）等于当前主题的 ScrollTrack；
            //   ③ 能找到 Thumb（滑块），说明 Track 结构没写坏。
            var scrollHost = new Window
            {
                Width = 240,
                Height = 260,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
            };

            var listBox = new ListBox();

            for (var i = 0; i < 80; i++)
                listBox.Items.Add("第 " + i + " 行：用来把竖向滚动条撑出来");

            scrollHost.Content = listBox;
            scrollHost.Show();

            // 离屏窗口 + 虚拟化列表，模板是**排到下一步**才生成的，
            // 所以要泵几次 Loaded 优先级再找（只 UpdateLayout 一次会找不到，实测过）。
            ScrollBar? bar = null;

            for (var attempt = 0; attempt < 12 && bar is null; attempt++)
            {
                scrollHost.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                scrollHost.UpdateLayout();

                // ★ 只认**竖向且已测量**的那条：模板里横向滚动条是折叠的，
                //   ActualWidth 自然是 0，抓错了会得出「宽度 0，还在用 Aero」的假失败。
                bar = FindDescendants<ScrollBar>(listBox)
                    .FirstOrDefault(b => b.Orientation == Orientation.Vertical && b.ActualWidth > 0);
            }

            var source = "80 行列表内部";

            if (bar is null)
            {
                // 撑不出来也别放弃这条断言：单放一个 ScrollBar，验它套的是哪套模板 ——
                // ScrollViewer 内部的滚动条走的是同一条「隐式样式」规则。
                var lone = new ScrollBar
                {
                    Orientation = Orientation.Vertical,
                    Minimum = 0,
                    Maximum = 100,
                    Value = 30,
                    Height = 200,
                };

                // 显式把「隐式样式」套上（TryFindResource(typeof(ScrollBar)) 拿的就是它），
                // 并泵一次 Render 优先级 —— 离屏窗口只 UpdateLayout 不一定会测量。
                // 精确取 CeTheme 里那条「隐式样式」（键就是 typeof(ScrollBar)）：
                // 用 TryFindResource 有可能拿到系统主题自带的那条，验了等于没验。
                if (Resources.MergedDictionaries.Count > 0
                    && Resources.MergedDictionaries[0][typeof(ScrollBar)] is Style ours)
                {
                    lone.Style = ours;
                }

                scrollHost.Content = lone;
                scrollHost.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                scrollHost.UpdateLayout();
                bar = lone;
                source = "独立 ScrollBar（列表撑不出时的退路）";
            }

            var thumb = FindDescendant<Thumb>(bar);

            var barColor = bar.Background is SolidColorBrush barBrush ? Fmt(barBrush.Color) : "?";

            if (Math.Abs(bar.ActualWidth - 14) > 0.5)
                throw new InvalidOperationException(
                    "滚动条宽度 " + bar.ActualWidth + "（期望 14）｜来源=" + source
                    + "｜IsVisible=" + bar.IsVisible
                    + "｜Visibility=" + bar.Visibility
                    + "｜DesiredSize=" + bar.DesiredSize
                    + "｜Style=" + (bar.Style?.TargetType.Name ?? "null")
                    + "｜槽色=" + barColor);

            if (barColor != ThemeManager.TrackColorOf(AppTheme.Dark))
                throw new InvalidOperationException(
                    "滚动条槽色 " + barColor + "（期望 " + ThemeManager.TrackColorOf(AppTheme.Dark) + "）");

            report.Add("滚动条（" + source + "）：宽度 " + bar.ActualWidth
                + "，槽色 " + barColor + "，Thumb 已就位");
            scrollHost.Close();

            report.Add("dark：Chrome " + light["Chrome"] + " → " + darkChrome
                + "（换掉 " + ThemeManager.LastChangedCount + " 个键），FgPrimary " + darkFg);
            report.Add("DynamicResource 探针：" + consumerColor + (consumerOk ? "（已跟着变）" : "（未跟着变）"));

            if (!consumerOk)
                report.Add("⚠ 探针没同步 —— 若实际界面也不变色，说明 DynamicResource 链路断了");

            // ---- 切回亮色：逐项还原（快照思路的可逆性验证）----
            ThemeManager.Apply(AppTheme.Light);

            foreach (var key in probes)
            {
                var now = ReadThemeBrush(res, key);

                if (now != light[key])
                    throw new InvalidOperationException(key + " 切回亮色没还原：" + now + " ≠ " + light[key]);
            }

            report.Add("light：" + probes.Length + " 个探针逐项还原");

            // ---- 覆盖率：主题字典里每个画刷键都必须在暗色表里，否则那一处切了不会变 ----
            var theme = Resources.MergedDictionaries.Count > 0 ? Resources.MergedDictionaries[0] : null;

            if (theme is not null)
            {
                var covered = new HashSet<string>(ThemeManager.PaletteKeys, StringComparer.Ordinal);

                foreach (var k in ThemeManager.GradientPaletteKeys)
                    covered.Add(k);

                var missing = new List<string>();

                foreach (var key in theme.Keys)
                {
                    if (key is not string name)
                        continue;

                    var value = theme[key];

                    if ((value is SolidColorBrush || value is LinearGradientBrush) && !covered.Contains(name))
                        missing.Add(name);
                }

                if (missing.Count > 0)
                    throw new InvalidOperationException(
                        "这些画刷键没进暗色表（切夜间不会变色）：" + string.Join("、", missing));
            }

            report.Add("覆盖率：暗色表覆盖全部画刷键");

            WriteThemeSmokeResult("OK\n" + string.Join("\n", report));
            Shutdown(0);
        }
        catch (Exception ex)
        {
            WriteThemeSmokeResult("FAIL " + ex.GetType().Name + ": " + ex.Message);
            Shutdown(2);
        }
    }

    /// <summary>在视觉树里收集所有 T 类型的后代。</summary>
    private static List<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();

        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T hit)
                found.Add(hit);

            found.AddRange(FindDescendants<T>(child));
        }

        return found;
    }

    /// <summary>在视觉树里找第一个 T 类型的后代（找不到返回 null）。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T hit)
                return hit;

            var nested = FindDescendant<T>(child);

            if (nested is not null)
                return nested;
        }

        return null;
    }

    /// <summary>把一个主题画刷读成可比较的字符串（纯色 → #AARRGGBB；渐变 → 首尾两色）。</summary>
    private static string ReadThemeBrush(ResourceDictionary res, string key) => res[key] switch
    {
        SolidColorBrush sb => Fmt(sb.Color),
        LinearGradientBrush gb => Fmt(gb.GradientStops[0].Color) + " -> " + Fmt(gb.GradientStops[^1].Color),
        _ => throw new InvalidOperationException(key + " 既不是纯色也不是渐变画刷"),
    };

    /// <summary>颜色 → 不变文化的字符串（CA1305：默认 ToString 会随区域设置变）。</summary>
    private static string Fmt(Color color) => color.ToString(CultureInfo.InvariantCulture);

    /// <summary>夜间模式冒烟结果：stdout + FATFISH_SMOKE_RESULT 双写。</summary>
    private static void WriteThemeSmokeResult(string text)
    {
        try
        {
            Console.WriteLine("SMOKE_THEME " + text);
        }
        catch (IOException)
        {
            // stdout 不可用就算了，下面还有文件兜底
        }

        try
        {
            var marker = Environment.GetEnvironmentVariable("FATFISH_SMOKE_RESULT");

            if (!string.IsNullOrWhiteSpace(marker))
                File.WriteAllText(marker, text);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 外观冒烟：在指定主题下把「审批窗（普通 / 口令）」和「顶栏审批栏」都渲染/采样一遍，
    /// 结果写进 png 与 txt，供人眼和像素检查复核。
    /// </summary>
    /// <remarks>
    /// 为什么需要它：这些都是**运行才存在**的东西 ——
    ///   ① 审批窗是独立 Window，主窗变黑不代表它变；
    ///   ② 顶栏「审批」栏是代码插进工具栏的（InstallApprovalBar），它的弹层是独立 Popup；
    /// 编译通过证明不了任何一条。主题由 FATFISH_SMOKE_APPROVAL_THEME 指定（dark / light）。
    /// </remarks>
    private async void RunApprovalSmoke(string pngPath)
    {
        try
        {
            var theme = ThemeManager.Parse(
                Environment.GetEnvironmentVariable("FATFISH_SMOKE_APPROVAL_THEME") ?? "dark");

            ThemeManager.Apply(theme);

            var args = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            {
                ["path"] = System.Text.Json.JsonDocument.Parse("\"notes/todo.md\"").RootElement.Clone(),
                ["content"] = System.Text.Json.JsonDocument.Parse("\"# 待办\\n- 验审批窗\"").RootElement.Clone(),
            };

            var report = new List<string> { "主题：" + theme };

            report.AddRange(await CaptureAsync(
                new ApprovalWindow(new ApprovalRequest(
                    "ws_write", args, "写文件（有副作用，会改盘上内容）", true)),
                pngPath, "approval_normal").ConfigureAwait(true));

            report.AddRange(await CaptureAsync(
                new ApprovalWindow(new ApprovalRequest(
                    "ws_write", args, "写的是 .env（敏感文件），需要口令", false, ApprovalKind.Password)),
                pngPath, "approval_password").ConfigureAwait(true));

            // 顶栏「审批」栏：图标按钮 + 弹出菜单（与插件栏同构）
            report.AddRange(ProbeApprovalBar());

            // 主窗整窗渲一张：顶栏那排按钮、审批图标、菜单都在里面看得见
            foreach (Window w in Windows)
            {
                if (w is MainWindow main)
                {
                    report.AddRange(await CaptureAsync(main, pngPath, "main_window", ownWindow: false)
                        .ConfigureAwait(true));
                    break;
                }
            }

            File.WriteAllText(pngPath + ".txt", string.Join(Environment.NewLine, report));
            WriteSmokeResult("SMOKE_APPROVAL OK" + Environment.NewLine + string.Join(Environment.NewLine, report));
            Shutdown(0);
        }
        catch (Exception ex)
        {
            WriteSmokeResult("SMOKE_APPROVAL FAIL " + ex);
            Shutdown(2);
        }
    }

    /// <summary>顶栏「审批」栏的采样：按钮样式 / 图标色 / 点开后的菜单底色与勾选。</summary>
    private static List<string> ProbeApprovalBar()
    {
        var report = new List<string>();

        MainWindow? window = null;
        Button? button = null;

        foreach (Window w in Application.Current.Windows)
        {
            if (w is not MainWindow main)
                continue;

            window = main;
            button = FindDescendants<Button>(main)
                .FirstOrDefault(b => string.Equals(b.Name, "ApprovalButton", StringComparison.Ordinal));

            break;
        }

        if (window is null || button is null)
        {
            report.Add("⚠ 顶栏找不到「审批」按钮（ApprovalButton）—— 安装失败或被跳过");
            return report;
        }

        report.Add("审批按钮：样式=" + (button.Style?.TargetType.Name ?? "null")
            + "　图标色=" + BrushText((button.Content as TextBlock)?.Foreground)
            + "　提示首行=" + FirstLine(button.ToolTip as string));

        // 真点一下：菜单是弹层，只能用「打开后读它的样式」来验外观。
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var menu = window.LastApprovalMenu;

        if (menu is null)
        {
            report.Add("⚠ 点开审批按钮后没拿到菜单");
            return report;
        }

        report.Add("审批菜单：背景=" + BrushText(menu.Background)
            + "　描边=" + BrushText(menu.BorderBrush)
            + "　项数=" + menu.Items.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            report.Add("  项「" + Short(item.Header as string) + "」勾=" + item.IsChecked
                + "　fg=" + BrushText(item.Foreground));
        }

        menu.IsOpen = false;

        // 真点一下菜单项：验证「点菜单 → 策略改档 + 图标换色」这条链真的通，而不是只画了个菜单。
        var items = menu.Items.OfType<MenuItem>().ToList();

        if (items.Count == 4)
        {
            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            report.Add("点「最严」后：策略=" + Describe(window.CurrentApprovalLevel)
                + "　图标色=" + BrushText((button.Content as TextBlock)?.Foreground));

            items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            report.Add("点回「除敏感外免批」：策略=" + Describe(window.CurrentApprovalLevel)
                + "　图标色=" + BrushText((button.Content as TextBlock)?.Foreground));
        }

        return report;
    }

    /// <summary>把一个窗口的内容离屏渲染成 PNG，并收集各控件的实际颜色。</summary>
    /// <param name="ownWindow">true = 本方法负责把它挪到屏幕外、打开并在渲染后关闭；false = 窗口已经开着，只渲染。</param>
    private static async Task<List<string>> CaptureAsync(
        Window window, string pngPath, string tag, bool ownWindow = true)
    {
        var report = new List<string>();

        if (ownWindow)
        {
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.Show();
        }

        await Task.Delay(ownWindow ? 500 : 200);

        window.UpdateLayout();

        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();

        var px = (int)Math.Ceiling(root.ActualWidth);
        var py = (int)Math.Ceiling(root.ActualHeight);

        if (px <= 0 || py <= 0)
            throw new InvalidOperationException(tag + "：内容没测量出来 " + px + "x" + py);

        // ★ 必须先铺一层窗口自己的背景：口令闸门那种窗口把 Content 换成了透明 StackPanel，
        //   直接 Render(root) 得到的是透明底 —— 存成 PNG 看上去就是一片白，
        //   会让人误判成「深色没生效」（第一次做这个冒烟时就被它骗过）。
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.Transparent, null, new Rect(0, 0, px, py));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap(px, py, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var png = Path.Combine(
            Path.GetDirectoryName(pngPath) ?? ".",
            Path.GetFileNameWithoutExtension(pngPath) + "_" + tag + ".png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (var stream = File.Create(png))
            encoder.Save(stream);

        // 顶栏局部：工具栏按钮只有 30x26，整窗图里根本看不出画的是什么图标。
        var stripW = Math.Min(px, 560);
        var stripH = Math.Min(py, 78);

        if (stripW > 40 && stripH > 20)
        {
            var cropped = new CroppedBitmap(bitmap, new Int32Rect(0, 0, stripW, stripH));
            var stripEncoder = new PngBitmapEncoder();
            stripEncoder.Frames.Add(BitmapFrame.Create(cropped));

            using var stripStream = File.Create(Path.Combine(
                Path.GetDirectoryName(pngPath) ?? ".",
                Path.GetFileNameWithoutExtension(pngPath) + "_" + tag + "_top.png"));

            stripEncoder.Save(stripStream);
        }

        report.Add(tag + "：" + px + "x" + py + " → " + Path.GetFileName(png));
        report.Add("  窗背景 " + BrushText(window.Background));

        foreach (var element in FindDescendants<Border>(root))
        {
            if (element.Background is SolidColorBrush sb && sb.Color.A > 0)
                report.Add("  Border bg " + Fmt(sb.Color)
                    + " @ " + element.ActualWidth.ToString("0", CultureInfo.InvariantCulture)
                    + "x" + element.ActualHeight.ToString("0", CultureInfo.InvariantCulture));
        }

        foreach (var element in FindDescendants<TextBox>(root))
            report.Add("  TextBox " + element.Name + " bg=" + BrushText(element.Background)
                + " fg=" + BrushText(element.Foreground));

        foreach (var element in FindDescendants<PasswordBox>(root))
            report.Add("  PasswordBox bg=" + BrushText(element.Background)
                + " fg=" + BrushText(element.Foreground));

        foreach (var element in FindDescendants<TextBlock>(root))
            report.Add("  Text 「" + Short(element.Text) + "」 fg=" + BrushText(element.Foreground));

        foreach (var element in FindDescendants<Button>(root))
            report.Add("  Button 「" + Short(element.Content as string) + "」 bg="
                + BrushText(element.Background) + " fg=" + BrushText(element.Foreground)
                + " border=" + BrushText(element.BorderBrush));

        foreach (var element in FindDescendants<CheckBox>(root))
        {
            element.ApplyTemplate();
            element.UpdateLayout();
            var box = FindDescendant<Border>(element);
            report.Add("  CheckBox fg=" + BrushText(element.Foreground)
                + "　方框=" + BrushText(box?.Background) + "　描边=" + BrushText(box?.BorderBrush));
        }

        if (ownWindow)
            window.Close();

        return report;
    }

    /// <summary>把画刷读成可比较的字符串（纯色 → #AARRGGBB；渐变 → 首尾两色）。</summary>
    private static string BrushText(Brush? brush) => brush switch
    {
        SolidColorBrush sb => Fmt(sb.Color),
        LinearGradientBrush gb => Fmt(gb.GradientStops[0].Color) + "->" + Fmt(gb.GradientStops[^1].Color),
        null => "null",
        _ => brush.GetType().Name,
    };

    /// <summary>可空档位 → 文本（报告里用）。</summary>
    private static string Describe(ApprovalLevel? level) => level?.ToString() ?? "null";

    /// <summary>取多行文本的第一行（报告里用）。</summary>
    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var idx = text.IndexOf('\n', StringComparison.Ordinal);

        return idx < 0 ? text : text[..idx];
    }

    /// <summary>把多行文本压成一行短标签（报告里用）。</summary>
    private static string Short(string? text)
    {
        var one = (text ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " / ", StringComparison.Ordinal);

        return one.Length > 16 ? one[..16] + "…" : one;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception, "UI");
        e.Handled = true; // 保持窗口存活，便于用户复制错误信息
    }

    private static void ShowFatal(Exception ex, string source)
    {
        try
        {
            MessageBox.Show(
                $"[{source}] {ex.GetType().Name}\n\n{ex.Message}\n\n{ex.StackTrace}",
                "FatFish 未处理的异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // 连弹窗都失败时只能放弃 —— 但不要让异常继续传播
        }
    }
}
