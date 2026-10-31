using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FatFish.App.Theme;

/// <summary>界面主题：亮色（白底黑字）/ 暗色（黑底白字）。</summary>
public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// 运行时换肤：白底黑（彩）字 ↔ 黑底白（彩）字。
/// </summary>
/// <remarks>
/// <para><b>怎么实现的：</b>CeTheme.xaml 里的主题画刷键在 XAML 中一律用
/// <c>{DynamicResource …}</c> 引用（不是 StaticResource）；切换时本类往
/// <c>Application.Resources</c> 里**换掉这个键对应的值**（放进一个新的纯色/渐变画刷），
/// DynamicResource 会自动重解析，于是所有引用该键的控件立刻重绘。
/// 全窗口共享一套键，所以只需换 40 多个值，所有窗口（含审批窗、配置编辑器）一起换。</para>
///
/// <para><b>为什么不用「就地改画刷颜色」这套更省事的写法：</b>实测不行。
/// WPF 把这些画刷当资源加载时会**冻结**（<c>IsFrozen == true</c>），
/// 冻结的 Freezable 改 <c>Color</c> 直接抛 InvalidOperationException ——
/// 而且在 XAML 上写 <c>po:Freeze="False"</c> 在本机这版 WPF 上**也没被尊重**
/// （见 App.RunThemeSmoke 的实测结论：BAML 加载完成时就已经是冻结态）。
/// 因此改色这条路是死的，只能换值。</para>
///
/// <para><b>两条硬约束（下次改主题前必读）：</b>
/// ① 模板/样式里<b>不许写死色值</b>，一律引用资源键 —— 写死的那一处不会跟着变
///    （CeTheme.xaml 里原先 13 处 <c>Value="#FF…"</c> 就是为这个改成了键）；
/// ② 引用主题画刷请用 <c>DynamicResource</c>；代码里给控件上色请用
///    <c>SetResourceReference(…) </c>，别 <c>FindResource</c> 一个 Brush 存起来 ——
///    存下来的实例在换肤后还是旧颜色。</para>
///
/// <para><b>亮色不另写一份表：</b>启动时（<see cref="Init"/>）把 CeTheme.xaml 里的原色
/// **抄一份快照**，切回亮色就按快照重发 —— 天然可逆，同一种颜色不用维护两遍
/// （改 XAML 不用同步改代码）。</para>
///
/// <para><b>线程：</b>必须在 UI 线程调用。</para>
/// </remarks>
public static class ThemeManager
{
    /// <summary>当前主题。默认亮色 —— 与 CeTheme.xaml 里那套 CE 7.5 实测色一致。</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>上一次 <see cref="Apply"/> 真正换掉的键数（诊断用）。</summary>
    public static int LastChangedCount { get; private set; }

    /// <summary>上一次 <see cref="Apply"/>/<see cref="Init"/> 遇到的问题（缺键等）。正常应为空。</summary>
    public static IReadOnlyList<string> LastProblems => _problems;

    /// <summary>主题切换后触发（按钮图标、菜单打勾之类跟着刷新）。</summary>
    public static event EventHandler? Changed;

    private static readonly List<string> _problems = new();

