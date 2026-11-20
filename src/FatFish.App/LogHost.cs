using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FatFish.App;

/// <summary>
/// 过程页宿主进程 —— 移植自 Python 版的 <c>workspace/conhost_log/loghost.py</c>。
///
/// <para>
/// 它唯一的工作就是：<b>拥有一个真控制台窗口</b>，把主进程写进日志文件的
/// UTF-8 字节原样写进自己的控制台。于是「过程」页 = 一个货真价实的 cmd 窗口：
/// 黑底、等宽、自带滚动条、鼠标拖选、ANSI 彩色，写入只改字符格子。
/// </para>
///
/// <para>
/// <b>为什么走文件而不是 stdin 管道</b>：.NET 的 <c>ProcessStartInfo</c> 不暴露
/// <c>CREATE_NEW_CONSOLE</c>，而 GUI 进程启动 console 子进程时 Windows
/// <b>不会</b>自动分配控制台（实测：父进程是 GUI，子进程拿不到控制台窗口）。
/// 所以启动必须走 <c>CreateProcess</c> 带 <c>CREATE_NEW_CONSOLE</c>，
/// 而那条路上再拼 stdin 管道要自己建管道句柄，不值得 ——
/// 文件是 append-only 的，宿主按偏移 tail 即可，顺带还能事后查。
/// </para>
///
/// <para>
/// 三个关键设计（都是 Python 版踩过坑才有的）：
/// </para>
/// <list type="number">
///   <item>
///     用 <c>CONOUT$</c> 直接打开控制台输出，<b>不依赖 std 句柄</b>。
///     ★ 必须以 <c>GENERIC_READ|GENERIC_WRITE</c> 打开：读窗口尺寸要求<b>读权限</b>，
///     只写句柄会让这些 API 静默失败。
///   </item>
///   <item>
///     <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> → ANSI 色码由控制台自己解析，
///     跟 bat 里看到的一模一样。
///   </item>
///   <item>
///     后台线程把「缓冲区宽度对齐窗口宽度」→ 折行发生在可见边缘，与 bat 一致
///     （控制台默认在缓冲区宽度处折行，缓冲区比窗口宽就会变成横向滚动）。
///   </item>
/// </list>
///
/// <para>
/// 由主进程用 <c>FatFish.exe --loghost-file &lt;文件&gt; &lt;唯一标题&gt;</c> 启动；
/// 标题用来让父进程用 <c>FindWindowW(ConsoleWindowClass, …)</c> 精确认领自己的窗口。
/// </para>
/// </summary>
internal static class LogHost
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const int StdOutputHandle = -11;

    /// <summary>缓冲区高度上限（回看空间）。</summary>
    private const int BufferMaxHeight = 9999;

    /// <summary>窗口上方至少保留多少行历史。</summary>
    private const int BufferKeepHeight = 400;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    /// <summary>
    /// 入口：一直跟到进程被杀（主进程收尾时会 taskkill）。
    /// 调用方负责在返回后退出进程。
    /// </summary>
    public static void Run(string filePath, string title)
    {
        // 哨兵：证明「--loghost-file 分支真的进来了」。
        // 自检失败时靠它区分「参数没送到 / 分支没进」和「分支进了但没窗口」。
        try
        {
            File.WriteAllText(
                filePath + ".started",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " pid=" + Environment.ProcessId);
        }
        catch
        {
            // 哨兵写不出来不影响主流程
        }

        // ★ 自己分配控制台。
        //   实测：WinExe（GUI 子系统）用 CREATE_NEW_CONSOLE 启动时，这台机器
        //   只给进程一个控制台对象，**不创建任何窗口**（整桌面 ConsoleWindowClass=0）；
        //   父进程带不带 SW_HIDE 都一样。AllocConsole 才是真正「要一个窗口」的调用 ——
        //   Python 版没这问题，因为它的宿主是 python.exe（console 子系统）。
        try
        {
            if (GetConsoleWindow() == IntPtr.Zero)
                AllocConsole();
        }
        catch
        {
            // 分配失败就只能靠 CREATE_NEW_CONSOLE 碰运气了
        }

        try
        {
            SetConsoleTitleW(title);
        }
        catch
        {
            // 标题设不上只影响查找，父进程会按超时降级
        }

        try
        {
            SetConsoleOutputCP(65001);
        }
        catch
        {
            // ignore
        }

        var conout = OpenConOut();

        // 打开 VT 处理：ANSI 色码由控制台自己解析
        try
        {
            if (GetConsoleMode(conout, out var mode))
                SetConsoleMode(conout, mode | EnableVirtualTerminalProcessing);
        }
        catch
        {
            // 老系统没有这个模式：不影响写，只是颜色会变成字面量
        }

        var stop = new ManualResetEventSlim(false);

        var reflow = new Thread(() => ReflowLoop(conout, stop))
        {
            IsBackground = true,
            Name = "fatfish-loghost-reflow",
        };

        reflow.Start();

        FollowLoop(conout, filePath);

        stop.Set();
    }

    private static IntPtr OpenConOut()
    {
        var handle = CreateFileW(
            "CONOUT$",
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            handle = GetStdHandle(StdOutputHandle);

        return handle;
    }

    /// <summary>
    /// 按字节偏移跟随文件（append-only）：有新增就写进控制台，没有就等。
    /// 文件被清空/重写（长度比上次小）时从头再来。
    /// </summary>
    private static void FollowLoop(IntPtr conout, string path)
    {
        var bytes = new byte[64 * 1024];
        var chars = new char[64 * 1024];
        var decoder = Encoding.UTF8.GetDecoder();
        long offset = 0;

        while (true)
        {
            try
            {
                if (File.Exists(path))
                {
                    using var fs = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                    if (fs.Length < offset)
                    {
                        offset = 0;
                        decoder.Reset();
                    }

                    if (fs.Length > offset)
                    {
                        fs.Seek(offset, SeekOrigin.Begin);

                        int read;

                        while ((read = fs.Read(bytes, 0, bytes.Length)) > 0)
                        {
                            var count = decoder.GetChars(bytes, 0, read, chars, 0);

                            if (count > 0)
                                WriteConsole(conout, chars, count);

                            offset += read;
                        }
                    }
                }
            }
            catch
            {
                // 主进程正在写同一文件：下一轮再读
            }

            Thread.Sleep(40);
        }
    }

    /// <summary>写一段字符。WriteConsoleW 单次有长度上限，分块送。</summary>
    private static void WriteConsole(IntPtr conout, char[] buffer, int length)
    {
        const int chunk = 8192;

        for (var offset = 0; offset < length; offset += chunk)
        {
            var size = Math.Min(chunk, length - offset);

            try
            {
                WriteConsoleW(conout, buffer, offset, (uint)size, out _, IntPtr.Zero);
            }
            catch
            {
                return;
            }
        }
    }

    /// <summary>把缓冲区宽度对齐窗口宽度 —— 折行发生在可见边缘（bat 手感）。</summary>
    private static void ReflowLoop(IntPtr conout, ManualResetEventSlim stop)
    {
        while (!stop.Wait(250))
        {
            try
            {
                if (!GetConsoleScreenBufferInfo(conout, out var info))
                    continue;

                var windowWidth = info.Window.Right - info.Window.Left + 1;
                var windowHeight = info.Window.Bottom - info.Window.Top + 1;

                if (windowWidth < 1 || windowHeight < 1)
                    continue;

                var wantHeight = (int)info.Size.Y;

                if (wantHeight < windowHeight + BufferKeepHeight)
                    wantHeight = Math.Min(windowHeight + BufferKeepHeight, BufferMaxHeight);

                if (info.Size.X != windowWidth || info.Size.Y != wantHeight)
                {
                    SetConsoleScreenBufferSize(conout, new Coord
                    {
                        X = (short)Math.Min(windowWidth, short.MaxValue),
                        Y = (short)Math.Min(wantHeight, short.MaxValue),
                    });
                }
            }
            catch
            {
                // 窗口正在重建时取尺寸会失败，等一下再来
            }
        }
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsoleW(
        IntPtr consoleOutput, char[] buffer, int startIndex, uint numberOfCharsToWrite,
        out uint numberOfCharsWritten, IntPtr reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr consoleHandle, uint mode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetConsoleTitleW(string title);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(
        IntPtr consoleOutput, out ConsoleScreenBufferInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleScreenBufferSize(IntPtr consoleOutput, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);
}
