using System.Diagnostics;
using System.Text;

namespace FatFish.Core.Tools;

/// <summary>
/// 后台任务管理器 —— <c>ws_bg_*</c> 五个工具的状态中枢。
///
/// 为什么不放在工具里：工具是「一次调用一个对象、调完就丢」的。
/// 而一件后台活要跨越很多次调用活着 —— 启动是一次调用，看进度是另一次，
/// 读输出是第三次，中止可能永远不会来。状态必须放在比工具活得久的地方。
///
/// 三处关键设计：
///
/// <b>1) 输出存**字节**，不存字符串。</b>
///    进程输出是分块到达的，一块可能正好切在「肥」字的第三个字节上。
///    如果每来一块就解码一次，那个字会被拆成两个 U+FFFD，
///    而且**永远不会自愈** —— 后面再补上也拼不回来。
///    存原始字节、读的时候再整体解码，就没有这个问题。
///
/// <b>2) 缓冲区只保留最近一段（默认 4MB），但 offset 是绝对位置。</b>
///    任务可能跑几小时、输出几百 MB，全留着会把内存吃光。
///    可是「只留尾部」会让 offset 失去意义 —— 所以 offset 用「从任务开始算起的总字节数」，
///    读取时若 offset 落在已丢弃的区间，就明确告诉调用方
///    <see cref="BackgroundTailResult.DroppedAtStart"/>，而不是悄悄从头给一遍
///    （那会让调用方以为自己漏读了，然后又读一遍，死循环）。
///
/// <b>3) 状态判定只看「谁取消的」和「退出码」，不信 work 的返回值。</b>
///    超时与 Kill 都会让子进程被杀，两者的退出码都是空/非零。
///    只有 KillRequested 这一个标记能把它们分开 ——
///    丢掉这个标记，一次用户主动中止就会被播报成「失败」，然后有人去查一个不存在的 bug。
/// </summary>
public sealed class BackgroundJobManager : IBackgroundJobManager, IDisposable
{
    /// <summary>单个任务保留多少字节的输出（滚动窗口）。</summary>
    public const int DefaultMaxBufferBytes = 4 * 1024 * 1024;

    /// <summary>最多同时保留多少个任务记录（含已结束的）。</summary>
    public const int DefaultMaxJobs = 32;

    /// <summary>单次 Tail 允许的最大字符数，防止一次拉进几十万字符。</summary>
    public const int HardMaxTailChars = 40_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly int _maxBufferBytes;
    private readonly int _maxJobs;
    private readonly Func<string> _idFactory;

    private int _seq;
    private bool _disposed;

    /// <summary>任务结束时触发（含 done / failed / timeout / killed）。</summary>
    public event Action<BackgroundJobSnapshot>? Finished;

    public BackgroundJobManager(
        int maxBufferBytes = DefaultMaxBufferBytes,
        int maxJobs = DefaultMaxJobs,
        Func<string>? idFactory = null)
    {
        if (maxBufferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBufferBytes), "缓冲区上限必须为正数");

