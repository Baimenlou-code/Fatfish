using System.Text.Json;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_cd_approve —— 批准并执行那个「移出默认作业区」的挂起切换请求。
///
/// 对应 Python 版 workspace.py::ws_cd_approve → approve_pending_cd。
///
/// ★ 为什么要做成两步，而不是在 ws_cd 里直接弹个窗问用户：
///   因为「弹窗」是宿主才有的能力，而工具是要能跨宿主复用的。
///   两步法把「用户同意」这件事留在对话里 —— 用户亲口说「可以换」，
///   模型才调这个工具。宿主换成一个没有弹窗能力的界面（比如 QQ 桥接、
///   甚至一个只读日志的守护进程）时，这套流程照样成立。
///
/// ★ 它没有参数：批准的是**唯一那个**挂起请求，不接受「批准到 X」。
///   如果允许带路径，就等于给了模型一条绕过 ws_cd 审批的路：
///   先随便挂起一个，再用 ws_cd_approve(path=任意目录) 落地。
///   现在唯一的落地方式就是「把用户已经看到并同意的那个目标写下去」。
/// </summary>
public sealed class WsCdApproveTool : ITool
{
    public string Name => "ws_cd_approve";

    public string Description =>
        "批准并执行上一次 ws_cd 挂起的『移出默认作业区』切换请求。" +
        "只有在用户明确同意之后才能调用；没有挂起请求时会直接失败。" +
        "不带参数 —— 批准的目标永远是那个已经展示给用户看过的目录。";

    public JsonElement ParametersSchema => Schema.Object([]);

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var session = context.Workspace;

        if (session is null)
        {
            return ValueTask.FromResult(ToolResult.Failure(
                "当前宿主未启用工作台切换（工作台根固定），没有待批准的请求。"));
        }

        var pendingBefore = session.PendingRoot;

        var result = session.ApprovePending();

        if (!result.Ok)
        {
            return ValueTask.FromResult(ToolResult.Failure(result.Message));
        }

        context.Log?.Invoke($"[工作台] 用户批准，{result.OldRoot} → {result.NewRoot}（读凭证已清空）");

        return ValueTask.FromResult(ToolResult.Success(new
        {
            approved_root = pendingBefore,
            previous_root = result.OldRoot,
            root = result.NewRoot,
            default_root = session.DefaultRoot,
            inside_default = session.IsInsideDefault(result.NewRoot),
            tickets_cleared = true,
        }, notice: result.Message + "｜读凭证已清空，修改文件前请重新 ws_read"));
    }
}