    /// <summary>
    /// 暗色配色表：键 = CeTheme.xaml 里的资源键，值 = 暗色下该键的颜色。
    /// </summary>
    /// <remarks>
    /// 取色思路照抄 CE 的准则：<b>大面积中性灰、靠 1px 线分区、彩色只留给图标</b>，
    /// 只是把明度轴整个翻过来 —— 底 1E1E1E / 面板 262626 / 输入 141414 / 正文 E4E4E4，
    /// 强分隔线从 #BCBCBC 压到 #4E4E4E，图标色统一提亮一档（暗底上原色会糊）。
    /// </remarks>
    private static readonly Dictionary<string, Color> _dark = new(StringComparer.Ordinal)
    {
        // ---- 语义色（对应 CeTheme.xaml「色板」段）----
        ["BgBase"] = C("1E1E1E"),
        ["BgPanel"] = C("262626"),
        ["BgInput"] = C("141414"),
        ["FgPrimary"] = C("E4E4E4"),
        ["FgMuted"] = C("9B9B9B"),
        ["Accent"] = C("5AA9E6"),
        ["AccentBorder"] = C("3A6E9B"),
        ["Warn"] = C("E0A422"),
        ["Danger"] = C("E05A4A"),
        ["UserBubble"] = C("232A31"),
        ["AiBubble"] = C("1A1A1A"),

        // ---- CE 专用画刷 ----
        ["Chrome"] = C("2A2A2A"),
        ["Surface"] = C("1E1E1E"),
        ["SurfaceWhite"] = C("161616"),
        ["LineStrong"] = C("4E4E4E"),
        ["Line"] = C("3A3A3A"),
        ["LineSoft"] = C("2E2E2E"),
        ["SplitLine"] = C("2C2C2C"),
        ["SelBack"] = C("2C5375"),
        ["HoverBack"] = C("333333"),
        ["PressBack"] = C("3E3E3E"),

        // ---- 说话人色条 ----
        ["VoiceUser"] = C("5AA9E6"),
        ["VoiceAi"] = C("B6C36A"),
        ["VoiceSystem"] = C("8A8A8A"),

        // ---- 图标彩色（暗底统一提亮，否则 #3E8E41 这类会糊成一团）----
        ["IconGreen"] = C("66BB6A"),
        ["IconRed"] = C("E05A4A"),
        ["IconAmber"] = C("FFC65C"),
        ["IconBlue"] = C("5AA9E6"),
        ["IconGray"] = C("9E9E9E"),
        ["IconViolet"] = C("B69BE0"),

        // ---- 控件细节（原先写死在模板里的那批，现已改成键）----
        ["FgDisabled"] = C("6A6A6A"),
        ["BtnFace"] = C("303030"),
        ["BtnBorder"] = C("5A5A5A"),
        ["BtnHoverFace"] = C("3A3A3A"),
        ["BtnHoverBorder"] = C("8A8A8A"),
        ["BtnPressFace"] = C("262626"),
        ["ToolHoverFace"] = C("343434"),
        ["ToolHoverBorder"] = C("525252"),
        ["ToolPressFace"] = C("262626"),
        ["ToolPressBorder"] = C("6E6E6E"),
        ["InputBorder"] = C("4E4E4E"),
        ["InputEdge"] = C("060606"),
        ["InputEdgeLight"] = C("3A3A3A"),
        ["ListHoverFace"] = C("2B3138"),
        ["TabFace"] = C("2E2E2E"),
        ["TabHoverFace"] = C("3A3A3A"),

        // ---- 滚动条（原先没有样式，走系统 Aero，深色下是一条亮疤）----
        ["ScrollTrack"] = C("232323"),
        ["ScrollThumb"] = C("4A4A4A"),
        ["ScrollThumbHover"] = C("5E5E5E"),
        ["ScrollThumbPress"] = C("7A7A7A"),
        ["ScrollArrow"] = C("9B9B9B"),
    };

    /// <summary>暗色下的渐变（键 = 资源键，值 = 起点色/终点色，按 GradientStops 顺序）。</summary>
    private static readonly Dictionary<string, (Color From, Color To)> _darkGradient =
        new(StringComparer.Ordinal)
        {
            ["HeaderFace"] = (C("343434"), C("262626")),
        };

    private static Dictionary<string, Color>? _light;
    private static Dictionary<string, (Color From, Color To)>? _lightGradient;
    private static bool _inited;

    /// <summary>暗色表覆盖了哪些纯色键（供冒烟核对覆盖率）。</summary>
    public static IEnumerable<string> PaletteKeys => _dark.Keys;

    /// <summary>暗色表覆盖了哪些渐变键。</summary>
    public static IEnumerable<string> GradientPaletteKeys => _darkGradient.Keys;

    /// <summary>某个主题下滚动条「槽」的颜色（诊断/自测用：证明滚动条真的换了样式）。</summary>
    public static string TrackColorOf(AppTheme theme) =>
        (theme == AppTheme.Dark ? _dark["ScrollTrack"] : Color.FromRgb(0xF0, 0xF0, 0xF0))
        .ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>把 <c>UI_THEME</c> 这样的文本解析成主题：只认 "dark"（不分大小写），其余都是亮色。</summary>
    public static AppTheme Parse(string? text) =>
        string.Equals(text?.Trim(), "dark", StringComparison.OrdinalIgnoreCase)
            ? AppTheme.Dark
            : AppTheme.Light;

    /// <summary>
    /// 抄一份「亮色底稿」：从 CeTheme.xaml 合并进来的那套资源里读原色并快照。
    /// </summary>
    /// <remarks>
    /// 只读不写 —— 那些画刷是冻结的，读颜色没问题，改不行。
    /// 必须在任何窗口建出来之前调用（App.OnStartup 里），这样第一次 Apply 就有底稿可用。
    /// </remarks>
    /// <returns>是否成功（拿得到资源字典且至少读到一个键）。</returns>
    public static bool Init()
    {
        var app = Application.Current;

        if (app is null)
            return false;

        var source = FindThemeDictionary(app.Resources);

        if (source is null)
        {
            _problems.Add("没找到 CeTheme 资源字典 —— 换肤不可用");
            return false;
        }

        _light = new Dictionary<string, Color>(StringComparer.Ordinal);
        _lightGradient = new Dictionary<string, (Color From, Color To)>(StringComparer.Ordinal);

        foreach (var key in _dark.Keys)
        {
            if (source[key] is SolidColorBrush brush)
                _light[key] = brush.Color;
            else
                _problems.Add(key + "：CeTheme.xaml 里没有这个纯色画刷");
        }

        foreach (var key in _darkGradient.Keys)
        {
            if (source[key] is LinearGradientBrush gradient && gradient.GradientStops.Count >= 2)
                _lightGradient[key] = (gradient.GradientStops[0].Color, gradient.GradientStops[^1].Color);
            else
                _problems.Add(key + "：CeTheme.xaml 里没有这个渐变画刷");
        }

        _inited = _light.Count > 0;

        return _inited;
    }

