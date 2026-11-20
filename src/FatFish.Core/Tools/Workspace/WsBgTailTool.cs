using System.Text.Json;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_bg_tail —— 增量读取后台任务的输出。
///
/// 只读：读的是内存里那块缓冲区，不碰文件系统。所以不需要审批。
/// （缓冲区里的内容来自任务自己，而那个任务在启动时已经由用户批准过了。）
///
/// 核心是 **offset 是绝对字节位置**，不是「读取次数」：
/// 模型第一次读拿到 <c>next_offset=1024</c>，下次就该带 1024。
/// 如果 offset 用「第几次读」，中间漏一次就永久错位，而且错得毫无提示。
///
/// 三件事必须让模型知道，否则它会做出错误判断：
///   1. <c>has_more=true</c>  —— 还有没读完的，别以为就读到这里了；
///   2. <c>running=false</c>  —— 任务结束了，不用再等；
///   3. <c>dropped_at_start=true</c> —— 你要的位置太早，前面已被丢弃，
///      **不要**再用同一个 offset 重试（那会得到同样的结果，然后死循环）。
/// </summary>
public sealed class WsBgTailTool : ITool
{
    private const int DefaultMaxChars = 4000;
    private const int HardMaxChars = 40_000;

    public string Name => "ws_bg_tail";

    public string Description =>
        "读取后台任务的增量输出。用 offset 做游标：第一次传 0，之后传上次返回的 next_offset，" +
        "就只会拿到新增内容，不会把几小时的输出反复塞进上下文。" +
        "同时返回任务状态与 total_bytes。只读，不需要审批。" +
        "默认最多返回 4000 字符，可用 max_chars 调整（上限 40000）。" +
        "若任务已结束且想一次看到尾部，也可以传一个很大的 offset 之外的办法：" +
        "先用 ws_bg_list 看输出总量，再从接近末尾的位置开始读。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("job_id", Schema.String("任务 id（ws_bg_cmd / ws_bg_python 返回的 job_id）")),
            ("offset", Schema.Integer("从第几字节开始读。首次传 0，之后传上次的 next_offset", 0, null)),
            ("max_chars", Schema.Integer(
                $"本次最多返回多少字符，默认 {DefaultMaxChars}", 1, HardMaxChars)),
        ],
        "job_id");

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("job_id", out var jobId, out var err))
            return ValueTask.FromResult(err);

        if (context.Jobs is null)
            return ValueTask.FromResult(ToolResult.Failure("后台任务管理器未初始化"));

        var snapshot = context.Jobs.Get(jobId);

        if (snapshot is null)
        {
            return ValueTask.FromResult(ToolResult.Failure(
                $"没有这个任务：{jobId}。用 ws_bg_list 查看现有任务 id。"));
        }

        // offset 用 long：JsonArgs 只有 GetInt。超过 2GB 的输出极罕见，
        // 但 offset 是 long，这里按 int 取会让溢出静默变成负数。
        var offset = (long)Math.Max(0, args.GetInt("offset", 0));
        var maxChars = Math.Clamp(args.GetInt("max_chars", DefaultMaxChars), 1, HardMaxChars);

        var tail = context.Jobs.Tail(jobId, offset, maxChars);

        var notices = new List<string>(3);

        if (tail.DroppedAtStart)
            notices.Add("请求的 offset 太早，前面的输出已被缓冲区丢弃（只保留最近一段）");

        if (tail.HasMore)
            notices.Add($"还有内容没读完，用 offset={tail.NextOffset} 继续");

        if (snapshot.Status != BackgroundJobManager.StatusRunning && tail.Text.Length == 0 && !tail.HasMore)
            notices.Add("任务已结束，没有更多输出");

        if (snapshot.Error is not null)
            notices.Add("任务错误：" + snapshot.Error);

        return ValueTask.FromResult(ToolResult.Success(new
        {
            job_id = snapshot.Id,
            kind = snapshot.Kind,
            desc = snapshot.Desc,
            status = snapshot.Status,
            running = snapshot.Status == BackgroundJobManager.StatusRunning,
            elapsed_ms = snapshot.ElapsedMs,
            exit_code = snapshot.ExitCode,
            text = tail.Text,
            next_offset = tail.NextOffset,
            total_bytes = tail.TotalBytes,
            has_more = tail.HasMore,
            dropped_at_start = tail.DroppedAtStart,
        }, notice: notices.Count == 0 ? null : string.Join("；", notices)));
    }
}
