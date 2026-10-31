using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FatFish.Core.Tools;

/// <summary>
/// 一次子进程运行的完整结果。
/// </summary>
public sealed record ProcessRunResult
{
    /// <summary>进程真跑起来了，但没能启动（文件名错、权限不足…）时的原因；否则为 null。</summary>
    public string? StartError { get; init; }

    /// <summary>退出码。超时/启动失败时为 null。</summary>
    public int? ExitCode { get; init; }

    /// <summary>因超时被杀（含子进程）。</summary>
    public bool TimedOut { get; init; }

    /// <summary>因调用方的 CancellationToken 被取消。</summary>
    public bool Cancelled { get; init; }

    /// <summary>已解码、已去除 ANSI 转义的 stdout。</summary>
    public string Stdout { get; init; } = string.Empty;

    public string Stderr { get; init; } = string.Empty;

    /// <summary>输出超过上限被截断（保留的是开头部分）。</summary>
    public bool Truncated { get; init; }

    /// <summary>stdout 的原始总字节数（截断前）。</summary>
    public long StdoutBytes { get; init; }

    public long StderrBytes { get; init; }

    public long ElapsedMs { get; init; }

    public bool Ok => StartError is null && ExitCode == 0 && !TimedOut && !Cancelled;
}

/// <summary>
/// 子进程执行与输出收集 —— <c>ws_run_cmd</c> / <c>ws_run_python</c> / <c>ws_bg_*</c> 共用的唯一实现。
///
/// 为什么不各写一份：这段代码里有三个坑，每一个都不会在「跑个 echo hello」时暴露，
/// 而是在别的工具里以「卡死」「乱码」「杀不干净」的形式出现，且极难定位。
/// 抄第二份的时候，通常只会抄走顺眼的那一半。
///
/// 三个坑（全部是实测结论，不是查文档猜的）：
///
/// <b>1) 必须拿原始字节，不能设 StandardOutputEncoding。</b>
///    中文 Windows 的 cmd 内建命令按 OEM 代码页（936）写字节，而 git / python / node 写 UTF-8，
///    同一个进程里混着两种编码是常态。一旦设了 StandardOutputEncoding，
///    .NET 就直接给解码后的字符串，原始字节没了，再也没法「先严格 UTF-8、失败退 GBK」。
///    所以这里一律读 BaseStream，解码交给 <see cref="ProcessEncoding"/>。
///
/// <b>2) 输出限流时仍必须读到 EOF。</b>
///    只读够上限就停手，子进程写满管道缓冲区后会永久阻塞在 write 上、永不退出，
///    工具调用随之挂死。所以超出部分要「读掉并丢弃」，见 <see cref="PumpAsync"/>。
///
/// <b>3) 超时必须杀整棵进程树。</b>
///    只杀 cmd/python 本身，它起的子进程会成为孤儿并继续持有输出管道，
///    读线程永远等不到 EOF，整个工具调用挂死。而且杀完还要给管道一个排空上限
///    （<see cref="DrainTimeout"/>），万一有孙进程握着句柄也不能永久等待。
///
/// 两种收取方式，共用上面这一套：
///   · <see cref="RunAsync"/>          —— 一次性，跑完把（截断后的）输出解好码返回；
///   · <see cref="RunStreamingAsync"/> —— 边跑边把**全部原始字节**推给回调，不在本层保留。
///     后台任务用它：ws_bg_tail 要能在进程还没结束时就看到输出，
///     一次性收取意味着「要么看见全部、要么什么也看不见」。
/// </summary>
public static class ProcessRunner
{
    /// <summary>stdout / stderr 各自默认最多保留多少**字节**（解码前的原始字节）。</summary>
    public const int DefaultMaxOutputBytes = 24 * 1024;

    /// <summary>杀进程后等待输出管道排空的上限。</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// ANSI 转义序列（颜色、光标移动等）。
    /// 有些程序即使输出被重定向也照发不误，直接把 <c>\x1b[31m</c> 塞进模型上下文，
    /// 既不可读又白费 token。
    /// </summary>
    private static readonly Regex AnsiPattern = new(
        @"\x1B(?:\[[0-9;?]*[ -/]*[@-~]|[@-Z\\-_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 启动并等待一个进程，收集输出。
    /// </summary>
    /// <param name="psi">
    /// 进程参数。<c>UseShellExecute</c> / <c>CreateNoWindow</c> / 两个重定向会被强制设置 ——
    /// 调用方不需要重复写，也写不错。
    /// </param>
    /// <param name="timeout">超时。到点杀整棵树。</param>
    /// <param name="encodingMode">解码方式，见 <see cref="ProcessEncoding.Decode"/>。</param>
    /// <param name="maxOutputBytes">stdout / stderr 各自的保留上限（字节）。</param>
    /// <param name="log">可选的日志回调（例如把「正在执行什么」推到 UI）。</param>
    /// <param name="ct">取消信号。取消与超时会分别标记（Cancelled / TimedOut）。</param>
    public static async Task<ProcessRunResult> RunAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        string? encodingMode = null,
        int maxOutputBytes = DefaultMaxOutputBytes,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(psi);

        if (maxOutputBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes), "输出上限必须为正数");