        if (maxJobs <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxJobs), "任务保留数必须为正数");

        _maxBufferBytes = maxBufferBytes;
        _maxJobs = maxJobs;
        _idFactory = idFactory ?? (() => $"job_{DateTime.Now:MMdd_HHmmss}_{NextSeq():000}");
    }

    private int NextSeq() => Interlocked.Increment(ref _seq);

    // ------------------------------------------------------------------
    // 启动
    // ------------------------------------------------------------------

    public string Start(BackgroundJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ObjectDisposedException.ThrowIf(_disposed, this);

        var job = new Job(
            _idFactory(),
            request.Kind,
            request.Desc,
            request.TimeoutSeconds,
            _maxBufferBytes);

        lock (_gate)
        {
            _jobs[job.Id] = job;
            _order.Add(job.Id);

            TrimLocked();
        }

        // 超时由管理器统一控制：CancelAfter 之后 ct 会传到 ProcessRunner，
        // 它会杀掉整棵进程树。工具实现只需把 ct 原样传下去，不必各自算超时。
        if (request.TimeoutSeconds > 0)
            job.Cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        // 故意不 await：Start 必须立刻返回，否则「后台」二字没有意义。
        _ = RunAsync(job, request);

        return job.Id;
    }

    private async Task RunAsync(Job job, BackgroundJobRequest request)
    {
        var sw = Stopwatch.StartNew();
        int? exitCode = null;
        string? error = null;

        try
        {
            exitCode = await request.Work(job, job.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 超时 / Kill：都走这条路。具体是哪个由下面判定。
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        sw.Stop();

        string status;

        if (job.Cts.IsCancellationRequested)
        {
            status = job.KillRequested ? "killed" : "timeout";
        }
        else if (error is not null)
        {
            status = "failed";
        }
        else
        {
            status = exitCode == 0 ? "done" : "failed";
        }

        BackgroundJobSnapshot snapshot;

        lock (job.Lock)
        {
            job.Status = status;
            job.ExitCode = exitCode;
            job.Error = error;
            job.ElapsedMs = sw.ElapsedMilliseconds;
            job.EndedAt = DateTimeOffset.Now;
            snapshot = job.SnapshotLocked();
        }

        job.Cts.Dispose();

        try
        {
            Finished?.Invoke(snapshot);
        }
        catch (Exception)
        {
            // 订阅者（UI 播报）出错不能影响任务记录本身
        }
    }

    // ------------------------------------------------------------------
    // 查询
    // ------------------------------------------------------------------

    public IReadOnlyList<BackgroundJobSnapshot> List()
    {
        lock (_gate)
        {
            if (_order.Count == 0)
                return Array.Empty<BackgroundJobSnapshot>();

            var list = new List<BackgroundJobSnapshot>(_order.Count);

            foreach (var id in _order)
            {
                if (_jobs.TryGetValue(id, out var job))
                    list.Add(job.Snapshot());
            }

            return list;
        }
    }

    public BackgroundJobSnapshot? Get(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return null;

        lock (_gate)
        {
            return _jobs.TryGetValue(jobId.Trim(), out var job) ? job.Snapshot() : null;
        }
    }

    // ------------------------------------------------------------------
    // 增量读取
    // ------------------------------------------------------------------

    public BackgroundTailResult Tail(string jobId, long offset, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return new BackgroundTailResult(string.Empty, 0, 0);

        maxChars = Math.Clamp(maxChars, 1, HardMaxTailChars);

        Job? job;
        lock (_gate)
        {
            _jobs.TryGetValue(jobId.Trim(), out job);
        }

        if (job is null)
            return new BackgroundTailResult(string.Empty, 0, 0);

        if (offset < 0)
            offset = 0;

        // 按「最坏情况 4 字节一个字符」多取一些，之后按字符数精确截断，
        // 再由编码回算出真正消费了多少字节 —— 这样 next_offset 永远落在字符边界。
        var byteBudget = (int)Math.Min(maxChars * 4L + 4, 4L * 1024 * 1024);

        byte[] slice;
        long start;
        bool dropped;

        lock (job.Lock)
        {
            (slice, start, dropped) = job.Buffer.Slice(offset, byteBudget);
        }

        // 对齐起点：如果正好切在一个 UTF-8 字符中间，从起点跳过续字节。
        // 不做这一步，整段会出现一个 U+FFFD，而且 strict 解码失败会误判成 GBK，
        // 于是满屏中文变成乱码 —— 一个偏移量错误就能毁掉整次读取。
        var skip = 0;
        while (skip < slice.Length && (slice[skip] & 0xC0) == 0x80 && skip < 3)
            skip++;

        var body = skip == 0 ? slice : slice[skip..];
        var bodyStart = start + skip;

        var (text, encoding) = DecodeTail(body);

        long next;
        bool hasMore;

        if (text.Length > maxChars)
        {
            var cut = text[..maxChars];
            text = cut;

            // 用实际编码回算字节数，保证下次从这里接着读不会错位
            next = bodyStart + encoding.GetByteCount(cut);
            hasMore = true;
        }
        else
        {
            next = bodyStart + Math.Max(0, encoding.GetByteCount(text));
            hasMore = next < job.Buffer.Total;
        }

        return new BackgroundTailResult(text, next, job.Buffer.Total, dropped, hasMore);
    }

    /// <summary>
    /// 尾巴解码：优先严格 UTF-8（允许末尾缺一个字符），失败才按 OEM 代码页。
    ///
    /// 比 <see cref="ProcessEncoding.Decode(byte[], string?)"/> 多一步「砍掉末尾不完整字符再试」：
    /// 输出是边跑边读的，读到的最后几个字节很可能只是半个汉字。
    /// 直接用严格解码会整段失败并被误判成 GBK，结果满屏乱码 —— 但实际上它就是 UTF-8。
    /// </summary>
    private static (string Text, Encoding Encoding) DecodeTail(byte[] bytes)
    {
        if (bytes.Length == 0)
            return (string.Empty, Encoding.UTF8);

        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // 末尾最多缺 3 个字节（UTF-8 字符最长 4 字节）
        for (var cut = 0; cut <= 3 && cut < bytes.Length; cut++)
        {
            try
            {
                var text = strict.GetString(bytes, 0, bytes.Length - cut);
                return (text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text, Encoding.UTF8);
            }
            catch (DecoderFallbackException)
            {
                // 试试少一个字节
            }
        }

        var oem = ProcessEncoding.Oem;
        return (oem.GetString(bytes), oem);
    }

    // ------------------------------------------------------------------
    // 中止
    // ------------------------------------------------------------------

    public bool Kill(string jobId, string reason)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return false;

        Job? job;
        lock (_gate)
        {
            _jobs.TryGetValue(jobId.Trim(), out job);
        }

        if (job is null)
            return false;

        lock (job.Lock)
        {
            if (job.Status != StatusRunning)
                return false;

            // ★ 先立标记再取消：RunAsync 结束时靠这个标记区分「用户中止」与「超时」，
            //   顺序反了就有极小概率把一次 Kill 报成 timeout。
            job.KillRequested = true;
            job.Buffer.Append(Encoding.UTF8.GetBytes(
                $"\n[已请求中止：{(string.IsNullOrWhiteSpace(reason) ? "用户操作" : reason.Trim())}]\n"));
        }

        try
        {
            job.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务恰好结束并释放了 CTS
        }
        catch (InvalidOperationException)
        {
        }

        return true;
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    public const string StatusRunning = "running";

    /// <summary>
    /// 超过上限时丢掉最早的**已结束**任务。
    /// 只丢已结束的：正在跑的任务从列表里消失，用户就没法 tail、没法 kill 了 ——
    /// 而它还在占着 CPU，变成一个看不见的进程。
    /// </summary>
    private void TrimLocked()
    {
        while (_order.Count > _maxJobs)
        {
            var victim = -1;

            for (var i = 0; i < _order.Count; i++)
            {
                if (_jobs.TryGetValue(_order[i], out var j) && j.Status != StatusRunning)
                {
                    victim = i;
                    break;
                }
            }

            if (victim < 0)
                break;      // 全都在跑，宁可超限也不丢

            _jobs.Remove(_order[victim]);
            _order.RemoveAt(victim);
        }
    }

    public void Dispose()
    {
        List<Job> running;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;

            running = _order
                .Where(id => _jobs.TryGetValue(id, out var j) && j.Status == StatusRunning)
                .Select(id => _jobs[id])
                .ToList();
        }

        // 退出程序时把还在跑的任务一并掐掉，否则它们会成为孤儿进程继续占用 CPU
        foreach (var job in running)
        {
            lock (job.Lock)
            {
                job.KillRequested = true;
            }

            try
            {
                job.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>一个后台任务的全部可变状态。</summary>
    private sealed class Job : IBackgroundJobHandle
    {
        public Job(string id, string kind, string desc, int timeoutSeconds, int bufferBytes)
        {
            Id = id;
            Kind = kind;
            Desc = desc;
            TimeoutSeconds = timeoutSeconds;
            Buffer = new ByteWindow(bufferBytes);
        }

        public readonly object Lock = new();

        public string Id { get; }
        public string Kind { get; }
        public string Desc { get; }
        public int TimeoutSeconds { get; }

        public readonly ByteWindow Buffer;
        public readonly CancellationTokenSource Cts = new();

        public string Status = StatusRunning;
        public bool KillRequested;
        public int? ExitCode;
        public string? Error;
        public long ElapsedMs;
        public DateTimeOffset StartedAt = DateTimeOffset.Now;
        public DateTimeOffset? EndedAt;

        public void Append(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
                return;

            lock (Lock)
            {
                Buffer.Append(bytes);
            }
        }

        public void Note(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var line = text.EndsWith('\n') ? text : text + "\n";
            Append(Encoding.UTF8.GetBytes(line));
        }

        public BackgroundJobSnapshot Snapshot()
        {
            lock (Lock)
            {
                return SnapshotLocked();
            }
        }

        public BackgroundJobSnapshot SnapshotLocked() => new(
            Id,
            Kind,
            Desc,
            Status,
            StartedAt,
            Buffer.Total,
            ElapsedMs > 0 ? ElapsedMs : (long)(DateTimeOffset.Now - StartedAt).TotalMilliseconds,
            ExitCode,
            Error,
            TimeoutSeconds,
            EndedAt);
    }

    /// <summary>
    /// 只增不减的字节缓冲，超过容量时丢弃最早的一段（保留最近 3/4）。
    ///
    /// 一次丢到 3/4 而不是丢一点点：每次 compact 都要把剩余数据整体搬一遍，
    /// 每来一块就搬一次的话，输出量大的任务会在这上面耗掉可观的时间。
    /// </summary>
    private sealed class ByteWindow
    {
        private readonly int _cap;
        private readonly MemoryStream _ms = new();
        private long _dropped;

        public ByteWindow(int cap) => _cap = cap;

        /// <summary>累计写入的字节数（含已丢弃部分）—— tail 的 offset 就是它。</summary>
        public long Total => _dropped + _ms.Length;

        public void Append(ReadOnlySpan<byte> data)
        {
            _ms.Write(data);

            if (_ms.Length > _cap)
                Compact();
        }

        private void Compact()
        {
            var all = _ms.ToArray();
            var keep = _cap * 3 / 4;
            var drop = all.Length - keep;

            if (drop <= 0)
                return;

            _ms.SetLength(0);
            _ms.Write(all, drop, keep);
            _dropped += drop;
        }

        public (byte[] Slice, long Start, bool Dropped) Slice(long offset, int maxBytes)
        {
            var total = Total;
            var start = Math.Max(offset, _dropped);
            var dropped = offset < _dropped;

            if (start >= total || maxBytes <= 0)
                return (Array.Empty<byte>(), start, dropped);

            var len = (int)Math.Min(maxBytes, total - start);
            var buf = new byte[len];

            _ms.Position = start - _dropped;
            _ = _ms.Read(buf, 0, len);

            return (buf, start, dropped);
        }
    }
}
