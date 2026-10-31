using System.Runtime.InteropServices;
using FatFish.Core.Host;

namespace FatFish.App.Services;

/// <summary>
/// 枚举当前所有显示器的工作区（已扣掉任务栏）。
///
/// ★ 为什么要 P/Invoke 而不是 <c>System.Windows.Forms.Screen</c>：
///   那会给整个 WPF 项目引入 WinForms（UseWindowsForms=true），
///   随之而来的是 <c>Application</c> / <c>MessageBox</c> / <c>Timer</c> 全都会出现
///   同名歧义 —— 为了一个「列出屏幕」的功能付这个代价不值。
///   ★ 为什么不只用 <c>SystemParameters.WorkArea</c>：它只给主屏，
///   而「窗口在副屏上」恰恰是本功能要处理的首要场景。
///
/// ★ 坐标系转换：Win32 返回**物理像素**，WPF 的 Left/Top/Width/Height 是 **DIP**。
///   两者在高 DPI 下差 1.25 / 1.5 / 2 倍。不除的话，在 150% 缩放的机器上，
///   一块 1920 宽的屏会被当成 1920 DIP（实际 1280 DIP），于是「窗口明明在屏幕里」
///   也会被判成越界，然后每次启动都被强行搬位置。
///
/// ★ 多屏 DPI 不一致时这里只按主屏缩放换算 —— 不完美，但比 0 好得多，
///   而且错误方向是「偏保守」（算出来的屏幕更大），不会把可见窗口误判成不可见。
/// </summary>
internal static class ScreenEnumerator
{
    /// <summary>拿到所有显示器的工作区；失败时返回空列表（调用方会退回居中）。</summary>
    public static List<ScreenRect> WorkAreas(double dpiScale)
    {
        var result = new List<ScreenRect>();

        if (dpiScale <= 0 || double.IsNaN(dpiScale) || double.IsInfinity(dpiScale))
            dpiScale = 1.0;

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref Rect lprc, IntPtr data) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

                if (GetMonitorInfo(hMonitor, ref info))
                {
                    var w = info.Work.Right - info.Work.Left;
                    var h = info.Work.Bottom - info.Work.Top;

                    if (w > 0 && h > 0)
                    {
                        result.Add(new ScreenRect(
                            info.Work.Left / dpiScale,
                            info.Work.Top / dpiScale,
                            w / dpiScale,
                            h / dpiScale));
                    }
                }

                return true;   // 继续枚举下一块屏
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            // DllImport 失败（极端环境）、枚举中途异常 —— 都当成「没有屏幕信息」
            return [];
        }

        return result;
    }

    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect lprc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
}
