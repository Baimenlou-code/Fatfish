using System.Text.Json;
using FatFish.Core.Tools;

namespace FatFish.Core.Plugins;

/// <summary>
/// 把一个插件工具定义包装成内核认识的 <see cref="ITool"/>。
///
/// 这层包装很薄，但**不能省**：<c>ITool</c> 是内核与模型之间的契约，
/// 让 <see cref="PluginHost"/> 直接返回自定义类型会迫使 AgentLoop、ToolRegistry、
/// 审批、核验全部改一遍。薄适配器的价值就在于「内核完全不知道有插件这回事」——
/// 它看到的只是又多了一个普通工具，同样走审批、同样送 AI 核验。
///
/// 唯一泄露出处的地方是 <see cref="Description"/> 末尾那句「（插件 X）」：
/// 模型看到它会更谨慎地使用（插件工具的语义不像内置工具那样有保证），
/// 而人看日志时也能立刻分清「这是谁在动」。
/// </summary>
public sealed class PluginTool : ITool
{
    private readonly PluginManifest _manifest;
    private readonly PluginToolSpec _spec;
    private readonly string _description;

    public PluginTool(PluginManifest manifest, PluginToolSpec spec)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(spec);

        _manifest = manifest;
        _spec = spec;

        _description = $"{spec.Description}（插件 {manifest.Name} v{manifest.Version}）";
    }

    public string Name => _spec.Name;

    public string Description => _description;

    public JsonElement ParametersSchema => _spec.Parameters;

    /// <summary>
    /// ★ 只读标志**来自推断**，不来自插件自述（见 <see cref="PluginManifestParser"/>）。
    ///   AgentLoop 的核验判定只看这个标志：如果它是插件说了算，
    ///   那么写一行 <c>"readOnly": true</c> 就能让一个跑 <c>rm</c> 的工具跳过 AI 核验。
    /// </summary>
    public bool IsReadOnly => _spec.IsReadOnly;

    public bool HasSideEffects => _spec.HasSideEffects;

    public string PluginId => _manifest.Id;

    public PluginRiskTier Risk => _spec.EffectiveRisk;

    public PluginRunKind Kind => _spec.Run.Kind;

    /// <summary>该工具的规格（宿主列举时用）。</summary>
    public PluginToolSpec Spec => _spec;

    public PluginManifest Manifest => _manifest;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
        => PluginRunner.RunAsync(
            _spec,
            _manifest.Id,
            _manifest.DirectoryPath,
            context,
            args,
            ct,
            // 把「归属这个插件的配置」一并交下去：Runner 靠它知道
            // 用户在插件设置里选的模型该写回哪个环境变量。
            _manifest.Settings);
}
