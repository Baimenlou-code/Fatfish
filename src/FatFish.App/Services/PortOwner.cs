using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FatFish.App.Services;

/// <summary>
/// 端口 → 占用它的进程 PID，以及杀进程树。
///
/// <para>
/// <b>为什么不用 wmic / netstat / PowerShell / taskkill：</b>
/// </para>
/// <list type="bullet">
///   <item>wmic 在 Windows 11 已默认移除（只是可选的按需功能），调它随时会失败；</item>
///   <item>起一次 <c>powershell -Command Get-CimInstance Win32_Process</c> 要 700ms~3s，
///         还依赖执行策略、用户配置文件、PSReadLine 等一堆无关东西；</item>
///   <item>taskkill.exe 是外部进程，且一次只处理一个 PID，N 个进程就要起 N 次。</item>
/// </list>
///
/// <para>
/// <b>这里怎么做：</b>
/// 直接调 <c>iphlpapi.dll!GetExtendedTcpTable</c> —— 就是 netstat 自己用的那个 API，
/// 一次拿到整张 TCP 连接表，纯内存解析，实测 5~15ms，不创建任何外部进程。
/// 拿到 PID 后用 .NET 自带的 <c>Process.Kill(entireProcessTree: true)</c> 杀整棵子树
/// （.NET 5+ 内置，内部走 ToolHelp32 快照，等价于 <c>taskkill /T</c>，但不用起进程）。
/// </para>
///
/// <para>
/// 平台说明：<c>GetExtendedTcpTable</c> 只存在于 Windows，所以本文件整体是 Windows 专用的。
/// 项目本身就是 net10.0-windows 的 WPF 程序，不需要跨平台。
/// </para>
/// </summary>
internal static class PortOwner
{
    // ---- GetExtendedTcpTable 用到的常量（ctdef.h / iprtrmib.h）----
    private const int AfInet = 2;          // AF_INET
    private const int AfInet6 = 23;        // AF_INET6
    private const int TcpTableOwnerPidAll = 5;  // TCP_TABLE_OWNER_PID_ALL
    private const int MibTcpStateListen = 2;    // MIB_TCP_STATE_LISTEN
    private const int ErrorInsufficientBuffer = 122;

    // 表头是 1 个 DWORD（行数），行从偏移 4 开始。
    private const int HeaderSize = 4;

    // MIB_TCPROW_OWNER_PID：state/localAddr/localPort/remoteAddr/remotePort/owningPid，6×DWORD
    private const int RowSizeV4 = 24;
    private const int PortOffsetV4 = 8;
    private const int StateOffsetV4 = 0;
    private const int PidOffsetV4 = 20;

    // MIB_TCP6ROW_OWNER_PID：localAddr[16]/scope/localPort/remoteAddr[16]/scope/remotePort/state/pid
    private const int RowSizeV6 = 56;
    private const int PortOffsetV6 = 20;
    private const int StateOffsetV6 = 48;
    private const int PidOffsetV6 = 52;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);

    /// <summary>
    /// 查询正在<b>监听</b>指定端口的进程 PID（IPv4 与 IPv6 都查）。
    /// 查不到返回空列表；调用失败也返回空列表（不抛异常 —— 这是探测路径，
    /// 不该因为 API 抽风就把停插件这件事整体搞崩）。
    /// </summary>
    public static IReadOnlyList<int> ListeningPids(int port)
    {
        var pids = new HashSet<int>();

        byte[]? v4 = QueryTable(AfInet);
        if (v4 is not null)
            Collect(v4, RowSizeV4, PortOffsetV4, StateOffsetV4, PidOffsetV4, port, pids);

        byte[]? v6 = QueryTable(AfInet6);
        if (v6 is not null)
            Collect(v6, RowSizeV6, PortOffsetV6, StateOffsetV6, PidOffsetV6, port, pids);

        return new List<int>(pids);
    }

    /// <summary>把 PID 变成一个便于我看日志的名字，例如 <c>node(12345)</c>。取不到就只回 PID。</summary>
    public static string Describe(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.ProcessName + "(" + pid + ")";
        }
        catch
        {
            return "PID " + pid;
        }
    }

    /// <summary>
    /// 杀掉进程及其整棵子树（等价 <c>taskkill /F /T /PID</c>，但不需要外部进程）。
    /// 返回 true 表示至少发出过一次成功的终止；进程本来就没了也算 true（目标达成）。
    /// </summary>
    public static bool KillTree(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using Process proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(4000);
            return true;
        }
        catch (ArgumentException)
        {
            // 进程在这几毫秒里自己退出了：目标达成。
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            // 最常见的是 Win32Exception：拒绝访问（不是本用户启的进程、或需要提权）。
            return false;
        }
    }

    // ---------------------------------------------------------------- 内部解析

    private static void Collect(
        byte[] table,
        int rowSize,
        int portOffset,
        int stateOffset,
        int pidOffset,
        int port,
        HashSet<int> pids)
    {
        int rows = BitConverter.ToInt32(table, 0);
        // 防御：API 返回的行数与缓冲区长度不一致时，按缓冲区能容纳的行数截断。
        int maxRows = (table.Length - HeaderSize) / rowSize;
        if (rows > maxRows)
            rows = maxRows;
        if (rows <= 0)
            return;

        for (int i = 0; i < rows; i++)
        {
            int baseOffset = HeaderSize + i * rowSize;

            int state = BitConverter.ToInt32(table, baseOffset + stateOffset);
            if (state != MibTcpStateListen)
                continue;   // 只认 LISTEN：ESTABLISHED 行的 localPort 是服务端口，但 pid 归属要看监听者

            int rawPort = BitConverter.ToInt32(table, baseOffset + portOffset);
            if (NetToHostPort(rawPort) != port)
                continue;

            int pid = BitConverter.ToInt32(table, baseOffset + pidOffset);
            if (pid > 0)
                pids.Add(pid);
        }
    }

    /// <summary>
    /// dwLocalPort 的低 16 位是<b>网络字节序</b>的端口。
    /// 5700 = 0x1644 会被读成 0x4416，交换两个字节还原。
    /// </summary>
    private static int NetToHostPort(int raw)
    {
        int low = raw & 0xFFFF;
        return ((low & 0xFF) << 8) | ((low >> 8) & 0xFF);
    }

    private static byte[]? QueryTable(int addressFamily)
    {
        int size = 0;
        uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TcpTableOwnerPidAll, 0);

        // 正常流程：第一次调用只问需要多大，必然返回 ERROR_INSUFFICIENT_BUFFER。
        if (rc != ErrorInsufficientBuffer && rc != 0)
            return null;
        if (size <= 0)
            return null;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedTcpTable(buffer, ref size, false, addressFamily, TcpTableOwnerPidAll, 0);
            if (rc != 0)
                return null;

            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
