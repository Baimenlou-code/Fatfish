using FatFish.Core.Tools;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 进程输出收集器：**头尾都在内存里，文件只是完整副本**。
/// </summary>
/// <remarks>
/// ★ 这批测试守的是 2026-11-05 那个改动最容易踩坏的两处：
///   1. 磁盘写不了时，头尾必须还在（如果只从文件读，会返回空字符串 ——
///      那比截断更严重，等于把命令输出整个吞掉）；
///   2. 尾部拿的必须真是**最后**那段，不是「第二段 24KB」——
///      traceback 只在最末尾，差一个字节就等于没修。
/// ★ 断言里凡是涉及长度的，一律由 <see cref="ToolOutputCapture.InlineBytes"/> 反推，
///   不手写 24000/24576 这类数字 —— 手算过一次，就错了一次。
/// </remarks>
public sealed class ToolOutputCaptureTests
{
    /// <summary>造一段可辨识的数据：整块都是同一个字符。</summary>
    private static byte[] Chunk(char c, int size) => Enumerable.Repeat((byte)c, size).ToArray();

    [Fact]
    public void NullDirectory_StillKeepsHeadAndTail()
    {
        // 不落盘（directory = null）——模拟磁盘不可用
        using var capture = new ToolOutputCapture(null, "x");

        capture.WriteStdout(Chunk('a', 50_000).AsMemory());

        var (head, tail, path, total) = capture.SnapshotStdout("utf-8");

        Assert.Null(path);
        Assert.Equal(50_000, total);
        Assert.Equal(ToolOutputCapture.InlineBytes, head.Length);
        Assert.Equal(ToolOutputCapture.InlineBytes, tail.Length);
        Assert.All(head, ch => Assert.Equal('a', ch));
        Assert.All(tail, ch => Assert.Equal('a', ch));
    }

    [Fact]
    public void HeadIsTheBeginning_TailIsTheVeryEnd()
    {
        const int chunkSize = 1000;
        const int chunkCount = 100;

        using var capture = new ToolOutputCapture(null, "x");

        // 每块字符不同：第 i 块 = 'a' + i % 26
        for (var i = 0; i < chunkCount; i++)
            capture.WriteStdout(Chunk((char)('a' + i % 26), chunkSize).AsMemory());

        var (head, tail, _, total) = capture.SnapshotStdout("utf-8");

        Assert.Equal(chunkCount * chunkSize, total);

        // 头 = 第 0 块开头
        Assert.Equal('a', head[0]);

        // 尾的最后一个字节 = 最后一块的字符
        Assert.Equal((char)('a' + (chunkCount - 1) % 26), tail[^1]);

        // 尾的第一个字节 = 「总量 − InlineBytes」所在的那一块（由常量反推，不手写）
        var tailStartChunk = (chunkCount * chunkSize - ToolOutputCapture.InlineBytes) / chunkSize;
        Assert.Equal((char)('a' + tailStartChunk % 26), tail[0]);
    }

    [Fact]
    public void ShortOutput_HeadEqualsTailEqualsWhole()
    {
        using var capture = new ToolOutputCapture(null, "x");

        capture.WriteStdout("hello"u8.ToArray().AsMemory());

        var (head, tail, _, total) = capture.SnapshotStdout("utf-8");

        Assert.Equal(5, total);
        Assert.Equal("hello", head);
        Assert.Equal("hello", tail);
    }

    [Fact]
    public void Stderr_IsTrackedSeparately()
    {
        using var capture = new ToolOutputCapture(null, "x");

        capture.WriteStdout("out"u8.ToArray().AsMemory());
        capture.WriteStderr("err"u8.ToArray().AsMemory());

        Assert.Equal("out", capture.SnapshotStdout("utf-8").Head);
        Assert.Equal("err", capture.SnapshotStderr("utf-8").Head);
        Assert.Equal(3, capture.SnapshotStdout("utf-8").Total);
        Assert.Equal(3, capture.SnapshotStderr("utf-8").Total);
    }

    [Fact]
    public void WithDirectory_WritesFullCopy_AndEmptyStreamFileIsPruned()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fatfish_capture_" + Guid.NewGuid().ToString("N"));

        try
        {
            long total;
            string? path;

            using (var capture = new ToolOutputCapture(dir, "t"))
            {
                capture.WriteStdout(Chunk('z', 70_000).AsMemory());

                (_, _, path, total) = capture.SnapshotStdout("utf-8");
            }

            Assert.Equal(70_000, total);
            Assert.NotNull(path);
            Assert.True(File.Exists(path), "完整输出文件应保留");

            // 完整副本是**全量**，不是头尾
            Assert.Equal(70_000, new FileInfo(path!).Length);

            // stderr 一个字节都没写 → 对应文件应被删掉，不留下 0 字节垃圾
            var errPath = Path.Combine(dir, "t.err.log");
            Assert.False(File.Exists(errPath), "空的 stderr 文件应被清理");
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }
}
