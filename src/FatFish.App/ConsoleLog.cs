using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace FatFish.App;

/// <summary>
/// 把「真控制台窗口」嵌进 WPF 面板 —— 移植自 Python 版的
/// <c>workspace/conhost_log/conlog.py</c>（Tk 版）。
///
/// <para>
/// 四步（与 Python 版一一对应）：
/// </para>
/// <list type="number">
///   <item>用 <c>CreateProcess</c> + <c>CREATE_NEW_CONSOLE</c> 起一个宿主进程
///         （<c>FatFish.exe --loghost-file</c>）→ 它有自己的控制台窗口。</item>
///   <item><c>FindWindowW("ConsoleWindowClass", 唯一标题)</c> 找到那个 HWND。</item>
///   <item>去掉 <c>WS_CAPTION/WS_THICKFRAME/…</c>、加 <c>WS_CHILD</c>，<c>SetParent</c> 到
///         <see cref="ConsoleStage"/> 的宿主窗口，<c>MoveWindow</c> 铺满。</item>
///   <item>日志按行 append 到文件，宿主按偏移 tail 后写自己的 <c>CONOUT$</c>。</item>
/// </list>
///
/// <para>
/// <b>为什么必须 CreateProcess</b>：实测（自检 <c>--trace-selftest</c>）
/// 从 GUI 进程用 <c>Process.Start</c> 起 cmd.exe，<b>拿不到控制台窗口</b> ——
/// Windows 只在父进程有控制台时才让子进程继承，要新分配必须显式
/// <c>CREATE_NEW_CONSOLE</c>，而 .NET 的 <c>ProcessStartInfo</c> 不暴露这个标志。
/// </para>
///
/// <para>
/// <b>为什么日志走文件而不是 stdin</b>：CreateProcess 那条路上再拼 stdin 管道，
/// 要自己建管道句柄并处理继承，收益为零。文件是 append-only 的，宿主 tail 即可，
/// 顺带还能事后查。
/// </para>
///
/// <para>
/// <b>安全边界</b>：只认 <c>ConsoleWindowClass</c>；只动自己起的那个宿主窗口
/// （标题里带 pid + 随机串）。任何一步失败都返回 false，调用方退回文本框。
/// </para>
/// </summary>
internal sealed class ConsoleLog : IDisposable
{
    // ---------- Win32 常量 ----------
    private const int GwlStyle = -16;

    private const long WsChild = 0x40000000;
    private const long WsPopup = 0x80000000;
    private const long WsCaption = 0x00C00000;
    private const long WsThickFrame = 0x00040000;
    private const long WsBorder = 0x00800000;
    private const long WsDlgFrame = 0x00400000;
    private const long WsSysMenu = 0x00080000;
    private const long WsMinimizeBox = 0x00020000;
    private const long WsMaximizeBox = 0x00010000;

    private const int SwHide = 0;
    private const int SwShowNormal = 1;

    private const int SwpNoZOrder = 0x0004;
    private const int SwpShowWindow = 0x0040;
    private const int SwpFrameChanged = 0x0020;

    private const uint CreateNewConsole = 0x00000010;
    private const int StartfUseShowWindow = 0x00000001;

    private const string ConsoleClass = "ConsoleWindowClass";

    /// <summary>VT 清屏 + 光标归位（宿主已打开 ENABLE_VIRTUAL_TERMINAL_PROCESSING）。</summary>
    private const string ClearScreen = "\u001b[2J\u001b[H";

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly CancellationTokenSource _cts = new();

    private Process? _proc;
    private FileStream? _stream;
    private Thread? _pump;

    private string _title = string.Empty;
    private string _filePath = string.Empty;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _attached;

    /// <summary>宿主进程与窗口都还在（写之前必须先看这个）。</summary>
    public bool IsAlive
    {
        get
        {
            if (_proc is not { HasExited: false })
                return false;

            // ★ 句柄悬空的坑（2026-11-13 实测）：窗口被跨进程 SetParent 之后，
            //   某些情况下 conhost 会重建自己的窗口 —— 旧 HWND 变悬空，但宿主进程
            //   活得好好的。只看 IsWindow(_hwnd) 会把这种情况误判成「宿主没了」，
            //   于是点「弹出」只会得到一句「还没起来（或已经退出）」，且永不恢复。
            //   这里按唯一标题重找一次，找到就接管新句柄。
            if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            {
                var again = FindWindowW(ConsoleClass, _title);

                if (again != IntPtr.Zero)
                {
                    _hwnd = again;
                    _attached = false;      // 新句柄还没挂进面板
                }
            }

            return _hwnd != IntPtr.Zero && IsWindow(_hwnd);
        }
    }

