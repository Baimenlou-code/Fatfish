using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_site_list —— 列出全部车间（含库房），标出当前站在哪。
///
/// 只读：不建目录、不改根、不写登记簿。它唯一的用处是让模型在动手之前
/// 先看清「我现在在哪个车间、还有哪些车间、名字各是什么」——
/// 这比让模型猜一个名字然后失败一次便宜得多。
/// </summary>
public sealed class WsSiteListTool : ITool
{
    public string Name => "ws_site_list";

    public string Description =>
        "列出所有临时车间与库房，并标出当前所在的位置。" +
        "进车间前先看一眼这个，别猜名字。车间里有 note 字段，写着当初是为什么开的。";

    public JsonElement ParametersSchema => Schema.Object([]);

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var sites = context.Sites;
        var session = context.Workspace;

        if (sites is null || session is null)
            return ValueTask.FromResult(ToolResult.Failure(
                "当前宿主未启用车间登记（工作台根固定）。"));

        // 每次现读盘：登记簿是给人改的（档名就叫 _sites.json，没加密），
        // 而它在启动时被载入过一次内存 —— 不重读就会一直显示旧清单。
        sites.Reload();

        var current = session.Root;

        static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return p.Trim(); }
        }

        var list = sites.All.Select(s => new
        {
            name = s.Name,
            path = s.Path,
            note = s.Note,
            created_at = s.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            exists = Directory.Exists(s.Path),
            current = string.Equals(Norm(s.Path), Norm(current), StringComparison.OrdinalIgnoreCase),
        }).ToList();

        var atHome = string.Equals(Norm(current), Norm(session.DefaultRoot), StringComparison.OrdinalIgnoreCase);

        return ValueTask.FromResult(ToolResult.Success(new
        {
            current_root = current,
            home = session.DefaultRoot,
            at_home = atHome,
            inside_default = session.IsInsideDefault(current),
            sites_root = sites.SitesRoot,
            registry = sites.RegistryPath,
            count = list.Count,
            sites = list,
        }, notice: sites.LoadError ?? sites.Describe()));
    }
}
