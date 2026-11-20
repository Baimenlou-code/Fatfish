using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_mkdir —— 在工作台内创建目录（含缺失的各级父目录）。
///
/// 对应 Python 版 workspace.py::ws_mkdir（<c>os.makedirs(p, exist_ok=True)</c>）。
///
/// ★ 为什么它<b>不</b>需要人工报批，而 ws_write 需要：
///   建目录是「可逆且不覆盖任何东西」的动作 —— 撤销方式是删掉这个空目录，
///   而且它建不到工作台外面去（<see cref="PathGuard"/> 先过一遍）。
///   每次建目录都弹窗，只会把用户训练成不看内容就点「允许」，
///   那反而会削弱 ws_write / ws_delete 那几次弹窗的可信度。
///
///   但它仍然是「模型单方面改变磁盘」的行为，所以 HasSideEffects = true，
///   auto 模式下照样送审查员复核 —— 见 <see cref="ApprovalPolicy.LowRiskTools"/>。
///
/// ★ 顺带一提：ws_write / ws_append 本来就会自动创建父目录，
///   所以模型大多数时候用不着这个工具。留着它的真正用途是
///   「先搭好骨架、稍后再往里填内容」这种多步任务。
/// </summary>
public sealed class WsMkdirTool : ITool
{
    public string Name => "ws_mkdir";

    public string Description =>
        "在工作台内核创建目录（父目录会一并创建）。" +
        "注意：ws_write / ws_append 会自动创建父目录，所以只在「先建骨架、稍后填内容」时才需要它。" +
        "路径为相对工作台根目录的相对路径。只建目录，不改动任何已有内容。";

    public JsonElement ParametersSchema => Schema.Object(
        [("path", Schema.String("要创建的目录路径（相对工作台根目录），例如 'src/module/sub'"))],
        "path");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("path", out var rel, out var err))
            return ValueTask.FromResult(err);

        var full = context.Guard.Resolve(rel);

        if (File.Exists(full))
            return ValueTask.FromResult(ToolResult.Failure($"{rel} 已存在，且是一个文件，无法创建同名目录"));

        // 已经存在就如实说「已存在」，而不是假装刚刚创建 ——
        // 模型会据此判断「是不是别人先建了」，从而避免重复劳动。
        if (Directory.Exists(full))
        {
            return ValueTask.FromResult(ToolResult.Success(
                new { path = context.Guard.ToRelative(full), created = false, levels = 0 },
                notice: "目录已存在，未做任何改动"));
        }

        ct.ThrowIfCancellationRequested();

        // 先数一下会新建几层：CreateDirectory 是一次性递归建完的，
        // 建完之后就再也分不清哪些是刚建的、哪些本来就有。
        var levels = 0;
        for (var probe = full; !string.IsNullOrEmpty(probe); probe = Path.GetDirectoryName(probe))
        {
            if (Directory.Exists(probe))
                break;

            levels++;

            // 到头了（盘符根）就停，否则 GetDirectoryName 会返回 null
            if (string.Equals(probe, Path.GetPathRoot(probe), StringComparison.OrdinalIgnoreCase))
                break;
        }

        Directory.CreateDirectory(full);

        return ValueTask.FromResult(ToolResult.Success(new
        {
            path = context.Guard.ToRelative(full),
            created = true,
            levels,
        }, notice: levels > 1 ? $"连父目录共新建 {levels} 层" : null));
    }
}
