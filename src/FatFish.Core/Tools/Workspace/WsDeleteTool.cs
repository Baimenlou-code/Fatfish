using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_delete —— 删除文件或**空**目录。
///
/// 对应 Python 版 workspace.py::ws_delete。
///
/// 三条安全规则，每条都对应一种「删错了就回不来」的现场：
///
///   1. <b>文件必须先读</b>（凭证绑定内容指纹）。删除是最不可逆的写操作，
///      和 ws_replace 一样走凭证，避免模型凭记忆删一个自己没看过的文件。
///
///   2. <b>目录不递归</b>。Python 版用 os.rmdir，C# 版用 Directory.Delete(recursive: false)，
///      语义一致：非空目录直接拒绝。真想清空一棵树，只能一个一个删 ——
///      慢，但每一次都在审批窗口里留下痕迹，比一个递归开关安全得多。
///
///   3. <b>目录不需要凭证，但受保护目录禁止删除</b>。
///      这一条是对 Python 版的一处<u>有意修正</u>：
///      Python 版对目录也调用 _check_ticket，但没有任何工具能为目录签发凭证
///      （ws_read 读到目录直接报错），于是「删除空目录」这条路实际永远走不通 ——
///      表现为一个永远不会成功的工具分支。
///      这里改为：目录不看凭证（改由审批确认兜底，ws_delete 本来就在
///      NeverAutoApprove 名单里，每次都必须用户亲手点），
///      同时把 .git / _backup / _ssh_setup 这几个「删掉等于毁掉版本历史、
///      回滚保险、密钥」的根一级目录直接锁死。
///
///   文件在删除前会备份到 _backup/（根一级在 _backup/，深层在 _backup/tree/ 镜像目录）——
///   这是最后一道后悔药。
/// </summary>
public sealed class WsDeleteTool : ITool
{
    /// <summary>
    /// 受保护的根一级目录。
    /// .git = 版本历史（删了克隆记录全没）、
    /// _backup = 回滚保险（删了等于把后悔药一起扔了）、
    /// _ssh_setup = 私钥所在（删了要重新配 SSH）。
    /// </summary>
    private static readonly HashSet<string> ProtectedRootDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "_backup", "_ssh_setup",
    };

    public string Name => "ws_delete";

    public string Description =>
        "删除工作台内的文件，或**空目录**。删除文件前必须先 ws_read 拿到读凭证；" +
        "删除目录不需要凭证，但目录必须为空 —— 本工具绝不递归删除。" +
        "删除前会自动备份到 _backup/（深层文件进 _backup/tree/ 镜像目录；" +
        "产物目录与超过 4MB 的文件跳过，结果里 backup_skipped 会说明原因）。" +
        "根一级的 .git / _backup / _ssh_setup 受保护，拒绝删除。";

    public JsonElement ParametersSchema => Schema.Object(
        [("path", Schema.String("相对工作台根目录的文件路径，或空目录路径"))],
        "path");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("path", out var rel, out var err))
            return ValueTask.FromResult(err);

        var full = context.Guard.Resolve(rel);

        // 根目录本身：Resolve("") / Resolve(".") 都会落到这里。
        // 删掉工作台根 = 后面的每一次工具调用都失去立足点。
        if (full.Equals(context.Guard.Root, StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(ToolResult.Failure("不能删除工作台根目录本身"));

        var display = context.Guard.ToRelative(full);

        if (File.Exists(full))
            return ValueTask.FromResult(DeleteFile(context, full, display, ct));

        if (Directory.Exists(full))
            return ValueTask.FromResult(DeleteDirectory(context, rel, full, display));

        return ValueTask.FromResult(ToolResult.Failure($"不存在：{display}"));
    }

    private static ToolResult DeleteFile(
        ToolContext context,
        string full,
        string display,
        CancellationToken ct)
    {
        // ★ 凭证校验必须在备份与删除之前：
        //   没读过就一个字节都不该动 —— 包括「先备份再拒绝」这种看似无害的顺序，
        //   因为它会凭空往 _backup/ 里塞一份用户没要求过的副本。
        if (!context.Tickets.Check(full, out var ticketError))
            return ToolResult.Failure(ticketError);

        var size = new FileInfo(full).Length;

        // 删除前先备案（任意深度：根一级进 _backup/，深层进 _backup/tree/ 镜像目录）
        var capture = RootBackup.Capture(full, context.Guard);
        var backupPath = capture.Path;

        ct.ThrowIfCancellationRequested();

        File.Delete(full);

        context.Tickets.Forget(full);

        return ToolResult.Success(new
        {
            path = display,
            kind = "file",
            freed_bytes = size,
            backup = backupPath is null ? null : context.Guard.ToRelative(backupPath),
            backup_skipped = capture.Skipped,
        }, notice: backupPath is null
            ? null
            : $"删除前已备份到 {context.Guard.ToRelative(backupPath)}");
    }

    private static ToolResult DeleteDirectory(ToolContext context, string rel, string full, string display)
    {
        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (context.Guard.IsRootLevelFile(rel) && ProtectedRootDirs.Contains(name))
        {
            return ToolResult.Failure(
                $"{display} 是受保护目录（版本历史 / 回滚备份 / 密钥），本工具拒绝删除。"
                + "确实需要时请用户手工处理。");
        }

        // 不递归：非空就停手。
        // 用 GetFileSystemEntries 而不是 Enumerate + Any()，是为了不在这种
        // 极小的目录上引入 Linq 依赖（空目录时数组长度为 0，开销可忽略）。
        if (Directory.GetFileSystemEntries(full).Length > 0)
        {
            return ToolResult.Failure(
                $"{display} 不是空目录，拒绝删除。本工具不递归删除 —— "
                + "请先用 ws_list 看清内容，再逐个删除。");
        }

        Directory.Delete(full, recursive: false);

        return ToolResult.Success(new
        {
            path = display,
            kind = "dir",
            freed_bytes = 0,
            backup = (string?)null,
        }, notice: "空目录已删除（目录不看读凭证，删除依据是本次审批确认）");
    }
}
