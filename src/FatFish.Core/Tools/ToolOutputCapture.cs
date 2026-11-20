namespace FatFish.Core.Tools;

/// <summary>
/// 进程输出的收集器：**内存里恒留头尾，磁盘上留完整副本**。
/// </summary>
/// <remarks>
/// ★ 存在的理由（2026-11-05）：
///   <see cref="ProcessRunner.RunAsync"/> 的输出上限是 24KB，而且
///   <c>PumpAsync</c> 是从**开头**往后写的 —— 保留头部、丢掉尾部。
///   而命令行工具的结论几乎总在尾部（traceback、退出原因、汇总统计），
///   于是模型看到的是「8000 行正常日志」，据此得出「一切正常」，
///   真正的错误被静默丢掉，而且没有任何提示能让它察觉。
///
/// ★ 三条硬性设计约束：
///   1. **头尾都在内存里**。落盘只是「完整副本」的额外福利 ——
///      如果只从文件读，磁盘写失败时就会返回空字符串，那是比截断更严重的回归。
///   2. **落盘失败不影响命令**：捕获器退化成「只有头尾」，绝不抛异常。
///   3. **完整的那一份必须可及**：「完整」不等于「每次塞进上下文」，
///      而是给一个路径，让模型自己 ws_search / ws_read。
///
/// ★ 为什么复用 <see cref="ProcessRunner.RunStreamingAsync"/> 而不是改 RunAsync：
///   那个泵血函数里有三个「不报错但会挂死」的坑（必须读原始字节、超限后仍要读到 EOF、
///   超时杀整棵树）。落盘这件事完全可以在它的 sink 上做，不必去动那三个坑。
/// </remarks>
public sealed class ToolOutputCapture : IDisposable
{
    /// <summary>行内保留多少字节（头、尾各一份）。</summary>
    public const int InlineBytes = 24 * 1024;

    /// <summary>一条流（stdout 或 stderr）的收集状态。</summary>
    private sealed class Channel : IDisposable
    {
        private readonly MemoryStream _head = new();
        private readonly Queue<byte[]> _tail = new();

        private FileStream? _file;
        private long _tailBytes;

        public string? Path { get; private set; }

        public long Total { get; private set; }

        public void Open(string directory, string path)
        {
            try
            {
                Directory.CreateDirectory(directory);
                Path = path;
                _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
            catch (Exception)
            {
                // 磁盘满 / 目录只读 / 被杀软锁住：放弃落盘，头尾照常保留
                _file?.Dispose();
                _file = null;
                Path = null;
            }
        }

        public void Write(ReadOnlyMemory<byte> chunk)
        {
            Total += chunk.Length;

            if (_file is not null)
            {
                try
                {
                    _file.Write(chunk.Span);
                }
                catch (Exception)
                {
                    // 写盘失败就关掉它，别让后续每一块都抛一次
                    _file.Dispose();
                    _file = null;
                }
            }

            // 头：只留前 InlineBytes
            var room = InlineBytes - (int)_head.Length;

            if (room > 0)
                _head.Write(chunk.Span[..Math.Min(chunk.Length, room)]);

            // 尾：环形保留最后 InlineBytes
            var copy = chunk.ToArray();
            _tail.Enqueue(copy);
            _tailBytes += copy.Length;

            while (_tailBytes - _tail.Peek().Length >= InlineBytes)
                _tailBytes -= _tail.Dequeue().Length;
        }

        public (string Head, string Tail, string? Path, long Total) Snapshot(string? encodingMode)
        {
            try
            {
                _file?.Flush();

                var headBytes = _head.ToArray();

                var tailBytes = new byte[_tailBytes];
                var at = 0;

                foreach (var chunk in _tail)
                {
                    chunk.CopyTo(tailBytes, at);
                    at += chunk.Length;
                }

                if (tailBytes.Length > InlineBytes)
                    tailBytes = tailBytes[^InlineBytes..];

                return (
                    ProcessRunner.Clean(ProcessEncoding.Decode(headBytes, encodingMode)),
                    ProcessRunner.Clean(ProcessEncoding.Decode(tailBytes, encodingMode)),
                    Path,
                    Total);
            }
            catch (Exception)
            {
                return (string.Empty, string.Empty, Path, Total);
            }
        }

        public void Dispose()
        {
            var empty = Total == 0;

            try
            {
                _file?.Dispose();
            }
            catch (Exception)
            {
                // 忽略
            }

            _file = null;

            if (!empty || Path is null)
                return;

            // 空文件没必要留着（stderr 常年为空）
            try
            {
                File.Delete(Path);
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }

    private readonly object _gate = new();
    private readonly Channel _out = new();
    private readonly Channel _err = new();

    /// <param name="directory">
    /// 落盘目录（通常是 &lt;工作台根&gt;/logs/tool_output）。为 null 则只保留头尾、不落盘。
    /// </param>
    /// <param name="prefix">文件名前缀，例如 <c>cmd_20261105_220130_412</c>。</param>
    public ToolOutputCapture(string? directory, string prefix)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        _out.Open(directory, Path.Combine(directory, prefix + ".out.log"));
        _err.Open(directory, Path.Combine(directory, prefix + ".err.log"));
    }

    public void WriteStdout(ReadOnlyMemory<byte> chunk)
    {
        lock (_gate)
            _out.Write(chunk);
    }

    public void WriteStderr(ReadOnlyMemory<byte> chunk)
    {
        lock (_gate)
            _err.Write(chunk);
    }

    /// <summary>stdout 的头、尾、完整文件路径、原始总字节数。</summary>
    public (string Head, string Tail, string? Path, long Total) SnapshotStdout(string? encodingMode)
    {
        lock (_gate)
            return _out.Snapshot(encodingMode);
    }

    /// <summary>stderr 的头、尾、完整文件路径、原始总字节数。</summary>
    public (string Head, string Tail, string? Path, long Total) SnapshotStderr(string? encodingMode)
    {
        lock (_gate)
            return _err.Snapshot(encodingMode);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _out.Dispose();
            _err.Dispose();
        }
    }
}
