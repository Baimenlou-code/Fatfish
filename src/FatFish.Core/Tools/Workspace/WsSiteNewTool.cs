using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_site_new —— 开一处临时车间（建目录 + 登记 + 切过去）。
///
/// ★ 它与 ws_mkdir 的区别：ws_mkdir 只建目录，切根还得再调 ws_cd，
///   而且切过去之后**没有任何地方记得**这个目录曾经是干什么的。
///   车间是「有名字、有事由、下次还能按名字回来」的目录。
///
/// ★ 为什么它不弹窗：
///   与 ws_cd 同一档（<see cref="ApprovalPolicy.LowRiskTools"/>）——
///   它只动「工作台的形状」，不碰任何已有文件内容；
///   而换根本身的安全边界由 <see cref="WorkspaceSession.ChangeTo"/> 继续守着
///   （默认作业区之外仍然要 ws_cd_approve）。
/// </summary>
public sealed class WsSiteNewTool : ITool
{
    public string Name => "ws_site_new";

    public string Description =>
        "开一处临时车间：在库房下建一个带名字的目录、登记进清单，并立刻把工作台切过去。" +
        "适合「临时处理一批东西，弄完就撤」的活计（一份外来文档、一次数据整理、一个只看一眼的旧项目）。" +
        "车间名留空会自动生成 site_MMdd_HHmm。落地位置是 <库房>\\sites\\<名字>。" +
        "切换会清空所有读凭证（修改文件前要重新 ws_read）。收工用 ws_site_home 回库房。";

    public JsonElement ParametersSchema => Schema.Object(
    [
        ("name", Schema.String("车间名（短名，之后用它 ws_site_enter / ws_site_drop）。留空则自动生成")),
        ("note", Schema.String("备注：这处车间是干什么的。写一句，将来自己看得懂")),
    ]);

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var sites = context.Sites;
        var session = context.Workspace;

        if (sites is null || session is null)
            return ValueTask.FromResult(ToolResult.Failure(
                "当前宿主未启用车间登记（工作台根固定）。请改用 ws_mkdir + 相对路径操作。"));

        var name = args.GetString("name");
        var note = args.GetString("note");

        ct.ThrowIfCancellationRequested();

        // 先重读：Create 成功后会把整份清单写回磁盘，
        // 若内存里是过期清单，手加的那几条会被静默覆盖掉。
        sites.Reload();

        var site = sites.Create(name, note, out var error);

        if (site is null)
            return ValueTask.FromResult(ToolResult.Failure(error));

        context.Log?.Invoke($"[车间] 新建「{site.Name}」：{site.Path}");

        // 建完就切过去。不切的话，「开一个车间」和「进一个车间」两步永远一起来，
        // 中间还多一次模型调用 —— 那只是把成本转嫁给用户。
        var switched = session.ChangeTo(site.Path);

        if (!switched.Ok)
        {
            // 目录已经建好并登记了，只是没切过去 —— 如实报告，不要把登记也一起撤掉
            return ValueTask.FromResult(ToolResult.Success(new
            {
                name = site.Name,
                path = site.Path,
                switched = false,
                root = session.Root,
                needs_approval = switched.NeedsApproval,
            }, notice: $"车间「{site.Name}」已建好并登记，但没能切过去：{switched.Message}"));
        }

        return ValueTask.FromResult(ToolResult.Success(new
        {
            name = site.Name,
            path = site.Path,
            switched = true,
            previous_root = switched.OldRoot,
            root = switched.NewRoot,
            tickets_cleared = true,
            sites_root = sites.SitesRoot,
            registry = sites.RegistryPath,
        }, notice: $"车间「{site.Name}」已就位，工作台已切过去｜读凭证已清空，修改文件前请重新 ws_read"));
    }
}
