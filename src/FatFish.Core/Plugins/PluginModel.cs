using System.Text;
using System.Text.Json;

namespace FatFish.Core.Plugins;

/// <summary>
/// 插件工具的风险档 —— 与 <see cref="Safety.ApprovalPolicy"/> 的三档一一对应。
/// </summary>
/// <remarks>
/// ★ 枚举值本身有含义：<b>数值越大越严</b>。
///   <see cref="PluginRisk.Combine"/> 用 <c>Math.Max</c> 实现「声明只能加严、不能放宽」，
///   那行代码的正确性直接依赖 Low &lt; Normal &lt; High 这个顺序。
///   将来若要插一档（比如 Critical），必须放在末尾，不能插在中间 ——
///   插在中间会让 Math.Max 的结果静默地变松，而且没有任何编译错误。
/// </remarks>
public enum PluginRiskTier
{
    /// <summary>只读：不弹窗，但仍进 AI 核验（前提是它真的只读）。</summary>
    Low = 0,

    /// <summary>有副作用、可逆：照常人工报批。</summary>
    Normal = 1,

    /// <summary>能执行任意命令 / 代码：人工报批 + 禁止一键放行。</summary>
    High = 2,
}

/// <summary>插件工具的执行面。</summary>
public enum PluginRunKind
{
    /// <summary>发一个 HTTP 请求。GET/HEAD 视为只读。</summary>
    Http,

    /// <summary>走 cmd / powershell 执行一条命令。</summary>
    Cmd,

    /// <summary>执行插件目录里的一个 .py 脚本。</summary>
    Python,
}

public enum PluginDiagnosticLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>一条装载诊断。</summary>
/// <param name="Source">出问题的插件 id 或目录名（用于在几十条诊断里定位）。</param>
public sealed record PluginDiagnostic(PluginDiagnosticLevel Level, string Source, string Message)
{
    public override string ToString() => $"[{Level}] {Source}: {Message}";
}

/// <summary>HTTP 执行面。</summary>
public sealed record PluginHttpSpec
{
    public required string Method { get; init; }

    public required string Url { get; init; }

    /// <summary>
    /// 允许访问的 host 列表。**必须显式声明，且请求的 host 必须命中**。
    /// </summary>
    /// <remarks>
    /// 为什么是白名单而不是黑名单：插件的 URL 是插件作者写的，
    /// 而插件可能来自任何地方。黑名单永远列不全（还有 IP 直连、302 跳转、DNS 重绑定），
    /// 白名单只要求作者说清「我要访问谁」—— 说不清就不许装。
    /// 支持一个 <c>*.example.com</c> 前缀通配（匹配子域，不匹配裸域）。
    /// </remarks>
    public required IReadOnlyList<string> Hosts { get; init; }

    /// <summary>URL 模板里出现的全部占位符（解析时提取，运行时逐个替换）。</summary>
    public required IReadOnlyList<string> Placeholders { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public required int TimeoutSeconds { get; init; }
}

/// <summary>命令执行面。</summary>
public sealed record PluginCmdSpec
{
    /// <summary>cmd 或 powershell。</summary>
    public required string Shell { get; init; }

    public required string Command { get; init; }

    /// <summary>相对工作台根的目录；空串表示工作台根。</summary>
    public required string WorkingDirectory { get; init; }

    public required int TimeoutSeconds { get; init; }
}

/// <summary>Python 执行面。</summary>
public sealed record PluginPythonSpec
{
    /// <summary>已解析的脚本绝对路径（解析时确保落在插件目录内且真实存在）。</summary>
    public required string ScriptPath { get; init; }

    public required string WorkingDirectory { get; init; }

    public required int TimeoutSeconds { get; init; }
}

/// <summary>执行面的联合体。</summary>
public sealed record PluginRunSpec
{
    public required PluginRunKind Kind { get; init; }

    public PluginHttpSpec? Http { get; init; }
    public PluginCmdSpec? Cmd { get; init; }
    public PluginPythonSpec? Python { get; init; }

    public string Describe() => Kind switch
    {
        PluginRunKind.Http => $"http {Http!.Method} {Http.Url}",
        PluginRunKind.Cmd => $"{Cmd!.Shell}: {Cmd.Command}",
        _ => $"python {Path.GetFileName(Python!.ScriptPath)}",
    };
}

/// <summary>插件里的一个工具定义（已通过全部校验）。</summary>
public sealed record PluginToolSpec
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>直接交给 LLM 的 JSON Schema（已 Clone，不依赖原 JsonDocument）。</summary>
    public required JsonElement Parameters { get; init; }

