using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FatFish.Core.Safety;

/// <summary>一段已读的行区间（1-based，闭区间）。</summary>
public readonly record struct LineRange(int From, int To)
{
    public bool IsEmpty => To < From;

    public int Length => IsEmpty ? 0 : To - From + 1;

    public bool Contains(int line) => !IsEmpty && line >= From && line <= To;

    /// <summary>两段是否重叠或相邻（相邻也算可合并：1-50 与 51-100 是连续读下来的）。</summary>
    public bool Touches(LineRange other)
        => !IsEmpty && !other.IsEmpty && other.From <= To + 1 && From <= other.To + 1;

    public LineRange Merge(LineRange other)
        => IsEmpty ? other
         : other.IsEmpty ? this
         : new LineRange(Math.Min(From, other.From), Math.Max(To, other.To));

    public override string ToString() => $"{From}-{To}";
}

/// <summary>只读的覆盖率快照，供审批文案与日志用。</summary>
/// <param name="ReadLines">已覆盖多少行（区间合并后）。</param>
/// <param name="TotalLines">读取时文件的总行数。</param>
/// <param name="Complete">是否已覆盖 1..TotalLines。</param>
public sealed record ReadCoverage(int ReadLines, int TotalLines, bool Complete)
{
    public override string ToString()
        => Complete ? $"已完整读取（{TotalLines} 行）" : $"只读了 {ReadLines}/{TotalLines} 行";
}

/// <summary>
/// 一张读凭证。
/// </summary>
/// <param name="Path">绝对路径（规范化后）。</param>
/// <param name="Fingerprint">内容指纹；null 表示路径当前不存在。</param>
/// <param name="IssuedAt">签发时间。</param>
/// <param name="TotalLines">读取时的总行数（0 = 空文件或未知）。</param>
/// <param name="Complete">是否已覆盖全文。</param>
/// <param name="Ranges">已读的行区间（已合并、按 From 升序）。</param>
public sealed record ReadTicket(
    string Path,
    string? Fingerprint,
    DateTimeOffset IssuedAt,
    int TotalLines = 0,
    bool Complete = true,
    IReadOnlyList<LineRange>? Ranges = null);

/// <summary>
/// ReadTicketStore —— 「读凭证」机制。
///
/// 设计意图（继承自 Python 版 workspace.py 的 _READ_TICKETS / _issue_ticket / _check_ticket）：
///   修改类工具（write / append / replace / delete）必须先成功 ws_read 过目标文件，
///   才能对同一文件动手。这样能防住「AI 凭记忆改一个自己没看过的文件」。
///
/// 本实现比 Python 版更严格一点：凭证绑定**内容指纹**而非仅路径。
/// 文件在读取后被外部进程改过，凭证立即失效，必须重读 —— 避免「读了旧版、改出新冲突」。
///
/// ★ 2026-11-05 扩展：凭证现在**记录已读的行区间**。
///   起因是 ws_read 开始支持 offset/limit 分页（读大文件时一次只取一页），
///   而原凭证只有「读过 / 没读过」两态 —— 于是「只读了第 1-100 行」和
///   「通读了 1200 行」拿到的是同一张许可，全量覆盖会把没看过的一千多行直接抹掉。
///   现在凭证能回答「读了哪几段」，写入侧据此分档：
///     · ws_replace 只需要「要改的那段在已读区间内」
///     · ws_write    需要「未读区间一个字节都没动」（由逐行 diff 证明）
///     · 证明不了才退回要求通读
///   ★ Issue(absolutePath) 的旧语义保持不变：视为**整份已读**，Complete = true。
/// </summary>
public sealed class ReadTicketStore
{
    /// <summary>一张凭证的内部状态（区间是可追加的，所以与对外的不可变 record 分开）。</summary>
    private sealed class Entry
    {
        public string? Fingerprint;
        public DateTimeOffset IssuedAt;
        public int TotalLines;

