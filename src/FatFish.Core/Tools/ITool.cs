using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools;

/// <summary>
/// 工具执行结果。
///
/// 对应 Python 版 ws_* 系列统一返回的那个 dict：
///   {ok, data, error, elapsed_ms, ...}
/// 这里用 record，让「成功/失败」在类型层面就可区分。
/// </summary>
public sealed record ToolResult
{
    public required bool Ok { get; init; }

    /// <summary>结构化数据。成功时通常非 null。</summary>
    public JsonElement? Data { get; init; }

    /// <summary>失败原因。Ok=false 时应非空。</summary>
    public string? Error { get; init; }

    /// <summary>耗时（毫秒），由 ToolRegistry 统一填充。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>是否需要在结果中附加提示（例如「已备份到 _backup/」）。</summary>
    public string? Notice { get; init; }

    public static ToolResult Success(JsonElement? data = null, string? notice = null)
        => new() { Ok = true, Data = data, Notice = notice };

    public static ToolResult Success<T>(T value, JsonSerializerOptions? options = null, string? notice = null)
        => new()
        {
            Ok = true,
            Data = JsonSerializer.SerializeToElement(value, options ?? ToolJson.DefaultOptions),
            Notice = notice,
        };

    public static ToolResult Failure(string error)
        => new() { Ok = false, Error = error };
}

/// <summary>
/// 工具协议。
///
/// 这是整个内核与 UI、以及内核与 LLM 之间的契约。
/// Python 版里对应的是 workspace.py 的 TOOL_SCHEMAS + _DISPATCH 字典，
/// 但那是「用字典模拟接口」；C# 版把它变成真正的类型。
/// </summary>
public interface ITool
{
    /// <summary>工具名，必须与 LLM 看到的 function name 完全一致。</summary>
    string Name { get; }

    /// <summary>给 LLM 看的描述。措辞会显著影响调用正确率，别随意改。</summary>
    string Description { get; }

    /// <summary>
    /// 参数的 JSON Schema（type=object 那一段的 properties）。
    /// 由 <see cref="ToolRegistry"/> 组装成 OpenAI tools 数组。
    /// </summary>
    JsonElement ParametersSchema { get; }

    /// <summary>是否只读 —— 只读工具不参与静态安全审查，可直接执行。</summary>
    bool IsReadOnly { get; }

    /// <summary>是否涉及副作用（写盘、执行命令等）。</summary>
    bool HasSideEffects { get; }

    /// <summary>执行。</summary>
    ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct);
}

/// <summary>
/// 工具执行上下文 —— 工具能接触到的全部依赖都从这里注入。
///
/// 这样做的好处：任何工具都无法绕过 <see cref="PathGuard"/> 直接摸文件系统，
/// 也无法偷偷签发读凭证。
/// </summary>
public sealed class ToolContext
{
    private PathGuard? _guard;
    private ReadTicketStore? _tickets;

    /// <summary>
    /// 工作台会话（可切换根目录）。为 null 时表示「根固定」，<see cref="Guard"/> 用自己那份。
    /// </summary>
    /// <remarks>
    /// ★ 为什么工具每次都通过 <c>context.Guard</c> 现取，而不是自己在构造函数里存一份：
    ///   ws_cd 一旦把根换掉，**所有**持有旧 Guard 的工具都会继续按旧根解析路径 ——
    ///   看起来一切正常，实际写到了另一个目录。这种错不会有任何异常，
    ///   只会在事后发现「文件怎么不见了」。
    ///
    ///   所以下面两个属性做成「优先取会话、否则取自带」的计算属性：
    ///   换根之后，任何地方读 <c>context.Guard</c> 拿到的都是新的。
    /// </remarks>
    public WorkspaceSession? Workspace { get; init; }

