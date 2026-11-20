using System.Text.Json;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_where —— 查看当前工作台根目录。
///
/// 对应 Python 版 workspace.py::ws_where。
///
/// ★ 这个工具存在的理由不是「模型不知道根在哪」，而是：
///   根是**会变**的（ws_cd 之后）。模型在长对话里很容易沿用早期的认知，
///   于是把文件写到已经不存在的路径上。让它能随时问一句「我现在在哪」，
///   比在系统提示里反复强调「注意工作台可能已改变」有效得多。
///
/// 返回值里同时给出 <c>default_root</c> 与 <c>inside_default</c>：
/// 后者决定了下一次 ws_cd 是否需要用户批准，模型据此才能判断
/// 「我说要换的这个目录，用户会不会被要求点头」。
/// </summary>
public sealed class WsWhereTool : ITool
{
    public string Name => "ws_where";

    public string Description =>
        "查看当前工作台根目录、默认作业区，以及是否有待批准的切换请求。" +
        "只读，不需要审批。切换过工作台（ws_cd）之后可以用它确认当前到底在哪。";

    public JsonElement ParametersSchema => Schema.Object([]);

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        // 没有会话（宿主未托管可切换根）时，退化成「报当前守卫的根」——
        // 不能因为这一个展示性工具而让整个工具表在测试环境里不可用。
        var session = context.Workspace;

        var root = context.Guard.Root;
        var defaultRoot = session?.DefaultRoot ?? root;
        var insideDefault = session?.IsInsideDefault(root) ?? true;
        var pending = session?.PendingRoot;

        return ValueTask.FromResult(ToolResult.Success(new
        {
            root,
            default_root = defaultRoot,
            inside_default = insideDefault,
            pending_cd = pending,
            described = session?.Describe() ?? root,
            open_tickets = context.Tickets.Count,
        },
            notice: pending is null
                ? null
                : $"有一个待批准的切换请求：{pending}（用户同意后调用 ws_cd_approve）"));
    }
}