        /// <summary>通过 Issue(path) 签发的「整份读取」——不记区间，直接算完整。</summary>
        public bool WholeFile;

        public readonly List<LineRange> Ranges = new();
    }

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    private readonly PathGuard _guard;

    public ReadTicketStore(PathGuard guard)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    /// <summary>当前已签发的凭证数量（用于状态展示）。</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// 签发凭证（**整份已读**）。调用方应在此**之前**已成功读取文件全部内容。
    /// </summary>
    /// <param name="absolutePath">绝对路径，必须在工作台内。</param>
    public ReadTicket Issue(string absolutePath)
    {
        var full = EnsureInside(absolutePath);

        lock (_gate)
        {
            var entry = new Entry
            {
                Fingerprint = ComputeFingerprint(full),
                IssuedAt = DateTimeOffset.Now,
                WholeFile = true,
            };

            _entries[full] = entry;
            return Snapshot(full, entry);
        }
    }

    /// <summary>
    /// 签发凭证（**只读了某几行**）。同一指纹下多次调用会把区间合并起来。
    /// </summary>
    /// <param name="absolutePath">绝对路径，必须在工作台内。</param>
    /// <param name="totalLines">读取时文件的总行数（从 1 开始计数；空文件传 0）。</param>
    /// <param name="fromLine">本次读到的起始行（1-based，闭区间）。</param>
    /// <param name="toLine">本次读到的结束行（1-based，闭区间）。</param>
    public ReadTicket Issue(string absolutePath, int totalLines, int fromLine, int toLine)
    {
        var full = EnsureInside(absolutePath);

        lock (_gate)
        {
            var fingerprint = ComputeFingerprint(full);

            if (!_entries.TryGetValue(full, out var entry)
                || !string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                // 新文件，或者内容变了（旧区间已作废）
                entry = new Entry { Fingerprint = fingerprint, IssuedAt = DateTimeOffset.Now };
                _entries[full] = entry;
            }

            entry.TotalLines = Math.Max(entry.TotalLines, totalLines);
            entry.IssuedAt = DateTimeOffset.Now;

            if (toLine >= fromLine && fromLine >= 1)
                entry.Ranges.Add(new LineRange(fromLine, toLine));

            return Snapshot(full, entry);
        }
    }

    /// <summary>
    /// 校验凭证是否仍然有效（存在且内容未变）。
    /// </summary>
    public bool IsValid(string absolutePath)
    {
        if (!_guard.IsInside(absolutePath))
            return false;

        var full = Path.GetFullPath(absolutePath);

        if (!_entries.TryGetValue(full, out var entry))
            return false;

        var current = ComputeFingerprint(full);

        // 两者都为 null = 文件本来就不存在，凭证对「新建文件」依然有效
        if (entry.Fingerprint is null && current is null)
            return true;

        return string.Equals(entry.Fingerprint, current, StringComparison.Ordinal);
    }

