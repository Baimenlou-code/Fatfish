using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_site_home —— 收工，回库房。
///
/// 就是 <see cref="WorkspaceSession.Reset"/> 的一层薄壳。
/// 单独给一个工具而不是「ws_cd 到默认根」，理由只有一个：
/// 模型手里永远不需要知道默认根那串绝对路径，少一处拼错的机会。
/// </summary>
public sealed class WsSiteHomeTool : ITool
{
    public string Name => "ws_site_home";

    public string Description =>
        "收起车间：把工作台切回库房（默认作业区）。" +
        "切换会清空所有读凭证，修改文件前请重新 ws_read。注意：它**不删**任何车间目录，车间仍留在 ws_site_list 里。";

    public JsonElement ParametersSchema => Schema.Object([]);

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var session = context.Workspace;

        if (session is null)
            return ValueTask.FromResult(ToolResult.Failure(
                "当前宿主未启用工作台切换（工作台根固定）。"));

        var result = session.Reset();

        if (!result.Ok)
            return ValueTask.FromResult(ToolResult.Failure(result.Message));

        context.Log?.Invoke($"[工作台] 回库房：{result.NewRoot}");

        return ValueTask.FromResult(ToolResult.Success(new
        {
            previous_root = result.OldRoot,
            root = result.NewRoot,
            home = session.DefaultRoot,
            tickets_cleared = true,
        }, notice: result.Message + "｜读凭证已清空，修改文件前请重新 ws_read"));
    }
}
