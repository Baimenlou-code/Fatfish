using System.Text.Json;
using FatFish.Core.Plugins;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// 查看单个插件、或单个插件工具的详情。
///
/// 与 <see cref="WsPluginListTool"/> 的分工：列表回答「有什么」，
/// 本工具回答「这个东西到底怎么用 / 为什么是现在这个风险档」。
/// 第二个用途很重要 —— 当模型看到某个插件工具需要报批时，
/// 它应该能查到「因为这个工具会跑 python」，
/// 而不是把责任推给用户（「系统不让」）。
/// </summary>
public sealed class WsPluginInfoTool : ITool
{
    public string Name => "ws_plugin_info";

    public string Description =>
        "查看插件或插件工具的详情。name 可以填插件 id（如 stock-quote），也可以填工具名（如 stock_quote）。" +
        "返回里包含风险档的来历（执行面推断 vs 清单声明）、参数 schema、以及该插件相关的诊断。" +
        "插件有多个工具时，先用 ws_plugin_list 看全貌，再用本工具逐个确认参数。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("name", Schema.String("插件 id 或插件工具名")),
        ],
        "name");

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        var host = context.Plugins;

        if (host is null)
            return ValueTask.FromResult(ToolResult.Failure(
                "本宿主没有启用插件系统（ToolContext.Plugins 为空）"));

        if (!args.TryGetRequired("name", out var key, out var err))
            return ValueTask.FromResult(err);

        var plugin = host.FindPlugin(key);

        if (plugin is not null)
        {
            return ValueTask.FromResult(ToolResult.Success(new
            {
                kind = "plugin",
                id = plugin.Id,
                name = plugin.Name,
                version = plugin.Version,
                description = plugin.Description,
                built_in = plugin.BuiltIn,
                requires = plugin.Requires,
                directory = plugin.DirectoryPath,
                tools = plugin.Tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    risk = t.EffectiveRisk.ToString().ToLowerInvariant(),
                    declared_risk = t.DeclaredRisk?.ToString().ToLowerInvariant(),
                    read_only = t.IsReadOnly,
                    run = t.Run.Describe(),
                    parameters = t.Parameters,
                    required = t.RequiredParameters,
                }).ToList(),
                diagnostics = host.Diagnostics
                    .Where(d => string.Equals(d.Source, plugin.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.ToString())
                    .ToList(),
            }));
        }

        var tool = host.FindTool(key);

        if (tool is not null)
        {
            var spec = tool.Spec;

            return ValueTask.FromResult(ToolResult.Success(new
            {
                kind = "tool",
                name = spec.Name,
                plugin = new { id = tool.PluginId, name = tool.Manifest.Name, version = tool.Manifest.Version },
                description = spec.Description,
                risk = new
                {
                    effective = spec.EffectiveRisk.ToString().ToLowerInvariant(),
                    declared = spec.DeclaredRisk?.ToString().ToLowerInvariant(),
                    inferred = PluginRisk.Infer(spec.Run).ToString().ToLowerInvariant(),
                    read_only = spec.IsReadOnly,
                    requires_approval = spec.EffectiveRisk != PluginRiskTier.Low,
                    one_key_approval_allowed = spec.EffectiveRisk != PluginRiskTier.High,
                },
                run = new
                {
                    kind = spec.Run.Kind.ToString().ToLowerInvariant(),
                    describe = spec.Run.Describe(),
                    url = spec.Run.Http?.Url,
                    hosts = spec.Run.Http?.Hosts,
                    method = spec.Run.Http?.Method,
                    command = spec.Run.Cmd?.Command,
                    shell = spec.Run.Cmd?.Shell,
                    script = spec.Run.Python is { } py ? Path.GetFileName(py.ScriptPath) : null,
                    timeout_seconds = spec.Run.Kind switch
                    {
                        PluginRunKind.Http => spec.Run.Http!.TimeoutSeconds,
                        PluginRunKind.Cmd => spec.Run.Cmd!.TimeoutSeconds,
                        _ => spec.Run.Python!.TimeoutSeconds,
                    },
                },
                parameters = spec.Parameters,
                required = spec.RequiredParameters,
                environment = spec.EnvironmentNames,
            }));
        }

        return ValueTask.FromResult(ToolResult.Failure(
            $"没有找到插件或插件工具 '{key}'；先用 ws_plugin_list 看看有哪些"));
    }
}