    /// <summary>
    /// 当前生效的路径守卫。
    ///
    /// 设置了 <see cref="Workspace"/> 时它跟随会话（换根后自动变新）；
    /// 否则用初始化时给的这一份。
    /// </summary>
    public required PathGuard Guard
    {
        get => Workspace?.Guard ?? _guard
               ?? throw new InvalidOperationException("ToolContext 必须设置 Guard 或 Workspace");
        init => _guard = value;
    }

    /// <summary>当前生效的读凭证库（同样跟随 <see cref="Workspace"/>）。</summary>
    public required ReadTicketStore Tickets
    {
        get => Workspace?.Tickets ?? _tickets
               ?? throw new InvalidOperationException("ToolContext 必须设置 Tickets 或 Workspace");
        init => _tickets = value;
    }

    public required Safety.ApprovalPolicy Approval { get; init; }

    /// <summary>后台任务管理器（供 bg_* 系列工具使用）；可为 null。</summary>
    /// <remarks>
    /// 类型必须是接口 <see cref="IBackgroundJobManager"/>：
    /// 具体实现（进程内任务 / 独立 Job 对象）在 App 层注入，
    /// Core 不应该知道实现细节。
    /// </remarks>
    public IBackgroundJobManager? Jobs { get; init; }

    /// <summary>
    /// 联网服务（web_search / web_fetch / net_balance 用）；可为 null。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Jobs"/> 同一个理由：联网服务持有 HttpClient 与限速桶，
    /// 是**有状态、该被复用**的东西。如果每个工具自己 new 一个，
    /// 那三个工具就是三个 HttpClient、三套限速桶 —— 而官方的 5 QPS 是账号级的，
    /// 三套桶各算各的，等于把限速放大了三倍。
    ///
    /// 为 null 时工具会自己从环境变量装配一个（CLI 之外的宿主可能不注入），
    /// 但那条路径会丢掉台账里的历史成功率调权 —— 所以正式宿主应当注入。
    /// </remarks>
    public FatFish.Core.Net.NetService? Net { get; init; }

    /// <summary>
    /// 插件宿主；为 null 表示本进程没有插件。
    /// </summary>
    /// <remarks>
    /// 只有 <c>ws_plugin_*</c> 三个工具真的用它。之所以放在这里而不是做成全局单例：
    /// <see cref="ToolContext"/> 的约定是「本次调用能接触到的全部依赖都从这儿注入」，
    /// 这个约定一旦破例，后面就会有第二个、第三个全局单例 ——
    /// 而每一个都意味着工具可以在测试里偷偷摸到真实环境。
    /// </remarks>
    public FatFish.Core.Plugins.PluginHost? Plugins { get; init; }

    /// <summary>写操作审批后的回调（UI 层实现：弹窗确认）。</summary>
    public Func<ApprovalRequest, CancellationToken, ValueTask<bool>>? RequestApproval { get; init; }

    /// <summary>日志回调。</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>一次审批请求。</summary>
/// <param name="Tool">工具名。</param>
/// <param name="Args">参数原文（已脱敏的展示用副本）。</param>
/// <param name="Reason">为什么需要报批。</param>
/// <param name="AllowsOneKey">是否允许「本次会话一键放行」。</param>
public sealed record ApprovalRequest(
    string Tool,
    IReadOnlyDictionary<string, JsonElement> Args,
    string Reason,
    bool AllowsOneKey,
    ApprovalKind Kind = ApprovalKind.None);

/// <summary>
/// 后台任务管理接口（具体实现见 <c>Tools/BackgroundJobManager.cs</c>）。
///
/// ★ 为什么需要这一层抽象：ws_bg_* 五个工具要能在**不阻塞对话回合**的前提下
///   启动进程、查进度、读增量输出、中止。这四件事都需要跨调用保存状态，
///   而工具本身是「一次调用一个对象、无状态」的。状态必须放在一个比工具活得久的地方。
///
/// 参数形状比最初的设计多了一个 <see cref="IBackgroundJobHandle"/>：
/// 任务的输出要边跑边写进缓冲区（否则 ws_bg_tail 只能等跑完才有内容，
/// 那就完全失去意义了），而 work 委托需要拿到「往哪儿写」。
/// 用回调闭包捕获也可以，但那样每个工具都得自己管缓冲区、自己处理越界，
/// 迟早出现三份不一样的实现。
/// </summary>
public interface IBackgroundJobManager
{
    /// <summary>启动一个后台任务，**立即返回** job id（不等任务完成）。</summary>
    string Start(BackgroundJobRequest request);

