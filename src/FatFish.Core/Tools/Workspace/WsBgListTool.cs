using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_bg_list —— 列出后台任务。
///
/// 只读：它只读内存里的任务表，不碰文件系统、不启动任何东西。
/// 所以不参与审批 —— 让它也弹窗，只会训练用户在没看清内容时点「允许」，
/// 那比不弹更糟。
///
/// 输出刻意用**文本表格**而不是 JSON 数组：
/// 这个结果会进入后续每一次请求的上下文。JSON 里
/// <c>{"id":..,"kind":..,"status":..,"desc":..}</c> 的字段名会在每条记录里重复一遍，
/// 十个任务就是几十个纯浪费的 token，而表格用行尾对齐表达同样的信息。
/// 这与 ws_list / ws_search 的选择是一致的。
/// </summary>
public sealed class WsBgListTool : ITool
{
    public string Name => "ws_bg_list";

    public string Description =>
        "列出后台任务（含已结束的），显示 id、状态、已运行时长、种类、简述与输出字节数。" +
        "配合 ws_bg_tail 读输出、ws_bg_kill 中止。只读，不需要审批。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("status", Schema.String(
                "只看某种状态：all（默认）/ running / done / failed / timeout / killed",
                ["all", "running", "done", "failed", "timeout", "killed"])),
        ]);

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (context.Jobs is null)
            return ValueTask.FromResult(ToolResult.Failure("后台任务管理器未初始化"));

        var filter = (args.GetString("status") ?? "all").Trim().ToLowerInvariant();
        var all = context.Jobs.List();

        var jobs = filter is "all" or ""
            ? all
            : all.Where(j => string.Equals(j.Status, filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var sb = new StringBuilder();

        if (jobs.Count == 0)
        {
            sb.Append(all.Count == 0
                ? "（没有后台任务）"
                : $"（没有状态为 {filter} 的任务；共 {all.Count} 个）");
        }
        else
        {
            sb.AppendLine("id                            状态      已运行   种类    输出    简述");

            foreach (var j in jobs)
            {
                sb.Append(j.Id.PadRight(30));
                sb.Append(Mark(j.Status).PadRight(10));
                sb.Append(Duration(j.ElapsedMs).PadRight(9));
                sb.Append(j.Kind.PadRight(8));
                sb.Append(WorkspaceFilters.HumanSize(j.OutputBytes).PadRight(8));
                sb.AppendLine(Trim(j.Desc, 40));

                if (j.Error is not null)
                    sb.AppendLine("    错误：" + j.Error);

                if (j.Status == BackgroundJobManager.StatusRunning && j.TimeoutSeconds > 0)
                    sb.Append("    超时设置：")
                      .Append(j.TimeoutSeconds.ToString(CultureInfo.InvariantCulture))
                      .AppendLine(" 秒");
            }

            var running = jobs.Count(j => j.Status == BackgroundJobManager.StatusRunning);
            sb.Append("共 ").Append(jobs.Count.ToString(CultureInfo.InvariantCulture)).Append(" 个任务");

            if (running > 0)
            {
                sb.Append('（').Append(running.ToString(CultureInfo.InvariantCulture)).Append(" 个在跑）");
            }
        }

        return ValueTask.FromResult(ToolResult.Success(new
        {
            count = jobs.Count,
            running = jobs.Count(j => j.Status == BackgroundJobManager.StatusRunning),
            list = sb.ToString().TrimEnd(),
        }));
    }

    /// <summary>状态标记。用符号而不是纯文字：一眼能扫出哪个出问题了。</summary>
    private static string Mark(string status) => status switch
    {
        "running" => "⏳ running",
        "done" => "✅ done",
        "failed" => "❌ failed",
        "timeout" => "⏰ timeout",
        "killed" => "⛔ killed",
        _ => status,
    };

    private static string Duration(long ms) => ms switch
    {
        < 0 => "?",
        < 1000 => ms.ToString(CultureInfo.InvariantCulture) + "ms",
        < 60_000 => (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s",
        < 3_600_000 => (ms / 60_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "m",
        _ => (ms / 3_600_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "h",
    };

    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
