using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_site_drop —— 注销一处车间（只删登记，不删文件）。
///
/// ★ 刻意**不**提供「连目录一起删」：
///   删文件只有 ws_delete 一条路 —— 那条路要求先读、只删空目录、
///   自动备份到 _backup、每次单独审批。在这里另开一个「顺手删掉车间」的口子，
///   等于给删除开了一条不需要读、不需要备份、点一下就没的后门。
///   要清目录：先 ws_site_home，再 ws_delete（或自己在资源管理器里删）。
/// </summary>
public sealed class WsSiteDropTool : ITool
{
    public string Name => "ws_site_drop";

    public string Description =>
        "注销一处车间：把它从清单里去掉。**不删除任何文件**——目录和内容都原地保留。" +
        "当前正站在该车间里时不能注销（先 ws_site_home）。要真的清掉目录，那是 ws_delete 的事。";

    public JsonElement ParametersSchema => Schema.Object(
    [
        ("name", Schema.String("要注销的车间名")),
    ], "name");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("name", out var key, out var err))
            return ValueTask.FromResult(err);

        var sites = context.Sites;

        if (sites is null)
            return ValueTask.FromResult(ToolResult.Failure(
                "当前宿主未启用车间登记。"));

        // 手改过 _sites.json 的话，内存里的清单已经过期了
        sites.Reload();

        var site = sites.Find(key);

        if (site is null)
            return ValueTask.FromResult(ToolResult.Failure($"没有这个车间：{key}"));

        if (!sites.Drop(key, out var error))
            return ValueTask.FromResult(ToolResult.Failure(error));

        context.Log?.Invoke($"[车间] 已注销「{site.Name}」（目录保留：{site.Path}）");

        return ValueTask.FromResult(ToolResult.Success(new
        {
            name = site.Name,
            path = site.Path,
            files_kept = true,
            registry = sites.RegistryPath,
        }, notice: $"已从清单移除「{site.Name}」；目录与全部文件原样保留在 {site.Path}"));
    }
}
