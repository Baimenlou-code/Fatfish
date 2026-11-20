using System.Text.Json;
using FatFish.Core.Plugins;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// 列出已装载的插件与它们提供的工具。
///
/// 为什么这个工具要存在：插件是**磁盘上的文件**，模型看不见。
/// 没有它的时候，模型只能靠系统提示词里的一句「有插件」来猜 ——
/// 而它猜不出有哪些工具、每个工具吃什么参数。列出来的成本是几百 token，
/// 省下的是「反复调用不存在的工具 → 报错 → 再猜」的好几轮。
/// </summary>
public sealed class WsPluginListTool : ITool
{
    public string Name => "ws_plugin_list";

    public string Description =>
        "列出当前装载的插件，以及每个插件提供了哪些工具（工具名、风险档、执行方式）。" +
        "插件是可扩展的工具包，改动其 plugin.json 后会自动生效，也可以用 ws_plugin_reload 立即刷新。" +
        "想了解某个插件或某个插件工具的详细参数时，用 ws_plugin_info。" +
        "verbose=true 会附带全部装载诊断（排查「插件为什么没生效」时用）。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("verbose", Schema.Boolean("是否包含 Info 级诊断（默认只给错误与警告）", false)),
        ]);

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        var host = context.Plugins;

        if (host is null)
            return ValueTask.FromResult(ToolResult.Failure(
                "本宿主没有启用插件系统（ToolContext.Plugins 为空）；这是宿主装配问题，不是插件问题"));

        var verbose = args.GetBool("verbose", false);

        var plugins = host.Manifests.Select(m => new
        {
            id = m.Id,
            name = m.Name,
            version = m.Version,
            description = m.Description,
            built_in = m.BuiltIn,
            requires = m.Requires,
            tools = m.Tools.Select(t => new
            {
                name = t.Name,
                risk = t.EffectiveRisk.ToString().ToLowerInvariant(),
                read_only = t.IsReadOnly,
                run = t.Run.Kind.ToString().ToLowerInvariant(),
            }).ToList(),
        }).ToList();

        var diagnostics = host.Diagnostics
            .Where(d => verbose || d.Level != PluginDiagnosticLevel.Info)
            .Select(d => d.ToString())
            .ToList();

        var directories = host.Directories
            .Select(d => new { path = d.Path, built_in = d.BuiltIn })
            .ToList();

        string? notice = null;

        if (plugins.Count == 0)
            notice = "还没有装载任何插件：在工作台根的 plugins/<名字>/plugin.json 放一份清单即可";

        return ValueTask.FromResult(ToolResult.Success(new
        {
            count = plugins.Count,
            tool_count = host.Risks.Count,
            plugins,
            diagnostics,
            directories,
            loaded_at = host.LoadedAt == DateTimeOffset.MinValue
                ? null
                : host.LoadedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        }, notice: notice));
    }
}
