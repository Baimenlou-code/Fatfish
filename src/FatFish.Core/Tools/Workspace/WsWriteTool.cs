using System.Text;
using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_write —— 全量写入/覆盖文件。
///
/// 对应 Python 版 workspace.py::ws_write。
///
/// 行为约定（继承自 Python 版）：
///   · 全量覆盖，**不需要先读**（与 ws_append / ws_replace 不同）；
///   · 覆盖前自动备份到 _backup/（根一级在 _backup/，深层在 _backup/tree/ 镜像目录；
///     产物目录与超过 4MB 的跳过，结果里 backup_skipped 说明原因）。
///     这是项目自己的版本保险，不是给用户看的——真要回滚得能找到那份备份。
///
/// ★ 2026-11-05 新增一道**只针对新风险**的闸（P4）：
///   ws_read 支持分页之后，出现了原设计里没有的情形 ——
///   「只读了 1200 行里的 100 行，却要整份覆盖」。此时模型手里只有片段，
///   覆盖会把没看过的一千多行直接抹掉，而且它自己往往并不觉得缺了什么。
///
///   闸门按「有没有读过」分档，**不改变既有的『覆盖无需先读』约定**：
///     · 文件不存在（新建）        → 放行（没有旧内容可丢）
///     · 存在但从没读过（无凭证）  → 放行（既有约定，本次不动）
///     · 存在且已完整读取          → 放行
///     · 存在且**只读了局部**      → 要求「未读块逐字保留」，由
///                                   <see cref="WriteCoverageGuard"/> 证明；
///                                   证明不了就拒绝，并给出可执行的下一步。
///   ★ 为什么只拦最后一行：前三种是既有行为，动它们属于扩大改动面；
///     而最后一种正是分页引入的、以前不可能出现的情形。
///
/// ★ 2026-11-05 追加逃生门（P5）：
///   最后那种情况可以带 <c>confirm_partial_overwrite=true</c> 越过证明，
///   但那样**必然弹出人工审批**（见 ApprovalPolicy 里专门的一条），
///   而且永不参与「一键放行」。也就是说：要么读全，要么人点头 ——
///   不能靠模型自己声称「我知道我在干什么」。
/// </summary>
public sealed class WsWriteTool : ITool
{
    public string Name => "ws_write";

    public string Description =>
        "把内容全量写入工作台内文件（覆盖原有内容，文件不存在则创建）。" +
        "覆盖前会自动备份到 _backup/（深层文件进 _backup/tree/ 镜像目录；" +
        "产物目录与超过 4MB 的跳过，backup_skipped 会说明原因）。这是全量覆盖，无需先读；" +
        "若只想改局部内容，请用 ws_replace（先 ws_read 拿凭证）。" +
        "如果你之前只是分页读了该文件的一部分，那么未读部分必须原样保留在新内容里，否则会被拒绝；" +
        "确需强行覆盖时加 confirm_partial_overwrite=true —— 那会弹出人工审批，由你决定。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("path", Schema.String("相对工作台根目录的文件路径")),
            ("content", Schema.String("要写入的完整内容（UTF-8）")),
            ("confirm_partial_overwrite", Schema.Boolean(
                "模型请求覆盖一个只读过局部的文件（跳过「未读部分必须保留」检查）。" +
                "会强制弹出人工审批，永不自动放行。默认 false", false)),
        ],
        "path", "content");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("path", out var rel, out var err))
            return err;

        var content = args.GetString("content")
                      ?? throw new ArgumentException("缺少必填参数 'content'");

        var force = args.GetBool("confirm_partial_overwrite", false);

        var full = context.Guard.Resolve(rel);

        if (Directory.Exists(full))
            return ToolResult.Failure($"{rel} 是目录，无法写入");

        var nameError = PathGuard.ValidateFileName(Path.GetFileName(full));
        if (nameError is not null)
            return ToolResult.Failure($"文件名不合法：{nameError}");

        var existed = File.Exists(full);
        string? backupPath = null;

        // ---- P4 闸：只有「部分读过」且没带逃生门时才拦 ----
        if (existed && !force)
        {
            var coverage = context.Tickets.Coverage(full);

            if (coverage is { Complete: false })
            {
                var oldContent = await ReadAllTextAsync(full, ct).ConfigureAwait(false);
                var unread = context.Tickets.UnreadBlocks(full);
                var proof = WriteCoverageGuard.Prove(oldContent, content, unread);

                if (!proof.Proven)
                {
                    return ToolResult.Failure(
                        $"{rel} 的未读区间 {proof.LostBlock} 在新内容里找不到了 —— " +
                        $"你只读过 {coverage.ReadLines}/{coverage.TotalLines} 行，" +
                        "这样覆盖会把没看过的内容丢掉。" +
                        $"要么继续读（ws_read offset={proof.LostBlock.From}），" +
                        "要么改用 ws_replace 只改你读过的那几行；" +
                        "确需强行覆盖就加 confirm_partial_overwrite=true（会弹人工审批）。");
                }
            }
        }

        // 备案覆盖任意深度：根一级进 _backup/，深层进 _backup/tree/ 的镜像目录。
        // 跳过（产物目录 / 超大小上限 / 本身在 _backup 内）会带原因返回，不静默。
        string? backupSkipped = null;
        if (existed)
        {
            var capture = RootBackup.Capture(full, context.Guard);
            backupPath = capture.Path;
            backupSkipped = capture.Skipped;
        }

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        ct.ThrowIfCancellationRequested();

        // UTF-8 无 BOM —— 与 git / 编译器的默认预期一致
        await using (var fs = new FileStream(
            full, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true))
        await using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
        }

        // 内容已变，此前的读凭证作废
        context.Tickets.Forget(full);

        var info = new FileInfo(full);

        return ToolResult.Success(new
        {
            path = context.Guard.ToRelative(full),
            bytes = info.Length,
            created = !existed,
            forced_partial_overwrite = force,
            backup = backupPath == null ? null : context.Guard.ToRelative(backupPath),
            backup_skipped = backupSkipped,
        }, notice: backupPath is null ? null : $"原文件已备份到 {context.Guard.ToRelative(backupPath)}");
    }

    private static async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
