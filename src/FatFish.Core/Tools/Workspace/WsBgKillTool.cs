using System.Text.Json;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_bg_kill —— 中止一个后台任务（连同它起的全部子进程）。
///
/// 有副作用，需要审批，且禁止一键放行。
///
/// 为什么一个「只是把一个任务停下来」的动作要这么严：
/// 因为它**不是**一个温和的停止。它做的是 <c>Kill(entireProcessTree: true)</c>，
/// 也就是把整棵进程树直接掐死 —— 正在写的数据文件会留在半截、
/// 正在跑的数据库事务会回滚、下载了一半的文件会不可用。
/// 「中止」听起来无害，实际影响面取决于那个任务当时在干什么，
/// 而这个决定值得用户亲眼看一眼。
///
/// 另一条理由更直接：如果不审批，模型可以一秒内连发 kill 把所有任务清空，
/// 然后声称「任务失败了」。审批让这个动作留下痕迹。
/// </summary>
public sealed class WsBgKillTool : ITool
{
    public string Name => "ws_bg_kill";

    public string Description =>
        "中止一个后台任务，连同它的全部子进程。需要人工审批，且每次都要单独确认。" +
        "注意这是强制终止：任务正在写的文件会停在半截。谨慎使用。" +
        "返回是否成功发出中止请求；实际状态用 ws_bg_list 确认。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("job_id", Schema.String("要中止的任务 id")),
            ("reason", Schema.String("中止原因（会记录到任务输出里）")),
        ],
        "job_id");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("job_id", out var jobId, out var err))
            return ValueTask.FromResult(err);

        if (context.Jobs is null)
            return ValueTask.FromResult(ToolResult.Failure("后台任务管理器未初始化"));

        var before = context.Jobs.Get(jobId);

        if (before is null)
        {
            return ValueTask.FromResult(ToolResult.Failure(
                $"没有这个任务：{jobId}。用 ws_bg_list 查看现有任务 id。"));
        }

        if (before.Status != BackgroundJobManager.StatusRunning)
        {
            return ValueTask.FromResult(ToolResult.Success(new
            {
                job_id = jobId,
                status = before.Status,
                killed = false,
            }, notice: $"任务已经结束（{before.Status}），无需中止。"));
        }

        var reason = args.GetString("reason");
        var ok = context.Jobs.Kill(jobId, reason ?? "用户中止");

        context.Log?.Invoke(ok ? $"[后台] {jobId} 已请求中止" : $"[后台] {jobId} 中止失败");

        var after = context.Jobs.Get(jobId);

        return ValueTask.FromResult(ToolResult.Success(new
        {
            job_id = jobId,
            status = after?.Status ?? before.Status,
            killed = ok,
        }, notice: ok
            ? "已发出中止请求（会杀掉整棵进程树）。状态是异步变化的，稍后用 ws_bg_list 或 ws_bg_tail 确认。"
            : "中止请求没有被接受（任务可能刚好结束）。用 ws_bg_list 看当前状态。"));
    }
}
