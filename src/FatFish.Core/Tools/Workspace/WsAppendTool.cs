using System.Text;
using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_append —— 把内容追加到文件末尾。
///
/// 对应 Python 版 workspace.py::ws_append。
///
/// 与 ws_write 的分工：
///   ws_write  = 全量覆盖，**不需要**凭证（因为它本来就要替换掉全部内容）；
///   ws_append = 在原有内容后面接一段，**需要**凭证（因为它默认「你知道文件里已经有什么」）。
///
/// 两条与 Python 版一致、但值得写下来的语义：
///   1. <b>文件不存在时不要求凭证</b>。此时「追加」等价于「新建」，
///      没有任何已有数据可能被覆盖，所以没有需要保护的对象。
///   2. <b>追加前先备份</b>（根一级进 _backup/，深层进 _backup/tree/ 镜像目录）。
///      即便内容只是被追加而不被删改，也可能把 README 这类文件写成半截夹生，
///      留一份原件比省一次 File.Copy 划算。
/// </summary>
public sealed class WsAppendTool : ITool
{
    public string Name => "ws_append";

    public string Description =>
        "把内容追加到工作台内文件末尾（不会覆盖原有内容）。" +
        "对已存在的文件，调用前必须先 ws_read 拿到读凭证 —— 凭证绑定内容指纹，" +
        "文件被外部改动后自动失效；文件不存在时直接新建，无需凭证。" +
        "追加前会自动备份到 _backup/（深层文件进 _backup/tree/；产物目录与超 4MB 的跳过）。" +
        "若想整体覆盖原文件，请用 ws_write；若想改中间某一段，请用 ws_replace。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("path", Schema.String("相对工作台根目录的文件路径")),
            ("content", Schema.String("要追加到末尾的内容（UTF-8）。可以是换行等空白，但不能是空串")),
        ],
        "path", "content");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("path", out var rel, out var err))
            return err;

        // ★ 刻意不用 TryGetRequired。
        //   它把「空白字符串」等同于「缺失」，而追加一个换行（"\n"）是完全合法的操作 ——
        //   写日志、给文件收尾加空行都靠它。这里只判 null（键缺失），空白一律放行。
        var content = args.GetString("content");
        if (content is null)
            return ToolResult.Failure("缺少必填参数 'content'");

        if (content.Length == 0)
        {
            return ToolResult.Failure(
                "content 是空串：追加空内容不会改变文件，几乎一定是参数漏了。"
                + "若确实要写入空文件，请用 ws_write。");
        }

        var full = context.Guard.Resolve(rel);

        if (Directory.Exists(full))
            return ToolResult.Failure($"{rel} 是目录，无法追加内容");

        var nameError = PathGuard.ValidateFileName(Path.GetFileName(full));
        if (nameError is not null)
            return ToolResult.Failure($"文件名不合法：{nameError}");

        var existed = File.Exists(full);

        // ★ 凭证只在「文件已存在」时要求：追加到不存在的文件 = 新建，无数据可损。
        if (existed && !context.Tickets.Check(full, out var ticketError))
            return ToolResult.Failure(ticketError);

        // 备案覆盖任意深度：根一级进 _backup/，深层进 _backup/tree/ 的镜像目录。
        // 跳过（产物目录 / 超大小上限 / 本身在 _backup 内）会带原因返回，不静默。
        var capture = RootBackup.Capture(full, context.Guard);
        var backupPath = capture.Path;

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        ct.ThrowIfCancellationRequested();

        // UTF-8 无 BOM：追加进一个已有文件时绝不能补 BOM，
        // 否则文件中间会多出三个字节 \xEF\xBB\xBF（Python 版同样不带 BOM）。
        var payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);

        await using (var fs = new FileStream(
            full, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true))
        {
            await fs.WriteAsync(payload, ct).ConfigureAwait(false);
        }

        // 内容已变，此前的读凭证作废 —— 下次要再改这个文件，必须重新读。
        // （Python 版这里重新签发凭证；C# 版统一走「改完即作废」，
        //   保证同一套规则：凭证只能由 ws_read 产生。）
        context.Tickets.Forget(full);

        var notices = new List<string>(2);
        if (!existed)
            notices.Add("文件原本不存在，已新建");

        if (backupPath is not null)
            notices.Add($"原文件已备份到 {context.Guard.ToRelative(backupPath)}");

        return ToolResult.Success(new
        {
            path = context.Guard.ToRelative(full),
            appended_chars = content.Length,
            appended_bytes = payload.Length,
            bytes = new FileInfo(full).Length,
            created = !existed,
            backup = backupPath is null ? null : context.Guard.ToRelative(backupPath),
            backup_skipped = capture.Skipped,
        }, notice: notices.Count == 0 ? null : string.Join("｜", notices));
    }
}
