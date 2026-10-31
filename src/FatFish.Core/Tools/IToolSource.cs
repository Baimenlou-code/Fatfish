using FatFish.Core.Plugins;

namespace FatFish.Core.Tools;

/// <summary>
/// 动态工具源 —— 让 <see cref="ToolRegistry"/> 能装下「运行时才出现」的工具。
///
/// <b>为什么不在插件装载时直接往 ToolRegistry 的字典里塞：</b>
/// 那样做要对注册表加锁、要处理「工具正在执行时被替换」、还要让所有持有注册表引用的
/// 地方重新读一遍。而工具集本来每轮对话都会被 <c>BuildToolSchema()</c> 重新取一次，
/// 所以「注册表每次问一个源」是改动最小、也最不容易出错的形状：
///   · 插件重载只换源内部的一个不可变快照，正在执行的对象不受影响；
///   · 注册表本身仍然可以认为自己是「只读的」——没有并发写入。
/// </summary>
public interface IToolSource
{
    /// <summary>取当前全部动态工具。实现必须返回不可变快照，且不得抛异常。</summary>
    IReadOnlyList<ITool> Snapshot();

    /// <summary>底层内容是否已变化（用于宿主决定要不要重新读取）。</summary>
    bool IsStale { get; }

    /// <summary>一行状态摘要（状态栏 / 诊断用）。</summary>
    string Describe();
}

/// <summary>插件装载/重载结果。</summary>
public sealed record PluginReloadReport(
    bool Reloaded,
    int Plugins,
    int Tools,
    int Errors,
    int Warnings,
    IReadOnlyList<PluginDiagnostic> Diagnostics,
    string Message)
{
    public bool Ok => Errors == 0;

    public string Summary => Reloaded
        ? $"已重载：{Plugins} 个插件 / {Tools} 个工具（错误 {Errors}，警告 {Warnings}）"
        : Message;
}

/// <summary>插件目录声明。</summary>
/// <param name="Path">目录绝对路径。</param>
/// <param name="BuiltIn">是否随程序发布（用户目录里的同名插件会覆盖它）。</param>
public sealed record PluginDirectory(string Path, bool BuiltIn);

/// <summary>插件宿主配置。</summary>
public sealed class PluginHostOptions
{
    /// <summary>要扫描的目录，**顺序即优先级**（后面的覆盖前面的同名插件）。</summary>
    public IReadOnlyList<PluginDirectory> Directories { get; init; } = Array.Empty<PluginDirectory>();

    /// <summary>
    /// 宿主当前具备的能力集合。
    /// </summary>
    /// <remarks>
    /// 插件的 <c>requires</c> 必须被这张表完全覆盖才允许装载。
    /// 这就是「自适应装配」的落点：能力不足时**明确拒载并说明缺什么**，
    /// 而不是装上去、等第一次调用才抛 NullReference ——
    /// 后者在模型看来是「工具坏了」，用户完全无法从对话里看出原因。
    /// </remarks>
    public IReadOnlySet<string> Capabilities { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "workspace", "net", "python",
    };

    /// <summary>已被占用的工具名（内置工具）—— 插件不许重名。</summary>
    public IReadOnlySet<string> ReservedToolNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>两次自动检查之间的最小间隔（防止每轮对话都去 stat 一遍磁盘）。</summary>
    public TimeSpan ReloadThrottle { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 插件设置文件所在的工作台根（设置写到 <c>&lt;根&gt;/config/plugins/&lt;id&gt;.json</c>）。
    /// </summary>
    /// <remarks>
    /// 为 null 时「启用 / 禁用」「记住选中的模型」这些能力整体不可用，
    /// 但插件照常装载 —— 这样 CLI、测试这类没有工作台概念的宿主不必被迫造一个目录出来。
    /// </remarks>
    public string? SettingsRoot { get; init; }
}
