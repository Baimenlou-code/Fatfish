using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_cd —— 切换工作台根目录。
///
/// 对应 Python 版 workspace.py::ws_cd → set_workspace。
///
/// ★ 这是**唯一**接受绝对路径的工具，也是唯一会改变「所有其它工具看到的世界」的工具。
///   三点因此必须守死：
///     1. 它只改「根指向」，自己不读也不写任何文件内容 ——
///        真正的写操作仍然要过 ws_write 等工具，仍然要读凭证与报批；
///     2. 切到默认作业区之外时**不执行**，只登记待批准，等用户点头后由 ws_cd_approve 放行；
///     3. 换根即清空读凭证（新的凭证库是空的）。
///        凭证里存的是绝对路径 + 内容指纹，换根后同一个相对路径指向别的文件，
///        两个内容相同的文件会被指纹认成同一个 —— 那等于「读过 A 就能改 B」。
///
/// ★ 同一批 tool_calls 里的顺序会影响结果：[ws_cd, ws_write] 里的 ws_write
///   会落在**新根**下。这不是缺陷而是必然（工具是顺序执行的），
///   但审查员与用户都该知道 —— 所以工具结果里会明写新根。
/// </summary>
public sealed class WsCdTool : ITool
{
    public string Name => "ws_cd";

    public string Description =>
        "切换工作台根目录（可用绝对路径、相对路径、%ENV% 或 ~）。" +
        "切到默认作业区之内会立即生效；切到默认作业区之外不会执行，" +
        "只会登记为待批准并返回提示 —— 必须先向用户说明目标与理由，用户同意后再调用 ws_cd_approve。" +
        "切换会清空所有读凭证，且同批次后续工具将按新根解析路径。";

    public JsonElement ParametersSchema => Schema.Object(
        [("path", Schema.String("目标目录。可以是绝对路径，也可以相对当前工作台根，例如 'workspace/sub'"))],
        "path");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("path", out var path, out var err))
            return ValueTask.FromResult(err);

        var session = context.Workspace;

        if (session is null)
        {
            // 宿主未把工作台托管成可切换会话（例如只读沙箱、或单测直接 new 了 Guard）。
            // 这里必须**明确失败**，不能悄悄当成「相对当前目录」处理 ——
            // 那会让模型以为切过去了，然后照着错误的根继续干活。
            return ValueTask.FromResult(ToolResult.Failure(
                "当前宿主未启用工作台切换（工作台根固定）。请改为在当前工作台内的相对路径下操作。"));
        }

        var result = session.ChangeTo(path);

        if (result.Ok)
        {
            context.Log?.Invoke($"[工作台] {result.OldRoot} → {result.NewRoot}（读凭证已清空）");

            return ValueTask.FromResult(ToolResult.Success(new
            {
                previous_root = result.OldRoot,
                root = result.NewRoot,
                default_root = session.DefaultRoot,
                inside_default = session.IsInsideDefault(result.NewRoot),
                tickets_cleared = true,
            }, notice: result.Message + "｜读凭证已清空，修改文件前请重新 ws_read"));
        }

        if (result.NeedsApproval)
        {
            context.Log?.Invoke($"[工作台] 切换请求已挂起，等待用户批准：{session.PendingRoot}");

            // 用 Failure 而不是 Success：这确实是「没做成」。
            // 把 needs_approval 写进 error 文本里，模型照着读就知道下一步该干什么。
            return ValueTask.FromResult(ToolResult.Failure(result.Message));
        }

        return ValueTask.FromResult(ToolResult.Failure(result.Message));
    }
}