        Prepare(psi);

        var sw = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };

        if (!TryStart(process, sw, out var failed))
            return failed!;

        log?.Invoke(psi.FileName + " " + psi.Arguments);

        // 读流时不传 ct：超时后要先杀进程让管道 EOF，再收数据。
        // 传了 ct 会把已经读到的内容一起丢掉 —— 恰恰是排查超时最需要的那部分。
        var outTask = PumpAsync(process.StandardOutput.BaseStream, maxOutputBytes, sink: null);
        var errTask = PumpAsync(process.StandardError.BaseStream, maxOutputBytes, sink: null);

        var exit = await AwaitExitAsync(process, timeout, ct).ConfigureAwait(false);

        // 收尾：等两个读任务把管道排空。给一个上限，
        // 万一有孙进程还握着句柄，也不能让工具调用永久挂住。
        await DrainAsync(outTask, errTask).ConfigureAwait(false);

        sw.Stop();

        var (outBytes, outTotal) = outTask.IsCompletedSuccessfully
            ? outTask.Result
            : (Array.Empty<byte>(), 0L);

        var (errBytes, errTotal) = errTask.IsCompletedSuccessfully
            ? errTask.Result
            : (Array.Empty<byte>(), 0L);

        return new ProcessRunResult
        {
            ExitCode = exit.ExitCode,
            TimedOut = exit.TimedOut,
            Cancelled = exit.Cancelled,
            Stdout = Clean(ProcessEncoding.Decode(outBytes, encodingMode)),
            Stderr = Clean(ProcessEncoding.Decode(errBytes, encodingMode)),
            Truncated = outTotal > maxOutputBytes || errTotal > maxOutputBytes,
            StdoutBytes = outTotal,
            StderrBytes = errTotal,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// 启动并等待一个进程，把**全部**原始输出边产生边推给回调。
    ///
    /// 与 <see cref="RunAsync"/> 的差别只有一处：本层不做截断、不解码、不保留。
    /// 后台任务需要一个只增不减的完整输出流（它自己有容量上限），
    /// 而 RunAsync 的语义是「给我一个能塞进上下文的结果」，两者目标不同。
    ///
    /// ★ 回调收到的 <see cref="ReadOnlyMemory{T}"/> 指向**复用的内部缓冲区**，
    ///   必须在这回调里就复制走。想在回调里存引用、等会儿再读，会读到后面几块的内容。
    ///   （这个约定写在这里，是因为它错了不会报错，只会看到错乱重复的输出。）
    /// </summary>
    public static async Task<ProcessRunResult> RunStreamingAsync(
        ProcessStartInfo psi,
        TimeSpan? timeout,
        Action<ReadOnlyMemory<byte>>? onStdout = null,
        Action<ReadOnlyMemory<byte>>? onStderr = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(psi);

        Prepare(psi);

        var sw = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };

        if (!TryStart(process, sw, out var failed))
            return failed!;

        log?.Invoke(psi.FileName + " " + psi.Arguments);

        var outTask = PumpAsync(process.StandardOutput.BaseStream, cap: 0, onStdout);
        var errTask = PumpAsync(process.StandardError.BaseStream, cap: 0, onStderr);

        var exit = await AwaitExitAsync(process, timeout, ct).ConfigureAwait(false);

        await DrainAsync(outTask, errTask).ConfigureAwait(false);

        sw.Stop();

        return new ProcessRunResult
        {
            ExitCode = exit.ExitCode,
            TimedOut = exit.TimedOut,
            Cancelled = exit.Cancelled,
            Stdout = string.Empty,
            Stderr = string.Empty,
            Truncated = false,
            StdoutBytes = outTask.IsCompletedSuccessfully ? outTask.Result.Total : 0,
            StderrBytes = errTask.IsCompletedSuccessfully ? errTask.Result.Total : 0,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    // ------------------------------------------------------------------
    // 启动 / 等待 / 排空
    // ------------------------------------------------------------------

    /// <summary>
    /// 强制设置这几个开关，而不是检查。
    /// 凡是漏一个，行为就会以「拿不到输出」或「弹出控制台窗口」的形式诡异失败，
    /// 而错误现场离真正原因很远。
    /// </summary>
    private static void Prepare(ProcessStartInfo psi)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = false;
    }

    /// <summary>启动进程。失败时通过 <paramref name="failed"/> 返回一个只带错误的结果。</summary>
    private static bool TryStart(Process process, Stopwatch sw, out ProcessRunResult? failed)
    {
        failed = null;

        try
        {
            if (!process.Start())
            {
                sw.Stop();
                failed = new ProcessRunResult
                {
                    StartError = "进程启动失败（Process.Start 返回 false）",
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            sw.Stop();
            failed = new ProcessRunResult
            {
                StartError = $"进程启动失败：{ex.GetType().Name}: {ex.Message}",
                ElapsedMs = sw.ElapsedMilliseconds,
            };
            return false;
        }
    }

    /// <summary>一次等待的结果（不含耗时，耗时由调用方在排空后测）。</summary>
    private readonly record struct ExitInfo(bool TimedOut, bool Cancelled, int? ExitCode);

    /// <summary>
    /// 等进程退出；超时或取消时**杀整棵进程树**。
    ///
    /// 这里是「杀树」逻辑的唯一实现：RunAsync 与 RunStreamingAsync 都走它。
    /// 拆成两份写过一次，第二份就忘了整个树 —— 症状是工具调用偶尔永久挂住。
    /// </summary>
    private static async Task<ExitInfo> AwaitExitAsync(
        Process process, TimeSpan? timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (timeout is { } t)
            timeoutCts.CancelAfter(t);

        var timedOut = false;
        var cancelled = false;

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = ct.IsCancellationRequested;
            timedOut = !cancelled;

            try
            {
                // ★ 必须杀整棵树：只杀父进程会留下孤儿握着管道
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程已经自己退出了
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 权限不足等，忽略：下面照样收已有输出
            }
        }

        int? exitCode = null;
        try
        {
            if (process.HasExited)
                exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // 没起来或已释放
        }

        return new ExitInfo(timedOut, cancelled, exitCode);
    }

    /// <summary>
    /// 等两个读任务把管道排空，最多等 <see cref="DrainTimeout"/>。
    ///
    /// 用 WhenAny + Delay 而不是直接 await：`WaitForExit` 只保证**本进程**退出，
    /// 若它起的孙进程还握着管道写端，读任务就永远等不到 EOF。
    /// 这时宁可丢掉尾部输出，也不能让整个工具调用挂死。
    /// </summary>
    private static async Task DrainAsync(Task a, Task b)
    {
        var drained = Task.WhenAll(a, b);
        await Task.WhenAny(drained, Task.Delay(DrainTimeout, CancellationToken.None))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 读流：最多保留 <paramref name="cap"/> 字节，同时把**每一块原始字节**推给
    /// <paramref name="sink"/>（若提供）。
    ///
    /// ★ 关键点：保留量有上限，但**必须一直读到 EOF**。
    ///   如果读够 cap 就停手，子进程写满管道缓冲区后会阻塞在 write 上，
    ///   永远不退出 —— 工具调用随之挂死。所以超出部分要「读掉并丢弃」。
    /// </summary>
    private static async Task<(byte[] Bytes, long Total)> PumpAsync(
        Stream stream, int cap, Action<ReadOnlyMemory<byte>>? sink)
    {
        using var kept = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;

        while (true)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;      // 进程被杀时管道可能直接断掉
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (read <= 0)
                break;

            total += read;

            if (sink is not null)
            {
                try
                {
                    sink(buffer.AsMemory(0, read));
                }
                catch (Exception)
                {
                    // ★ 回调出错绝不能中断读取循环：那会让子进程写满管道后永久阻塞。
                    //   丢掉这个 sink，继续把剩余输出读干净。
                    sink = null;
                }
            }

            var room = cap - (int)kept.Length;
            if (room > 0)
                kept.Write(buffer, 0, Math.Min(read, room));
        }

        return (kept.ToArray(), total);
    }

    /// <summary>去掉 ANSI 转义、归一化换行、去掉尾部空行。</summary>
    private static string Clean(string text)
    {
        if (text.Length == 0)
            return string.Empty;

        var stripped = AnsiPattern.Replace(text, string.Empty);
        return Workspace.WorkspaceFilters.Normalize(stripped);
    }
}