    /// <summary>
    /// 校验并给出人性化失败原因（供工具层直接拼进错误消息）。
    /// </summary>
    public bool Check(string absolutePath, out string error)
    {
        var full = Path.GetFullPath(absolutePath);

        if (!_entries.ContainsKey(full))
        {
            error = $"没有读凭证：请先读取 {_guard.ToRelative(full)} 再修改";
            return false;
        }

        if (!IsValid(full))
        {
            error = $"{_guard.ToRelative(full)} 在读取后已被外部改动，凭证失效，请重新读取";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 校验「能不能整份覆盖」——除了凭证有效，还要求**已覆盖全文**。
    /// </summary>
    /// <remarks>
    /// ★ 全量覆盖会丢弃未读内容，所以它需要比 ws_replace 更强的许可。
    ///   但「必须通读」是最后手段，不是第一选择：真正的判据是
    ///   「这次改动有没有碰到未读区间」（由逐行 diff 证明，见写入侧）。
    ///   本方法只给出最保守的那一档答案。
    /// </remarks>
    public bool CheckForFullRewrite(string absolutePath, out string error)
    {
        if (!Check(absolutePath, out error))
            return false;

        var coverage = Coverage(absolutePath);

        if (coverage is { Complete: true })
            return true;

        var full = Path.GetFullPath(absolutePath);

        error = coverage is null
            ? $"没有读凭证：请先读取 {_guard.ToRelative(full)} 再修改"
            : $"{_guard.ToRelative(full)} 只读了 {coverage.ReadLines}/{coverage.TotalLines} 行，" +
              "全量覆盖会丢弃未读部分；请继续读（ws_read 的 offset/limit）或改用 ws_replace 做局部修改";

        return false;
    }

    /// <summary>当前文件的覆盖率快照；没有凭证时返回 null。</summary>
    public ReadCoverage? Coverage(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);

        if (!_entries.TryGetValue(full, out var entry))
            return null;

        lock (_gate)
        {
            var merged = MergeRanges(entry.Ranges);
            var complete = entry.WholeFile || entry.TotalLines <= 0 || Covers(merged, entry.TotalLines);
            var read = complete ? Math.Max(entry.TotalLines, 0) : CountLines(merged);

            return new ReadCoverage(read, entry.TotalLines, complete);
        }
    }

    /// <summary>
    /// 判断这些行是否都落在已读区间内 —— 供写入侧证明「改动只碰了读过的部分」。
    /// </summary>
    /// <param name="firstUnread">第一个未读行号；全部读过时为 0。</param>
    /// <remarks>
    /// ★ 这是 ws_replace 那道闸的唯一依据：全量覆盖靠逐行 diff 证明，
    ///   而精确替换只需要「被替换的那几行你确实看过」。
    /// ★ 已完整读取（WholeFile，或区间合并后覆盖全篇）时恒为 true ——
    ///   所以「读全文再改」的老路径完全不受影响，只有分页读之后才会触发本判据。
    /// </remarks>
    public bool AreLinesCovered(string absolutePath, IReadOnlyList<int> lines, out int firstUnread)
    {
        firstUnread = 0;

        if (lines.Count == 0)
            return true;

        var full = Path.GetFullPath(absolutePath);

        if (!_entries.TryGetValue(full, out var entry))
        {
            firstUnread = lines[0];
            return false;
        }

        lock (_gate)
        {
            var merged = MergeRanges(entry.Ranges);
            var complete = entry.WholeFile || entry.TotalLines <= 0 || Covers(merged, entry.TotalLines);

            if (complete)
                return true;

            foreach (var line in lines)
            {
                var hit = false;

                foreach (var r in merged)
                {
                    if (r.Contains(line))
                    {
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                {
                    firstUnread = line;
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// 返回**未读**的连续行块（1-based 闭区间），用于 ws_write 的全量覆盖证明。
    /// </summary>
    /// <remarks>
    /// ★ 已完整读取、没有凭证、或总行数未知时返回空 —— 调用方据此直接放行或退回保守判定。
    /// ★ 返回的是「已读区间的补集」：读 1-100 与 200-300 时，未读块是 101-199 与 301-末尾。
    /// </remarks>
    public IReadOnlyList<LineRange> UnreadBlocks(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);

        if (!_entries.TryGetValue(full, out var entry))
            return Array.Empty<LineRange>();

        lock (_gate)
        {
            if (entry.WholeFile || entry.TotalLines <= 0)
                return Array.Empty<LineRange>();

            var merged = MergeRanges(entry.Ranges);

            // 补集：区间之间、以及最后一段到末尾
            var blocks = new List<LineRange>();
            var next = 1;

            foreach (var r in merged)
            {
                if (r.From > next)
                    blocks.Add(new LineRange(next, r.From - 1));

                if (r.To + 1 > next)
                    next = r.To + 1;
            }

            if (next <= entry.TotalLines)
                blocks.Add(new LineRange(next, entry.TotalLines));

            return blocks;
        }
    }

    /// <summary>清除单个文件的凭证（修改成功后调用）。</summary>
    public bool Forget(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        return _entries.TryRemove(full, out _);
    }

    /// <summary>清除全部凭证。</summary>
    public void Clear() => _entries.Clear();

    /// <summary>列出当前有效的相对路径（供 ws_list / 状态面板展示）。</summary>
    public IReadOnlyList<string> Describe()
    {
        var list = new List<string>(_entries.Count);

        foreach (var kv in _entries)
        {
            var state = IsValid(kv.Key) ? "有效" : "已失效";
            var coverage = Coverage(kv.Key);
            var note = coverage is null ? string.Empty : "，" + coverage;

            list.Add($"{_guard.ToRelative(kv.Key)} [{state}{note}]");
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    private string EnsureInside(string absolutePath)
    {
        if (!_guard.IsInside(absolutePath))
            throw new PathEscapeException(absolutePath, _guard.Root);

        return Path.GetFullPath(absolutePath);
    }

    private static ReadTicket Snapshot(string full, Entry entry)
    {
        var merged = MergeRanges(entry.Ranges);
        var complete = entry.WholeFile || entry.TotalLines <= 0 || Covers(merged, entry.TotalLines);

        return new ReadTicket(
            full, entry.Fingerprint, entry.IssuedAt, entry.TotalLines, complete, merged);
    }

    /// <summary>把区间排序并合并相邻/重叠段。</summary>
    private static IReadOnlyList<LineRange> MergeRanges(List<LineRange> ranges)
    {
        if (ranges.Count == 0)
            return Array.Empty<LineRange>();

        var sorted = ranges.Where(r => !r.IsEmpty).OrderBy(r => r.From).ToList();

        if (sorted.Count == 0)
            return Array.Empty<LineRange>();

        var merged = new List<LineRange>(sorted.Count) { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            var last = merged[^1];

            if (last.Touches(sorted[i]))
                merged[^1] = last.Merge(sorted[i]);
            else
                merged.Add(sorted[i]);
        }

        return merged;
    }

    /// <summary>合并后的区间是否连续覆盖 1..totalLines。</summary>
    private static bool Covers(IReadOnlyList<LineRange> merged, int totalLines)
    {
        if (totalLines <= 0)
            return true;

        var next = 1;

        foreach (var r in merged)
        {
            if (r.From > next)
                return false;           // 中间有洞

            if (r.To + 1 > next)
                next = r.To + 1;

            if (next > totalLines)
                return true;
        }

        return next > totalLines;
    }

    private static int CountLines(IReadOnlyList<LineRange> merged)
    {
        var sum = 0;
        foreach (var r in merged)
            sum += r.Length;
        return sum;
    }

    /// <summary>
    /// 计算内容指纹。
    /// 小文件用 SHA-256 全量；大文件用「长度 + 修改时间 + 头尾各 32KB 哈希」，
    /// 兼顾准确性与性能（避免为 500MB 的包算哈希）。
    /// </summary>
    private static string? ComputeFingerprint(string fullPath)
    {
        var info = new FileInfo(fullPath);

        if (!info.Exists)
        {
            // 目录也给一个可区分的指纹，方便 delete 目录时校验
            return Directory.Exists(fullPath) ? "dir:" + fullPath : null;
        }

        if (info.Length <= PathGuard.FullHashLimitBytes)
        {
            using var fs = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024);

            var hash = SHA256.HashData(fs);
            return $"sha256:{Convert.ToHexString(hash)}";
        }

        // 大文件：元数据 + 头尾采样
        using var big = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024);

        const int sample = 32 * 1024;
        var head = new byte[sample];
        var headRead = big.Read(head, 0, sample);

        big.Seek(-sample, SeekOrigin.End);
        var tail = new byte[sample];
        var tailRead = big.Read(tail, 0, sample);

        using var sha = SHA256.Create();
        sha.TransformBlock(head, 0, headRead, null, 0);
        sha.TransformFinalBlock(tail, 0, tailRead);

        return $"sampled:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(sha.Hash!)}";
    }
}
