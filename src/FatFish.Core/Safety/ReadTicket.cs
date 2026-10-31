using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FatFish.Core.Safety;

/// <summary>
/// 一张读凭证。
/// </summary>
/// <param name="Path">绝对路径（规范化后）。</param>
/// <param name="Fingerprint">内容指纹；null 表示路径当前不存在。</param>
/// <param name="IssuedAt">签发时间。</param>
public sealed record ReadTicket(string Path, string? Fingerprint, DateTimeOffset IssuedAt);

/// <summary>
/// ReadTicketStore —— 「读凭证」机制。
///
/// 设计意图（继承自 Python 版 workspace.py 的 _READ_TICKETS / _issue_ticket / _check_ticket）：
///   修改类工具（write / append / replace / delete）必须先成功 ws_read 过目标文件，
///   才能对同一文件动手。这样能防住「AI 凭记忆改一个自己没看过的文件」。
///
/// 本实现比 Python 版更严格一点：凭证绑定**内容指纹**而非仅路径。
/// 文件在读取后被外部进程改过，凭证立即失效，必须重读 —— 避免「读了旧版、改出新冲突」。
/// </summary>
public sealed class ReadTicketStore
{
    private readonly ConcurrentDictionary<string, ReadTicket> _tickets =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly PathGuard _guard;

    public ReadTicketStore(PathGuard guard)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    /// <summary>当前已签发的凭证数量（用于状态展示）。</summary>
    public int Count => _tickets.Count;

    /// <summary>
    /// 签发凭证。调用方应在此**之前**已成功读取文件内容。
    /// </summary>
    /// <param name="absolutePath">绝对路径，必须在工作台内。</param>
    public ReadTicket Issue(string absolutePath)
    {
        if (!_guard.IsInside(absolutePath))
            throw new PathEscapeException(absolutePath, _guard.Root);

        var full = Path.GetFullPath(absolutePath);
        var fingerprint = ComputeFingerprint(full);
        var ticket = new ReadTicket(full, fingerprint, DateTimeOffset.Now);

        _tickets[full] = ticket;
        return ticket;
    }

    /// <summary>
    /// 校验凭证是否仍然有效（存在且内容未变）。
    /// </summary>
    public bool IsValid(string absolutePath)
    {
        if (!_guard.IsInside(absolutePath))
            return false;

        var full = Path.GetFullPath(absolutePath);

        if (!_tickets.TryGetValue(full, out var ticket))
            return false;

        var current = ComputeFingerprint(full);

        // 两者都为 null = 文件本来就不存在，凭证对「新建文件」依然有效
        if (ticket.Fingerprint is null && current is null)
            return true;

        return string.Equals(ticket.Fingerprint, current, StringComparison.Ordinal);
    }

    /// <summary>
    /// 校验并给出人性化失败原因（供工具层直接拼进错误消息）。
    /// </summary>
    public bool Check(string absolutePath, out string error)
    {
        var full = Path.GetFullPath(absolutePath);

        if (!_tickets.TryGetValue(full, out var ticket))
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

    /// <summary>清除单个文件的凭证（修改成功后调用）。</summary>
    public bool Forget(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        return _tickets.TryRemove(full, out _);
    }

    /// <summary>清除全部凭证。</summary>
    public void Clear() => _tickets.Clear();

    /// <summary>列出当前有效的相对路径（供 ws_list / 状态面板展示）。</summary>
    public IReadOnlyList<string> Describe()
    {
        var list = new List<string>(_tickets.Count);
        foreach (var kv in _tickets)
        {
            var state = IsValid(kv.Key) ? "有效" : "已失效";
            list.Add($"{_guard.ToRelative(kv.Key)} [{state}]");
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
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
