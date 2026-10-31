using System.Text.Json;
using System.Text.Json.Serialization;

namespace FatFish.Core.Host;

/// <summary>
/// 一块屏幕（或屏幕上的「工作区」—— 已扣掉任务栏）。
///
/// ★ 刻意不用 WPF 的 <c>System.Windows.Rect</c>：Core 的目标框架是 net10.0
///   （不带 -windows），引 WPF 类型会让整个内核只能在 Windows 上编译，
///   也使这段「纯几何」逻辑无法在 Linux CI 上测。
/// </summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    [JsonIgnore]
    public double Right => X + Width;

    [JsonIgnore]
    public double Bottom => Y + Height;

    [JsonIgnore]
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);

    [JsonIgnore]
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>交集面积（无交集为 0）。</summary>
    public double IntersectArea(ScreenRect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);

        return w <= 0 || h <= 0 ? 0 : w * h;
    }

    public override string ToString()
        => $"({X:0},{Y:0} {Width:0}×{Height:0})";
}

/// <summary>
/// 窗口的位置与尺寸。
/// </summary>
/// <param name="Left">左边界（设备无关像素）。</param>
/// <param name="Top">上边界。</param>
/// <param name="Width">宽度。</param>
/// <param name="Height">高度。</param>
/// <param name="Maximized">是否处于最大化（此时上面四个值是「还原后」的位置）。</param>
public sealed record WindowPlacement(
    [property: JsonPropertyName("left")] double Left,
    [property: JsonPropertyName("top")] double Top,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("maximized")] bool Maximized = false)
{
    /// <summary>默认尺寸（首次启动，或存的尺寸坏到没法用）。</summary>
    public const double DefaultWidth = 1180;

    public const double DefaultHeight = 800;

    /// <summary>下限：再小就没法用了（工具栏 + 对话框会挤成一团）。</summary>
    public const double MinWidth = 880;

    public const double MinHeight = 560;

    /// <summary>
    /// 序列化时**不写**这些计算属性。
    /// </summary>
    /// <remarks>
    /// 不加 JsonIgnore 的话，window.json 里会多出 bounds / area / isEmpty / right / bottom
    /// 五个只读字段 —— 反序列化时它们被忽略（无害），但文件从 5 行变成 20 行，
    /// 手工排查时全是噪音，还容易让人以为这些字段可以改。
    /// </remarks>
    [JsonIgnore]
    public ScreenRect Bounds => new(Left, Top, Width, Height);

    /// <summary>在工作区里居中、取默认尺寸。</summary>
    public static WindowPlacement Centered(ScreenRect workArea)
    {
        var w = Math.Min(DefaultWidth, Math.Max(MinWidth, workArea.Width - 80));
        var h = Math.Min(DefaultHeight, Math.Max(MinHeight, workArea.Height - 80));

        if (workArea.IsEmpty)
            return new WindowPlacement(64, 64, DefaultWidth, DefaultHeight);

        return new WindowPlacement(
            Math.Round(workArea.X + (workArea.Width - w) / 2),
            Math.Round(workArea.Y + (workArea.Height - h) / 2),
            Math.Round(w),
            Math.Round(h));
    }

    public override string ToString()
        => $"{Left:0},{Top:0} {Width:0}×{Height:0}{(Maximized ? " 最大化" : string.Empty)}";
}

/// <summary>
/// 窗口状态的读写与「可见性修正」。
///
/// ★ 这个类解决的是一个很具体、很常见的故障：
///   用户在外接显示器上把窗口拖到右边（坐标 x=2400），回家拔掉显示器再启动，
///   主屏只有 1920 宽 —— 窗口「存在」，但它在屏幕外面。
///   表现是：任务栏里有图标、点了没反应、看起来像启动失败。
///   用户的第一反应是「程序坏了」，而实际上只需要把坐标拉回来。
///
///   所以这里不只是「记住位置」，而是**记住 + 每次加载时校验**。
///   只存不验的实现，等于给这类故障留了一个永久入口。
/// </summary>
public static class WindowPlacementStore
{
    /// <summary>标题栏至少要有这么高露在屏幕内，用户才抓得住它。</summary>
    private const double MinVisibleTitleBarHeight = 60;

