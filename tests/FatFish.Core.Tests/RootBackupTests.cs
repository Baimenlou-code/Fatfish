using System.Text;
using System.Text.Json;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 写前备案（_backup）的行为测试。
/// </summary>
/// <remarks>
/// ★ 这批测试守的是「改坏了还能回去」这条底线，所以断言的重点不是「有没有备份」，
///   而是三个一旦错了很难察觉的性质：
///     1. 深层文件的备份落在 <c>_backup/tree/&lt;原路径&gt;</c>，不是压平在 _backup/ 根
///        —— 压平会让 src 下几十个同名文件互相撞车，等于「有备份但找不到」；
///     2. 备份内容是**改动前**的原文（不是改完再拷一遍）；
///     3. 工具层真的调了备案 —— 只测 RootBackup.Capture 是不够的：
///        「逻辑写对了但调用点还留着 IsRootLevelFile 判断」的表现和没修一模一样，而且不报错。
///
/// ★ 跳过规则（产物目录 / 超 4MB / 本身在 _backup 内）也要测：
///   静默跳过比报错更危险 —— 使用者会以为「反正有备份」。
/// </remarks>
public class RootBackupTests : IDisposable
{
    private readonly string _root;
    private readonly PathGuard _guard;

    public RootBackupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_backup_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
        _guard = new PathGuard(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论（比如被占用）
        }