    /// <summary>亮色/暗色互换。</summary>
    public static AppTheme Toggle()
    {
        Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
        return Current;
    }

    /// <summary>切到指定主题：把每个主题键的值换成对应颜色，DynamicResource 会自动生效。</summary>
    /// <returns>实际生效的主题（没有 Application 时只记状态）。</returns>
    public static AppTheme Apply(AppTheme theme)
    {
        var app = Application.Current;

        if (app is null)
        {
            // 没有 Application（单测 / 命令行）时没有资源可换，只把状态记下来。
            Current = theme;
            LastChangedCount = 0;
            return theme;
        }

        if (!_inited && !Init())
        {
            // 连底稿都拿不到就别乱换 —— 保持现状，问题记在 LastProblems 里。
            Current = theme;
            LastChangedCount = 0;
            return theme;
        }

        _problems.Clear();

        var changed = 0;

        if (theme == AppTheme.Dark)
        {
            foreach (var (key, color) in _dark)
                changed += Publish(app.Resources, key, color);

            foreach (var (key, stop) in _darkGradient)
                changed += PublishGradient(app.Resources, key, stop.From, stop.To);
        }
        else
        {
            foreach (var (key, color) in _light!)
                changed += Publish(app.Resources, key, color);

            foreach (var (key, stop) in _lightGradient!)
                changed += PublishGradient(app.Resources, key, stop.From, stop.To);
        }

        Current = theme;
        LastChangedCount = changed;
        ApplyWindowChrome();
        Changed?.Invoke(null, EventArgs.Empty);

        return theme;
    }

    /// <summary>
    /// 让**系统标题栏**也跟着主题走（深色主题配一根浅色标题栏太难看了）。
    /// </summary>
    /// <remarks>
    /// WPF 的标题栏是系统绘制的，改资源字典管不到；Win10 1809+ 提供
    /// <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>（属性号 20，更早的版本是 19），传 1 = 深色。
    /// 拿句柄失败（窗口还没建出来）就静默跳过 —— 下一次 Apply 或窗口加载时会再调。
    /// </remarks>
    public static void ApplyWindowChrome(Window? window = null)
    {
        var targets = new List<Window>();

        if (window is not null)
            targets.Add(window);
        else if (Application.Current is not null)
            targets.AddRange(Application.Current.Windows.Cast<Window>());

        var dark = Current == AppTheme.Dark ? 1 : 0;

        foreach (var w in targets)
        {
            try
            {
                var handle = new WindowInteropHelper(w).Handle;

                if (handle == IntPtr.Zero)
                    continue;

                // 0 = 成功。老系统不认 20 号属性，退回 19 再试一次。
                var hr = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));

                if (hr != 0)
                    hr = DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));

                // 结果只用来决定要不要退属性号：标题栏是装饰品，调不动也不该报错打扰人。
                _ = hr;
            }
            catch (Exception)
            {
                // 标题栏是装饰，失败了也不该影响功能
            }
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 换掉字典里这个键的值（写进 Application.Resources 自己这一层，遮蔽合并字典里的原值）。
    /// </summary>
    /// <remarks>
    /// 这里是**换值**不是改色：DynamicResource 监听的是资源变更，值一换它就会重解析，
    /// 所以冻结与否都不影响（冻结的只是被替换掉的那个旧实例）。
    /// </remarks>
    private static int Publish(ResourceDictionary res, string key, Color color)
    {
        if (res[key] is SolidColorBrush current && current.Color == color && !current.IsFrozen)
            return 0;

        res[key] = new SolidColorBrush(color);

        return 1;
    }

    private static int PublishGradient(ResourceDictionary res, string key, Color from, Color to)
    {
        if (res[key] is LinearGradientBrush current
            && current.GradientStops.Count >= 2
            && !current.IsFrozen
            && current.GradientStops[0].Color == from
            && current.GradientStops[^1].Color == to)
        {
            return 0;
        }

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };

        gradient.GradientStops.Add(new GradientStop(from, 0));
        gradient.GradientStops.Add(new GradientStop(to, 1));

        res[key] = gradient;

        return 1;
    }

    /// <summary>找出 App.xaml 里合并进来的 CeTheme 字典（合并字典可能有多层）。</summary>
    private static ResourceDictionary? FindThemeDictionary(ResourceDictionary root)
    {
        foreach (var dict in root.MergedDictionaries)
        {
            if (dict.Contains("Chrome") && dict.Contains("FgPrimary"))
                return dict;

            var nested = FindThemeDictionary(dict);

            if (nested is not null)
                return nested;
        }

        return null;
    }

    /// <summary>"RRGGBB" → 不透明 Color（不走 ColorConverter，避免文化/格式异常）。</summary>
    private static Color C(string rgb) => Color.FromRgb(
        Convert.ToByte(rgb.Substring(0, 2), 16),
        Convert.ToByte(rgb.Substring(2, 2), 16),
        Convert.ToByte(rgb.Substring(4, 2), 16));
}