    /// <summary>按启动顺序列出全部任务（含已结束的，最多保留若干个）。</summary>
    IReadOnlyList<BackgroundJobSnapshot> List();

    /// <summary>取单个任务快照；id 不存在时返回 null。</summary>
    BackgroundJobSnapshot? Get(string jobId);

    /// <summary>
    /// 读增量输出。
    /// </summary>
    /// <param name="jobId">任务 id。</param>
    /// <param name="offset">从第几**字节**开始读（绝对位置，上次返回值里的 NextOffset）。</param>
    /// <param name="maxChars">本次最多返回多少字符。</param>
    BackgroundTailResult Tail(string jobId, long offset, int maxChars);

    /// <summary>中止任务（连同其子进程）。</summary>
    bool Kill(string jobId, string reason);

    /// <summary>任务结束（含失败、超时、被中止）时触发，供宿主播报。</summary>
    event Action<BackgroundJobSnapshot>? Finished;
}

/// <summary>一次后台任务的启动请求。</summary>
/// <param name="Kind">种类（cmd / python），用于展示与播报。</param>
/// <param name="Desc">人读的简述（例如「全市场因子重算」）。</param>
/// <param name="TimeoutSeconds">超时秒数；0 表示不限时。</param>
/// <param name="Work">
/// 真正要跑的东西。返回值是退出码。
/// 取消信号由管理器给出（超时或 Kill），实现里必须把它传导到子进程，
/// 否则「中止」只会让任务从列表里消失，进程照样在跑。
/// </param>
public sealed record BackgroundJobRequest(
    string Kind,
    string Desc,
    int TimeoutSeconds,
    Func<IBackgroundJobHandle, CancellationToken, Task<int>> Work);

/// <summary>传给后台任务的执行句柄。</summary>
public interface IBackgroundJobHandle
{
    string Id { get; }

    /// <summary>向输出缓冲区追加原始字节（stdout / stderr 混在一起，按到达顺序）。</summary>
    /// <remarks>
    /// 参数是 <see cref="ReadOnlySpan{T}"/> 而不是 string：进程输出是**原始字节**，
    /// 中途分块时按字符串解码会把中文切成半个字符。
    /// 存字节、读的时候再整体解码，就不会有这个问题。
    /// </remarks>
    void Append(ReadOnlySpan<byte> bytes);

    /// <summary>打一条提示进输出流（例如「临时脚本已删除」）。</summary>
    void Note(string text);
}

/// <summary>任务快照。</summary>
public sealed record BackgroundJobSnapshot(
    string Id,
    string Kind,
    string Desc,
    string Status,
    DateTimeOffset StartedAt,
    long OutputBytes,
    long ElapsedMs = 0,
    int? ExitCode = null,
    string? Error = null,
    int TimeoutSeconds = 0,
    DateTimeOffset? EndedAt = null);

/// <summary>一次增量读取的结果。</summary>
/// <param name="Text">本次读到的文本。</param>
/// <param name="NextOffset">下次应从这里继续（绝对字节位置）。</param>
/// <param name="TotalBytes">任务累计输出总字节数。</param>
/// <param name="DroppedAtStart">请求的 offset 太早，前面那段已被缓冲区丢弃。</param>
/// <param name="HasMore">还有没读完的内容（`max_chars` 用尽）。</param>
public sealed record BackgroundTailResult(
    string Text,
    long NextOffset,
    long TotalBytes,
    bool DroppedAtStart = false,
    bool HasMore = false);