        GC.SuppressFinalize(this);
    }

    // ---------- 辅助 ----------

    private string Write(string relPath, string content)
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    private (ToolRegistry Registry, ToolContext Context) NewTools()
    {
        var context = new ToolContext
        {
            Guard = _guard,
            Tickets = new ReadTicketStore(_guard),
            Approval = new ApprovalPolicy(_guard),
        };

        var registry = new ToolRegistry(new ITool[]
        {
            new WsReadTool(),
            new WsListTool(),
            new WsWriteTool(),
            new WsAppendTool(),
            new WsReplaceTool(),
            new WsDeleteTool(),
        });

        return (registry, context);
    }

    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement Data(ToolResult result)
    {
        Assert.True(result.Ok, "工具应当成功，实际失败：" + result.Error);
        Assert.NotNull(result.Data);
        return result.Data!.Value;
    }

    /// <summary>
    /// 取一个可能不存在的字符串字段。
    /// </summary>
    /// <remarks>
    /// ★ 必须是 TryGetProperty 而不是 GetProperty：ToolJson 配了
    ///   <c>DefaultIgnoreCondition = WhenWritingNull</c>，所以 null 字段**不会出现在 JSON 里**。
    ///   用 GetProperty 取一个 null 字段会抛 KeyNotFoundException，
    ///   看起来像「字段名写错了」，实际只是「值为 null 被省略」。
    /// </remarks>
    private static string? Str(JsonElement data, string name)
        => data.TryGetProperty(name, out var value) ? value.GetString() : null;

    // ==================================================================
    // 布局：根一级 vs 深层
    // ==================================================================

    [Fact]
    public void Capture_RootLevelFile_GoesToBackupRoot()
    {
        var full = Write("README.md", "原文");

        var outcome = RootBackup.Capture(full, _guard);

        Assert.Null(outcome.Skipped);
        Assert.NotNull(outcome.Path);
        Assert.Equal("_backup", Path.GetFileName(Path.GetDirectoryName(outcome.Path!)));
        Assert.Equal("原文", File.ReadAllText(outcome.Path!));
        Assert.StartsWith("README.", Path.GetFileName(outcome.Path!));
        Assert.EndsWith(".md.bak", Path.GetFileName(outcome.Path!));
    }

    [Fact]
    public void Capture_DeepFile_MirrorsDirectoryUnderTree()
    {
        var full = Write("src/app/Program.cs", "class A {}");

        var outcome = RootBackup.Capture(full, _guard);

        Assert.Null(outcome.Skipped);
        var rel = _guard.ToRelative(outcome!.Path!);

        Assert.StartsWith("_backup/tree/src/app/Program.", rel);
        Assert.EndsWith(".cs.bak", rel);
        Assert.Equal("class A {}", File.ReadAllText(outcome.Path!));
    }

    /// <summary>
    /// 深层备份必须保留目录层级：两个不同目录下的同名文件不能落到同一个备份名上。
    /// </summary>
    [Fact]
    public void Capture_SameNameInDifferentDirs_DoNotCollide()
    {
        var a = Write("a/Program.cs", "AAA");
        var b = Write("b/Program.cs", "BBB");

        var first = RootBackup.Capture(a, _guard).Path!;
        var second = RootBackup.Capture(b, _guard).Path!;

        Assert.NotEqual(first, second);
        Assert.Equal("AAA", File.ReadAllText(first));
        Assert.Equal("BBB", File.ReadAllText(second));
    }

    // ==================================================================
    // 无需备份 / 跳过
    // ==================================================================

    [Fact]
    public void Capture_MissingFile_IsNotNeeded()
    {
        var outcome = RootBackup.Capture(Path.Combine(_root, "nope.txt"), _guard);

        Assert.Null(outcome.Path);
        Assert.Null(outcome.Skipped);
        Assert.False(Directory.Exists(Path.Combine(_root, "_backup")));
    }

    [Fact]
    public void Capture_InsideBackupDir_IsSkipped()
    {
        var full = Write("_backup/old.txt", "x");

        var outcome = RootBackup.Capture(full, _guard);

        Assert.Null(outcome.Path);
        Assert.Contains("_backup", outcome.Skipped);
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData("src/bin")]
    [InlineData("src/obj/Release")]
    public void Capture_GeneratedDir_IsSkipped(string dir)
    {
        var full = Write($"{dir}/a.cs", "x");

        var outcome = RootBackup.Capture(full, _guard);

        Assert.Null(outcome.Path);
        Assert.Contains("产物", outcome.Skipped);
    }

    [Fact]
    public void Capture_OversizeFile_IsSkipped()
    {
        var big = Path.Combine(_root, "big.bin");
        using (var fs = new FileStream(big, FileMode.Create, FileAccess.Write))
            fs.SetLength(RootBackup.MaxBytes + 1);

        var outcome = RootBackup.Capture(big, _guard);

        Assert.Null(outcome.Path);
        Assert.Contains("上限", outcome.Skipped);
    }

    // ==================================================================
    // 保留策略
    // ==================================================================

    [Fact]
    public void Capture_KeepsOnlyNewestFiveBackups()
    {
        var full = Write("notes.txt", "v0");

        for (var i = 1; i <= RootBackup.KeepPerFile + 3; i++)
        {
            File.WriteAllText(full, "v" + i, new UTF8Encoding(false));
            Assert.NotNull(RootBackup.Capture(full, _guard).Path);

            // 让 LastWriteTimeUtc 有区分度，否则「淘汰最旧」无从验证
            Thread.Sleep(20);
        }

        var backups = Directory.GetFiles(Path.Combine(_root, "_backup"));
        Assert.Equal(RootBackup.KeepPerFile, backups.Length);

        var kept = backups.Select(File.ReadAllText).ToList();
        Assert.Contains("v8", kept);        // 最新一份必须在
        Assert.DoesNotContain("v0", kept);  // 最旧的已被淘汰
    }

    [Fact]
    public void Capture_TwiceInTheSameSecond_DoesNotOverwrite()
    {
        var full = Write("a.txt", "1");

        var first = RootBackup.Capture(full, _guard).Path;
        var second = RootBackup.Capture(full, _guard).Path;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    /// <summary>
    /// 备份是「改动前」的原文 —— 顺序错了（先改再拷）会让备份变成改后的副本，等于没备份。
    /// </summary>
    [Fact]
    public void Capture_SnapshotHoldsContentBeforeChange()
    {
        var full = Write("x.txt", "旧内容");

        var backup = RootBackup.Capture(full, _guard).Path!;
        File.WriteAllText(full, "新内容", new UTF8Encoding(false));

        Assert.Equal("旧内容", File.ReadAllText(backup));
        Assert.Equal("新内容", File.ReadAllText(full));
    }

    // ==================================================================
    // 工具层接缝：这才是「有没有真的接上」的证据
    // ==================================================================

    [Fact]
    public async Task WsWrite_DeepFile_IsBackedUpUnderTree()
    {
        var (registry, context) = NewTools();
        Write("src/deep/A.cs", "旧");

        var data = Data(await registry.InvokeAsync(
            "ws_write", context, Args("{\"path\":\"src/deep/A.cs\",\"content\":\"新\"}")));

        var backup = data.GetProperty("backup").GetString();
        Assert.NotNull(backup);
        Assert.StartsWith("_backup/tree/src/deep/A.", backup);
        Assert.Equal("旧", File.ReadAllText(Path.Combine(_root, backup!)));
        Assert.Null(Str(data, "backup_skipped"));
    }

    [Fact]
    public async Task WsAppend_DeepFile_IsBackedUpUnderTree()
    {
        var (registry, context) = NewTools();
        Write("src/deep/C.md", "旧");

        await registry.InvokeAsync("ws_read", context, Args("{\"path\":\"src/deep/C.md\"}"));

        var data = Data(await registry.InvokeAsync(
            "ws_append", context, Args("{\"path\":\"src/deep/C.md\",\"content\":\"新\"}")));

        var backup = data.GetProperty("backup").GetString();
        Assert.NotNull(backup);
        Assert.StartsWith("_backup/tree/src/deep/C.", backup);
        Assert.Equal("旧", File.ReadAllText(Path.Combine(_root, backup!)));
    }

    [Fact]
    public async Task WsReplace_DeepFile_IsBackedUpUnderTree()
    {
        var (registry, context) = NewTools();
        Write("src/deep/D.cs", "aaa\nbbb\n");

        await registry.InvokeAsync("ws_read", context, Args("{\"path\":\"src/deep/D.cs\"}"));

        var data = Data(await registry.InvokeAsync(
            "ws_replace", context, Args("{\"path\":\"src/deep/D.cs\",\"old\":\"aaa\",\"new\":\"zzz\"}")));

        var backup = data.GetProperty("backup").GetString();
        Assert.NotNull(backup);
        Assert.StartsWith("_backup/tree/src/deep/D.", backup);
        Assert.Contains("aaa", File.ReadAllText(Path.Combine(_root, backup!)));
    }

    [Fact]
    public async Task WsDelete_DeepFile_IsBackedUpUnderTree()
    {
        var (registry, context) = NewTools();
        Write("src/deep/B.cs", "要删的内容");

        await registry.InvokeAsync("ws_read", context, Args("{\"path\":\"src/deep/B.cs\"}"));

        var data = Data(await registry.InvokeAsync(
            "ws_delete", context, Args("{\"path\":\"src/deep/B.cs\"}")));

        var backup = data.GetProperty("backup").GetString();
        Assert.NotNull(backup);
        Assert.StartsWith("_backup/tree/src/deep/B.", backup);
        Assert.Equal("要删的内容", File.ReadAllText(Path.Combine(_root, backup!)));
        Assert.False(File.Exists(Path.Combine(_root, "src", "deep", "B.cs")));
    }

    /// <summary>
    /// 产物目录里的写入不备案 —— 但结果里必须有原因，不能静默。
    /// </summary>
    [Fact]
    public async Task WsWrite_InGeneratedDir_ReportsSkipReason()
    {
        var (registry, context) = NewTools();
        Write("bin/tmp.txt", "旧");

        var data = Data(await registry.InvokeAsync(
            "ws_write", context, Args("{\"path\":\"bin/tmp.txt\",\"content\":\"新\"}")));

        Assert.Null(Str(data, "backup"));
        Assert.Contains("产物", Str(data, "backup_skipped"));
        Assert.False(Directory.Exists(Path.Combine(_root, "_backup")));
    }

    /// <summary>
    /// _backup 默认不出现在 ws_list 里（2026-11-06 起加入 IgnoredDirs）。
    /// </summary>
    /// <remarks>
    /// ★ 为什么要专门钉住：_backup/tree/ 里是完整源码副本，
    ///   如果在列表和搜索里默认可见，一次全文检索会被同一个词命中十几遍。
    ///   而这条规则的失败方式很隐蔽 —— 列表里多了一行，没人会注意到；
    ///   等发现时已经习惯了「搜索结果里一半是 .bak」。
    /// ★ 逃生门必须仍然有效：include_ignored=true 时要能看见。
    /// </remarks>
    [Fact]
    public async Task WsList_HidesBackupDir_ButEscapeHatchWorks()
    {
        var (registry, context) = NewTools();
        Write("src/deep/A.cs", "x");
        RootBackup.Capture(Path.Combine(_root, "src", "deep", "A.cs"), _guard);

        var normal = Data(await registry.InvokeAsync("ws_list", context, Args("{}")));
        Assert.DoesNotContain("_backup", normal.GetProperty("listing").GetString()!);

        var all = Data(await registry.InvokeAsync(
            "ws_list", context, Args("{\"include_ignored\":true}")));
        Assert.Contains("_backup", all.GetProperty("listing").GetString()!);
    }
}