    /// <summary>水平方向至少要有这么宽露出来。</summary>
    private const double MinVisibleWidth = 160;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string workspaceRoot)
        => Path.Combine(workspaceRoot, "logs", "ui", "window.json");

    /// <summary>
    /// 读上次的窗口状态，并保证结果一定落在某块屏幕上。
    /// </summary>
    /// <param name="workspaceRoot">工作台根。</param>
    /// <param name="screens">当前所有屏幕的工作区。</param>
    /// <param name="primaryWorkArea">主屏工作区（找不到合适位置时的落点）。</param>
    public static WindowPlacement Load(
        string workspaceRoot,
        IReadOnlyList<ScreenRect> screens,
        ScreenRect primaryWorkArea)
    {
        WindowPlacement? saved = null;

        try
        {
            var path = PathFor(workspaceRoot);

            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                saved = JsonSerializer.Deserialize<WindowPlacement>(json, JsonOptions);
            }
        }
        catch (Exception)
        {
            // 文件坏了、权限没了 —— 一律当成「没有存过」，回到默认位置。
            // 绝不因为「记不住窗口位置」而让程序起不来。
            saved = null;
        }

        if (saved is null)
            return WindowPlacement.Centered(primaryWorkArea);

        return Clamp(saved, screens, primaryWorkArea);
    }

    /// <summary>保存。失败静默（同 <see cref="Session.SessionStore.Append"/> 的原则）。</summary>
    public static void Save(string workspaceRoot, WindowPlacement state)
    {
        try
        {
            var path = PathFor(workspaceRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 先写临时文件再替换：直接覆盖的话，恰好在此时断电会留下半截 JSON，
            // 下次启动就回到默认位置 —— 位置丢了是小事，但没人会想到是这个原因。
            var tmp = path + ".tmp";

            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            // 同上：记不住位置不该是错误
        }
    }

    /// <summary>
    /// 把任意窗口状态修正成「一定能在某块屏幕上操作」。
    /// </summary>
    /// <remarks>
    /// 三条规则，按顺序：
    /// 1. 尺寸夹到 [MinWidth/MinHeight, 屏幕最大] —— 存到 400×300 或 5000×3000 都修回来；
    /// 2. 标题栏必须露出来：顶部 60px 与某块屏幕的水平交集 ≥ 160px，
    ///    否则用户抓不到标题栏，拖不动（这正是「窗口在屏幕外」的真实体感）；
    /// 3. 都不满足 → 回到主屏居中。
    /// </remarks>
    public static WindowPlacement Clamp(
        WindowPlacement state,
        IReadOnlyList<ScreenRect> screens,
        ScreenRect primaryWorkArea)
    {
        var usable = screens.Where(s => !s.IsEmpty).ToList();

        if (usable.Count == 0)
        {
            // 没有屏幕信息（远程桌面刚断开 / WPF 还没初始化）：给一个保守的默认值，
            // 不要按 stored 值硬套 —— 那正是「窗口跑到屏幕外」的来源。
            return WindowPlacement.Centered(primaryWorkArea);
        }

        // ---- 1. 尺寸 ----
        var maxW = usable.Max(s => s.Width);
        var maxH = usable.Max(s => s.Height);

        var w = Math.Clamp(double.IsFinite(state.Width) ? state.Width : WindowPlacement.DefaultWidth,
            WindowPlacement.MinWidth, Math.Max(WindowPlacement.MinWidth, maxW));

        var h = Math.Clamp(double.IsFinite(state.Height) ? state.Height : WindowPlacement.DefaultHeight,
            WindowPlacement.MinHeight, Math.Max(WindowPlacement.MinHeight, maxH));

        var left = double.IsFinite(state.Left) ? state.Left : primaryWorkArea.X;
        var top = double.IsFinite(state.Top) ? state.Top : primaryWorkArea.Y;

        var candidate = new WindowPlacement(left, top, w, h, state.Maximized);

        // ---- 2. 标题栏可见性 ----
        var titleBarTop = top;
        var titleBarBottom = top + MinVisibleTitleBarHeight;

        var hasGrip = usable.Any(s =>
        {
            var vOverlap = Math.Min(titleBarBottom, s.Bottom) - Math.Max(titleBarTop, s.Y);
            if (vOverlap <= 0)
                return false;

            var hOverlap = Math.Min(left + w, s.Right) - Math.Max(left, s.X);
            return hOverlap >= MinVisibleWidth;
        });

        if (hasGrip)
        {
            // ---- 3a. 有抓手：再把整窗推回到「与它交集最大的那块屏」里 ----
            var home = usable.OrderByDescending(s => s.IntersectArea(candidate.Bounds)).First();

            var newLeft = Math.Clamp(left, home.X - (w - MinVisibleWidth), home.Right - MinVisibleWidth);
            var newTop = Math.Clamp(top, home.Y, home.Bottom - MinVisibleTitleBarHeight);

            return candidate with { Left = Math.Round(newLeft), Top = Math.Round(newTop) };
        }

        // ---- 3b. 没抓手（换了显示器 / 分辨率变小）：回主屏居中 ----
        // ★ 只挪位置、保留尺寸：用户调过的大小是他的偏好，不该因为换了屏就丢掉。
        //   尺寸在步骤 1 已经夹到当前屏幕能容纳的范围，所以这里不会又跑出去。
        var centered = WindowPlacement.Centered(primaryWorkArea);

        var left2 = primaryWorkArea.IsEmpty
            ? centered.Left
            : Math.Round(primaryWorkArea.X + (primaryWorkArea.Width - w) / 2);

        var top2 = primaryWorkArea.IsEmpty
            ? centered.Top
            : Math.Round(primaryWorkArea.Y + (primaryWorkArea.Height - h) / 2);

        return new WindowPlacement(left2, top2, w, h, state.Maximized);
    }
}