    public required PluginRunSpec Run { get; init; }

    /// <summary>参数名（保持声明顺序，便于诊断信息稳定）。</summary>
    public required IReadOnlyList<string> ParameterNames { get; init; }

    public required IReadOnlyList<string> RequiredParameters { get; init; }

    /// <summary>参数名 → 注入给 cmd / python 的环境变量名。</summary>
    /// <remarks>
    /// 为什么命令模板里**不许**直接插值：<c>{{code}}</c> 拼进命令行等于把插件参数
    /// 变成了命令的一部分，一个含 <c>&amp;</c> 的参数就能改变语义。
    /// 走环境变量则值不参与命令解析 —— 这是「危险能力自动选安全通道」的具体落点。
    /// </remarks>
    public required IReadOnlyDictionary<string, string> EnvironmentNames { get; init; }

    /// <summary>
    /// 是否只读。**由执行面推断，不接受插件自述**（自述只会更严格，见解析器）。
    /// </summary>
    public required bool IsReadOnly { get; init; }

    /// <summary>清单里显式写的风险档；写 auto 或没写时为 null。</summary>
    public PluginRiskTier? DeclaredRisk { get; init; }

    /// <summary>最终生效的风险档（推断与声明取严）。</summary>
    public required PluginRiskTier EffectiveRisk { get; init; }

    public bool HasSideEffects => !IsReadOnly;

    public string RiskLabel => EffectiveRisk switch
    {
        PluginRiskTier.Low => "低（只读）",
        PluginRiskTier.Normal => "中（写操作，需报批）",
        _ => "高（可执行任意代码，需报批且禁止一键放行）",
    };
}

/// <summary>一个已装载的插件。</summary>
public sealed record PluginManifest
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string Description { get; init; }

    /// <summary>宿主能力声明（net / workspace / python）。</summary>
    public required IReadOnlyList<string> Requires { get; init; }

    public required IReadOnlyList<PluginToolSpec> Tools { get; init; }

    public required string DirectoryPath { get; init; }

    public required string ManifestPath { get; init; }

    /// <summary>
    /// 插件声明的「归属我的配置」；清单里没写 settings 时为 null。
    /// </summary>
    /// <remarks>
    /// 有这一块的插件才会在插件栏里出现齿轮、也才会参与模型发现。
    /// 没有的（例如纯 cmd 插件）不该被硬塞一个空的设置页 ——
    /// 空设置页比没有设置页更让人困惑。
    /// </remarks>
    public PluginSettingsSpec? Settings { get; init; }

    /// <summary>是否来自程序自带目录（用户目录里的同名插件优先）。</summary>
    public bool BuiltIn { get; init; }
}

/// <summary>某个插件工具的风险信息（供 <see cref="Safety.ApprovalPolicy"/> 查询）。</summary>
public sealed record PluginRiskInfo(
    string PluginId,
    PluginRiskTier Tier,
    bool ReadOnly,
    string Description);

/// <summary>
/// 插件风险表 —— <see cref="Safety.ApprovalPolicy"/> 通过它认识「运行时才出现的工具名」。
/// </summary>
/// <remarks>
/// 为什么需要这层抽象，而不是把插件工具名直接塞进 <c>ApprovalPolicy</c> 的三张 HashSet：
/// 那三张表在构造时就固定了，而插件是**运行时**扫描磁盘后才存在的；
/// 更麻烦的是 <c>ToolPolicyCoverageTests.PolicyNames_AllReferToRealTools</c>
/// 要求「表里的每个名字都必须对应程序集里真实存在的工具」——
/// 插件名若写进静态表，那条测试会把它当成拼错的工具名而变红。
/// 所以静态表继续管内置工具，插件走这个动态查询。
/// </remarks>
public interface IPluginRiskTable
{
    PluginRiskInfo? Lookup(string toolName);
}

/// <summary>命名约定（工具名 / 环境变量名）。</summary>
public static class PluginNaming
{
    public const string EnvPrefix = "FATFISH_ARG_";

    /// <summary>插件可用的全部环境变量名。</summary>
    public static string EnvName(string parameter)
    {
        var sb = new StringBuilder(EnvPrefix.Length + parameter.Length);
        sb.Append(EnvPrefix);

        foreach (var ch in parameter.ToUpperInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');

        return sb.ToString();
    }
}