    /// <summary>当前是否嵌在 WPF 面板里（false = 弹出成了独立窗口）。</summary>
    public bool IsAttached => _attached;

    /// <summary>控制台窗口句柄（排障用）。</summary>
    public IntPtr WindowHandle => _hwnd;

    /// <summary>日志文件路径（排障用）。</summary>
    public string LogFile => _filePath;

    /// <summary>宿主进程 id（0 = 没起来 / 已退出）。</summary>
    public int ProcessId
    {
        get
        {
            try
            {
                return _proc?.Id ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>宿主进程是否已退出。</summary>
    public bool HasExited
    {
        get
        {
            try
            {
                return _proc is null || _proc.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>上一次启动失败的原因（成功时为 null）。</summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// 当前桌面上所有 <c>ConsoleWindowClass</c> 窗口的标题（排障用）。
    /// 找窗口失败时靠它回答「到底是窗口没创建，还是标题不一样」。
    /// </summary>
    public static List<string> ConsoleWindowTitles()
    {
        var list = new List<string>();

        try
        {
            EnumWindows((hWnd, _) =>
            {
                var cls = new StringBuilder(256);
                GetClassNameW(hWnd, cls, cls.Capacity);

                if (cls.ToString() != ConsoleClass)
                    return true;

                var title = new StringBuilder(512);
                GetWindowTextW(hWnd, title, title.Capacity);
                list.Add(title.ToString());
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败就返回空表
        }

        return list;
    }

    /// <summary>
    /// 按标题片段枚举顶层窗口，返回「类名 | 标题」。
    /// <paramref name="contains"/> 为 null 时返回全部。
    ///
    /// <para>
    /// 为什么需要它：Windows 11 的默认终端可能是 Windows Terminal ——
    /// 那时控制台窗口的类名<b>不是</b> <c>ConsoleWindowClass</c>，
    /// <c>FindWindowW(ConsoleWindowClass, …)</c> 会一直找不到。靠这个探针
    /// 可以判断「窗口没创建」还是「窗口在，只是类名不同」。
    /// </para>
    /// </summary>
    public static List<string> TopWindowTitles(string? contains = null)
    {
        var list = new List<string>();

        try
        {
            EnumWindows((hWnd, _) =>
            {
                var cls = new StringBuilder(256);
                GetClassNameW(hWnd, cls, cls.Capacity);

                var title = new StringBuilder(512);
                GetWindowTextW(hWnd, title, title.Capacity);

                var line = cls + " | " + title;

                if (contains is null
                    || line.Contains(contains, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(line);
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败就返回空表
        }

        return list;
    }

    /// <summary>
    /// 起宿主进程并等它的控制台窗口出现。<b>会阻塞</b>（默认最多 5 秒），
    /// 所以只能在后台线程调用 —— UI 线程上调用会让首次打开面板时顿一下。
    /// </summary>
    public bool Start(string title, string filePath, int timeoutMs = 5000)
    {
        if (_proc is not null)
            return IsAlive;

        _title = title;
        _filePath = filePath;

        try
        {
            var dir = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // append 模式 + 先清空：每次启动是一个新会话
            _stream = new FileStream(
                filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        catch
        {
            _stream = null;
            return false;
        }

        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
        {
            CloseStream();
            return false;
        }

        // ★ 必须经过 cmd.exe 一层：实测（--trace-selftest）
        //   ① GUI 进程用 Process.Start 起 cmd.exe → 没有任何控制台窗口；
        //   ② CREATE_NEW_CONSOLE 直接起自己（WinExe）→ 进程活着，但窗口数为 0；
        //   只有「CREATE_NEW_CONSOLE + console 子系统程序（cmd.exe）」才会真的
        //   分配出 ConsoleWindowClass 窗口。cmd 再用 /c 把我们自己拉起来当宿主。
        var inner = $"\"{exe}\" --loghost-file \"{filePath}\" \"{title}\"";
        var commandLine = $"cmd.exe /c \"{inner}\"";

        var si = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            dwFlags = StartfUseShowWindow,
            // ★ 不能用 SW_HIDE 启动：实测（--trace-selftest）这样启动时系统
            //   会给进程分配控制台，却**不创建任何窗口**（整桌面 ConsoleWindowClass = 0）。
            //   改成正常显示，找到窗口后立刻 ShowWindow(SW_HIDE)，只闪一下。
            wShowWindow = SwShowNormal,
        };

        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                            CreateNewConsole, IntPtr.Zero, null, ref si, out var pi))
        {
            var err = Marshal.GetLastWin32Error();
            LastFailure = "CreateProcess 失败 err=" + err;
            CloseStream();
            throw new Win32Exception(err);
        }

        try
        {
            _proc = Process.GetProcessById(pi.dwProcessId);
        }
        catch
        {
            _proc = null;
        }

        // 句柄我们不用，尽早关掉（进程本身还在）
        try
        {
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }
        catch
        {
            // ignore
        }

        if (_proc is null)
        {
            CloseStream();
            return false;
        }

        _pump = new Thread(PumpWrites)
        {
            IsBackground = true,
            Name = "fatfish-consolelog-writer",
        };

        _pump.Start();

        // 等窗口出现（标题是唯一的，不会认错别人的控制台）
        var deadline = Environment.TickCount64 + Math.Max(200, timeoutMs);

        while (Environment.TickCount64 < deadline)
        {
            var h = FindWindowW(ConsoleClass, title);

            if (h != IntPtr.Zero)
            {
                _hwnd = h;
                ShowWindow(_hwnd, SwHide);
                return true;
            }

            if (_proc.HasExited)
            {
                try
                {
                    LastFailure = "宿主已退出 exit=" + _proc.ExitCode;
                }
                catch
                {
                    LastFailure = "宿主已退出";
                }

                break;
            }

            Thread.Sleep(40);
        }

        LastFailure ??= "超时未见窗口（pid=" + ProcessId + "）";

        // 起不来：收掉子进程，别留一个看不见的进程
        DisposeProcess();
        return false;
    }

    /// <summary>把控制台窗口塞进 <paramref name="parentHwnd"/> 并铺满。</summary>
    public bool Attach(IntPtr parentHwnd, int width, int height)
    {
        // IsAlive 而不是 IsWindow：conhost 可能重建过窗口、旧句柄悬空，
        // IsAlive 会按唯一标题把新句柄接回来再挂。
        if (parentHwnd == IntPtr.Zero || !IsAlive)
            return false;

        try
        {
            var style = GetWindowLongPtrW(_hwnd, GwlStyle).ToInt64();

            style &= ~(WsPopup | WsCaption | WsThickFrame | WsBorder
                       | WsDlgFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);

            style |= WsChild;

            SetWindowLongPtrW(_hwnd, GwlStyle, new IntPtr(style));
            SetParent(_hwnd, parentHwnd);

            Fit(width, height);

            ShowWindow(_hwnd, SwShowNormal);
            _attached = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>容器尺寸变了：让控制台跟着变（像素级）。</summary>
    public void Fit(int width, int height)
    {
        if (!IsWindow(_hwnd) || width <= 0 || height <= 0)
            return;

        try
        {
            MoveWindow(_hwnd, 0, 0, width, height, true);
        }
        catch
        {
            // 尺寸调整失败不影响后续写入
        }
    }

    /// <summary>脱离面板并隐藏 —— 必须在面板销毁<b>之前</b>调用。</summary>
    public void Detach()
    {
        if (!IsWindow(_hwnd))
        {
            _attached = false;
            return;
        }

        try
        {
            ShowWindow(_hwnd, SwHide);
            SetParent(_hwnd, IntPtr.Zero);

            // 恢复成普通顶层窗口的样式，否则弹出去会变成一个没有边框的怪东西
            var style = GetWindowLongPtrW(_hwnd, GwlStyle).ToInt64();
            style &= ~WsChild;
            style |= WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox;
            SetWindowLongPtrW(_hwnd, GwlStyle, new IntPtr(style));
            _attached = false;
        }
        catch
        {
            _attached = false;
        }
    }

    /// <summary>
    /// 弹成独立窗口：物理上跟主程序脱钩 ——
    /// 主窗口卡死（UI 线程被占住）时，这个窗口由 conhost 自己渲染，照样滚。
    /// </summary>
    public bool PopOut(int x, int y, int width, int height)
    {
        // 用 IsAlive 而不是 IsWindow：宿主可能还在、只是句柄被 conhost 重建过，
        // IsAlive 会按唯一标题把新句柄接回来。
        if (!IsAlive)
            return false;

        try
        {
            // 位置兜底：窗口比屏幕右/下边界还远时会「弹到看不见的地方」，
            // 用户只会觉得按了没反应。夹回工作区内。
            var area = System.Windows.SystemParameters.WorkArea;

            if (x + width > area.Right)
                x = Math.Max(0, (int)area.Right - width - 40);

            if (y + height > area.Bottom)
                y = Math.Max(0, (int)area.Bottom - height - 40);

            if (x < 0)
                x = 0;

            if (y < 0)
                y = 0;

            ShowWindow(_hwnd, SwHide);
            SetParent(_hwnd, IntPtr.Zero);

            var style = GetWindowLongPtrW(_hwnd, GwlStyle).ToInt64();
            style &= ~WsChild;
            style |= WsPopup | WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox;
            SetWindowLongPtrW(_hwnd, GwlStyle, new IntPtr(style));

            // ★ SWP_FRAMECHANGED 不能省：WS_CHILD ↔ WS_POPUP 是框架级切换，
            //   不刷新非客户区时窗口可能出来但不可见/没边框（表现为「点了一点反应都没有」）。
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height,
                         SwpShowWindow | SwpNoZOrder | SwpFrameChanged);
            ShowWindow(_hwnd, SwShowNormal);
            SetForegroundWindow(_hwnd);

            _attached = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写一段文本。永不阻塞调用方（进队列，后台线程落文件）。</summary>
    public void Write(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsAlive)
            return;

        try
        {
            _queue.Add(text);
        }
        catch (InvalidOperationException)
        {
            // 已经 CompleteAdding（正在收尾）
        }
    }

    /// <summary>清屏（VT 序列，宿主侧已开 VT 处理）。</summary>
    public void Clear() => Write(ClearScreen);

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    private void PumpWrites()
    {
        try
        {
            foreach (var text in _queue.GetConsumingEnumerable(_cts.Token))
            {
                var stream = _stream;

                if (stream is null)
                    break;

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                catch
                {
                    break;      // 写不进去：宿主也读不到了，停
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常收尾
        }
    }

    private void CloseStream()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // ignore
        }

        _stream = null;
    }

    private void DisposeProcess()
    {
        try
        {
            _queue.CompleteAdding();
        }
        catch
        {
            // ignore
        }

        _cts.Cancel();

        CloseStream();

        try
        {
            if (_proc is { HasExited: false })
                _proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }

        try
        {
            _proc?.Dispose();
        }
        catch
        {
            // ignore
        }

        _proc = null;
        _hwnd = IntPtr.Zero;
        _attached = false;
    }

    public void Dispose()
    {
        Detach();
        DisposeProcess();
        _cts.Dispose();
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                           int x, int y, int cx, int cy, uint uFlags);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}

/// <summary>
/// WPF 侧的原生子窗口宿主。WPF 元素本身没有 HWND，所以要先建一个空的原生窗口
/// （类名 <c>static</c>）当「舞台」，再把 conhost 的窗口 <c>SetParent</c> 进来。
///
/// <para>
/// ★ 生命周期：TabControl 切页会卸载再重建 HwndHost，<see cref="DestroyWindowCore"/>
///   被调用时<b>必须先把控制台窗口收回去</b>（<see cref="BeforeDestroy"/>），
///   否则父窗口一销毁会把 conhost 窗口一起带走 —— Python 版 <c>conlog.py</c> 里
///   特意写了一条注释提醒这件事。
/// </para>
/// </summary>
internal sealed class ConsoleStage : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    /// <summary>舞台窗口句柄（给 ConsoleLog.Attach 用）。销毁后为 Zero。</summary>
    public IntPtr StageHandle { get; private set; }

    /// <summary>销毁舞台之前调用 —— 用于先把 conhost 窗口收回去。</summary>
    public Action? BeforeDestroy { get; set; }

    /// <summary>尺寸变化（像素）。</summary>
    public Action<int, int>? Sized { get; set; }

    /// <summary>
    /// 舞台建好之后调用（此时才拿得到句柄）。TabControl 切页会卸载再重建，
    /// 每次重建都要靠这个回调把 conhost 窗口重新挂进来。
    /// </summary>
    public Action? Built { get; set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        StageHandle = CreateWindowExW(
            0, "static", string.Empty,
            WsChild | WsVisible | WsClipChildren,
            0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        // 等这一轮布局结束再回调：此刻 ActualWidth/ActualHeight 还是 0，
        // 马上 Attach 会让控制台先以 1x1 出现，闪一下才铺满。
        var built = Built;

        if (built is not null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    built();
                }
                catch
                {
                    // 挂不上就继续用降级文本框
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        return new HandleRef(this, StageHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        try
        {
            BeforeDestroy?.Invoke();
        }
        catch
        {
            // 收不回去也不能阻止销毁
        }

        DestroyWindow(hwnd.Handle);
        StageHandle = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        try
        {
            Sized?.Invoke((int)ActualWidth, (int)ActualHeight);
        }
        catch
        {
            // 尺寸回调失败不影响渲染
        }
    }
}
