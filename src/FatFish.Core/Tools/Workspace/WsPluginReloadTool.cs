using System.Text.Json;
using FatFish.Core.Plugins;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// 立刻重新扫描插件目录。
///
/// <b>为什么它算「有副作用」：</b>它会改变本进程的工具集合 ——
/// 重载之后模型能调用的工具可能多了几个、也可能少了几个。
/// 这种改变不落盘、可逆，所以归入「低风险」档（不打断用户），
/// 但它仍然是「模型单方面改变了运行时的能力边界」，因此必须进 AI 核验。
///
/// 平时不需要手动调用：宿主每轮取工具时会检查清单指纹，改完文件下一轮就生效。
/// 这个工具的用途是「我等不及下一轮」以及「我要明确的成功/失败回执」。
/// </summary>
public sealed class WsPluginReloadTool : ITool
{
    public string Name => "ws_plugin_reload";

    public string Description =>
        "立即重新扫描插件目录并重载（平时不需要：改完 plugin.json 后，下一轮对话会自动生效）。" +
        "返回装载结果与全部诊断。重载失败时会保留上一次成功装载的工具，不会让插件工具突然消失。" +
        "排查「插件为什么没被加载」时，用 force=true 忽略节流，再用 ws_plugin_list 看诊断。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("force", Schema.Boolean("即使文件没有变化也重新扫描（默认 true）", true)),
        ]);

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        var host = context.Plugins;

        if (host is null)
            return ValueTask.FromResult(ToolResult.Failure(
                "本宿主没有启用插件系统（ToolContext.Plugins 为空）"));

        var force = args.GetBool("force", true);

        context.Log?.Invoke($"[插件] 手动重载（force={force}）");

        PluginReloadReport report;

        try
        {
            report = host.Reload(force);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(ToolResult.Failure($"重载失败：{ex.Message}"));
        }

        var notice = report.Errors > 0
            ? $"{report.Errors} 个错误，详情见 diagnostics"
            : null;

        return ValueTask.FromResult(ToolResult.Success(new
        {
            reloaded = report.Reloaded,
            plugins = report.Plugins,
            tools = report.Tools,
            errors = report.Errors,
            warnings = report.Warnings,
            message = report.Message,
            diagnostics = report.Diagnostics.Select(d => d.ToString()).ToList(),
        }, notice: notice));
    }
}
