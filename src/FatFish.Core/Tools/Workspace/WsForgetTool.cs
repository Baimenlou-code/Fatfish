using System.Text.Json;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_forget —— 清除「读凭证」。
///
/// 对应 Python 版 workspace.py::ws_forget。
///
/// ★ 凭证机制本身是「防模型凭记忆改文件」，所以撤销它应当由**用户**发起，而不是模型。
///   这个工具因此不写在任何示例流程里，也不在系统提示里出现 ——
///   它的实际用途有两个：
///     1. 用户手工下指令（「把凭证清了，从头读一遍」）；
///     2. 排查「为什么它总说我没有凭证」时，先清再读以排除脏状态。
///
/// ★ 它不改磁盘，只改本进程的内存状态，所以 IsReadOnly = true、不需要报批。
///   （把「改内存」也算成副作用的话，那 ws_read 签发凭证也是副作用 —— 概念就散了。）
/// </summary>
public sealed class WsForgetTool : ITool
{
    public string Name => "ws_forget";

    public string Description =>
        "清除『读凭证』。给 path 只清该文件，不给则清空全部。" +
        "清除后要修改那个文件就必须重新 ws_read。这是排查「凭证明明有却说没有」时的手段，" +
        "正常流程里不需要调用。只改本会话的内存状态，不动磁盘。";

    public JsonElement ParametersSchema => Schema.Object(
        [("path", Schema.String("相对工作台根目录的文件路径；省略则清空全部凭证"))]);

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var rel = args.GetString("path");

        // 不传 path（或传了空串）→ 清空全部。
        // 这与 Python 版的 ws_forget(path="") 行为一致。
        if (string.IsNullOrWhiteSpace(rel))
        {
            var before = context.Tickets.Count;
            context.Tickets.Clear();

            return ValueTask.FromResult(ToolResult.Success(new
            {
                scope = "all",
                cleared = before,
            }, notice: before == 0 ? "本来就没有凭证" : $"已清空 {before} 张凭证"));
        }

        var full = context.Guard.Resolve(rel);
        var had = context.Tickets.Forget(full);

        return ValueTask.FromResult(ToolResult.Success(new
        {
            scope = "one",
            path = context.Guard.ToRelative(full),
            cleared = had ? 1 : 0,
        }, notice: had ? null : "该文件本来就没有凭证"));
    }
}
