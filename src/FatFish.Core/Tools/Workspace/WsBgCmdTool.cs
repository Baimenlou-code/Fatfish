using System.Text.Json;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_bg_cmd —— 把一条命令丢到后台跑，立即返回。
///
/// 与 <see cref="WsRunCmdTool"/> 的全部差别只有「等不等」：
/// <c>ws_run_cmd</c> 同步等进程结束（上限 600 秒），期间整个回合被占住；
/// <c>ws_bg_cmd</c> 启动后立刻返回 job id，回合继续，用户还能接着说话。
///
/// 但**审批要求完全一致**，这一点刻意没有放松：
/// 后台任务不是「更安全的 ws_run_cmd」，它只是「更晚执行、更久运行」。
/// 它照样能 <c>del</c> 工作台外面、照样能 <c>curl</c> 发数据出去。
/// 如果因为「它只是启动一下」而免掉审批，那闸门就是漏的 ——
/// 攻击者只需要把危险命令包进后台任务里。
///
/// 所以三个约束一个都不少：
///   · 登记在 <c>HighRiskTools</c>（必弹窗）；
///   · 登记在 <c>NeverAutoApprove</c>（即使用户选过「本会话放行」也逐个确认）；
///   · 审批窗口里看到的是完整命令原文。
///
/// 另有一条只有后台工具才有的检查：<c>timeout_seconds</c> 默认 0（不限时）。
/// 因为「后台」的典型用途就是跑几小时的东西，给个默认超时等于让它悄悄死掉。
/// </summary>
public sealed class WsBgCmdTool : ITool
{
    /// <summary>上限 24 小时：再长就该用计划任务，而不是一个对话里的后台任务。</summary>
    private const int HardMaxTimeoutSeconds = 24 * 60 * 60;

    public string Name => "ws_bg_cmd";

    public string Description =>
        "在后台执行一条命令，**立即返回 job_id**，不阻塞当前回合。需要人工审批。" +
        "适合预计要跑几分钟到几小时的任务（下载、批处理、构建、全市场扫描）。" +
        "输出会写进缓冲区（保留最近 4MB）：用 ws_bg_tail 增量读取，ws_bg_list 看状态，" +
        "ws_bg_kill 中止。任务结束时程序会自动播报结果。" +
        "timeout_seconds 默认 0 表示不限时；设为正数则到时强制结束（含子进程）。" +
        "命令按批处理语法执行，引号与多行都能正常工作。" +
        "只是要立刻拿到结果、几十秒内能跑完的命令，用 ws_run_cmd 更直接。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("command", Schema.String("要执行的命令原文")),
            ("desc", Schema.String("任务简述（展示用，例如「全市场因子重算」）。缺省取命令前 40 字")),
            ("cwd", Schema.String("工作目录（相对工作台根目录）。缺省为工作台根目录")),
            ("timeout_seconds", Schema.Integer(
                "超时秒数。0（默认）表示不限时", 0, HardMaxTimeoutSeconds)),
        ],
        "command");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("command", out var command, out var err))
            return ValueTask.FromResult(err);

        if (context.Jobs is null)
            return ValueTask.FromResult(ToolResult.Failure("后台任务管理器未初始化，无法启动后台任务"));

        var cwdRel = args.GetString("cwd") ?? string.Empty;
        var timeoutSeconds = Math.Clamp(
            args.GetInt("timeout_seconds", 0), 0, HardMaxTimeoutSeconds);

        var cwd = context.Guard.Resolve(cwdRel);

        if (!Directory.Exists(cwd))
        {
            return ValueTask.FromResult(ToolResult.Failure(File.Exists(cwd)
                ? $"{cwdRel} 是文件，不能作为工作目录"
                : $"工作目录不存在：{cwdRel}"));
        }

        // 与 ws_run_cmd 同一条路：写临时 .bat（cmd）或 -EncodedCommand（powershell）。
        // 后台任务如果单开一份，引号那个坑就会在这里复活，而且更难发现 ——
        // 出问题的时候人已经离开键盘了。
        var launch = CmdShell.Build(command, "cmd", cwd, context.Guard);

        if (launch.Psi is null)
            return ValueTask.FromResult(ToolResult.Failure(launch.Error ?? "无法组装命令行"));

        var desc = BuildDesc(args.GetString("desc"), command);
        var psi = launch.Psi;
        var tempFile = launch.TempFile;

        var jobId = context.Jobs.Start(new BackgroundJobRequest(
            Kind: "cmd",
            Desc: desc,
            TimeoutSeconds: timeoutSeconds,
            Work: async (handle, jobCt) =>
            {
                try
                {
                    var run = await ProcessRunner.RunStreamingAsync(
                        psi,
                        timeout: null,          // 超时由 BackgroundJobManager 统一控制
                        onStdout: block => handle.Append(block.Span),
                        onStderr: block => handle.Append(block.Span),
                        ct: jobCt).ConfigureAwait(false);

                    if (run.StartError is not null)
                        handle.Note("进程启动失败：" + run.StartError);

                    return run.ExitCode ?? -1;
                }
                finally
                {
                    // 任务结束（含被 Kill）才删临时 .bat。
                    CmdShell.Cleanup(tempFile);
                }
            }));

        context.Log?.Invoke($"[后台] {jobId} 启动：{desc}");

        return ValueTask.FromResult(ToolResult.Success(new
        {
            job_id = jobId,
            kind = "cmd",
            desc,
            status = "running",
            cwd = context.Guard.ToRelative(cwd),
            timeout_seconds = timeoutSeconds,
        }, notice: "已启动，不阻塞本回合。用 ws_bg_tail 读增量输出、ws_bg_list 看状态、ws_bg_kill 中止。"));
    }

    private static string BuildDesc(string? given, string command)
    {
        if (!string.IsNullOrWhiteSpace(given))
            return given.Trim();

        var oneLine = command.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 40 ? oneLine : oneLine[..40] + "…";
    }
}
