using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_site_enter —— 进某处车间（按名字）。
///
/// 走的仍然是 <see cref="WorkspaceSession.ChangeTo"/>：
/// 车间若落在默认作业区之外，照样只登记待批准，要 ws_cd_approve 放行。
/// 换句话说，车间机制**没有**在边界检查上开任何口子 ——
/// 它只是把「那串又长又难记的绝对路径」换成了一个名字。
/// </summary>
public sealed class WsSiteEnterTool : ITool
{
    public string Name => "ws_site_enter";

    public string Description =>
        "进入某个已登记的车间（按车间名，也接受它的绝对路径）。" +
        "切换会清空所有读凭证，修改文件前请重新 ws_read。不确定名字时先 ws_site_list。";

    public JsonElement ParametersSchema => Schema.Object(
    [
        ("name", Schema.String("车间名，例如 'audit-2026'；也可直接给它的绝对路径")),
    ], "name");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("name", out var key, out var err))
            return ValueTask.FromResult(err);

        var sites = context.Sites;
        var session = context.Workspace;

        if (sites is null || session is null)
            return ValueTask.FromResult(ToolResult.Failure(
                "当前宿主未启用车间登记（工作台根固定）。请改用 ws_cd + 相对路径。"));

        // 手改过 _sites.json 的话，内存里的清单已经过期了
        sites.Reload();

        var site = sites.Find(key);

        if (site is null)
        {
            var known = string.Join("、", sites.All.Select(s => s.Name).DefaultIfEmpty("（还没有任何车间）"));
            return ValueTask.FromResult(ToolResult.Failure(
                $"没有这个车间：{key}。已登记的有：{known}。用 ws_site_new 开一处，或 ws_site_list 看全貌。"));
        }

        if (!Directory.Exists(site.Path))
        {
            // 目录被人删了：登记还在，但根已经不存在。如实说，别让模型以为切过去了
            return ValueTask.FromResult(ToolResult.Failure(
                $"车间「{site.Name}」的目录已不存在：{site.Path}。" +
                "（ws_site_drop 可以把这个失效的登记清掉）"));
        }

        var result = session.ChangeTo(site.Path);

        if (result.Ok)
        {
            context.Log?.Invoke($"[车间] 进入「{site.Name}」：{result.NewRoot}");

            return ValueTask.FromResult(ToolResult.Success(new
            {
                name = site.Name,
                note = site.Note,
                previous_root = result.OldRoot,
                root = result.NewRoot,
                inside_default = session.IsInsideDefault(result.NewRoot),
                tickets_cleared = true,
            }, notice: result.Message + "｜读凭证已清空，修改文件前请重新 ws_read"));
        }

        if (result.NeedsApproval)
            context.Log?.Invoke($"[车间] 进入「{site.Name}」需用户批准，已挂起");

        return ValueTask.FromResult(ToolResult.Failure(result.Message));
    }
}
