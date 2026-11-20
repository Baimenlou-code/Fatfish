using System.Text.Json;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_run_cmd —— 在工作台内执行一条命令。
///
/// 对应 Python 版 workspace.py::ws_run_cmd。
///
/// 这是整个工具集里**唯一能突破工作台边界**的工具：
/// 命令说什么就做什么，路径守卫管不住 `cd ..`、
/// 管不住 `del %USERPROFILE%\*`，也管不住 `curl` 往外发数据。
/// 因此它的安全模型只有一道，但必须可靠：
///   · 必须人工审批（ApprovalPolicy.HighRiskTools）
///   · 禁止「一键放行」（NeverAutoApprove）
///   · 审批窗口里展示**完整的命令行原文**，让用户看清再点
/// 除此之外不做「黑名单关键词拦截」—— 那种东西既能被轻松绕过，
/// 又会给人虚假的安全感，比不做更糟。
///
/// 进程执行本身（超时杀树、原始字节解码、输出限流不死锁）全部交给
/// <see cref="ProcessRunner"/> —— 它与 ws_run_python 共用同一份实现。
/// 本类只负责「把命令翻译成 ProcessStartInfo」和「把结果翻译成工具结果」。
///
/// 两个实现上的坑（实测出来的，不是查文档猜的）：
///
/// 1) **输出是 GBK 不是 UTF-8**，而且 <c>chcp 65001</c> 也改不掉。
///    实测「裸 dir」与「chcp 65001 后 dir」输出的字节完全一致（2846 字节，全 GBK）。
///    所以不能靠切代码页，只能拿到原始字节后自己解码 —— 见 <see cref="ProcessEncoding"/>。
///
/// 2) **引号嵌套**。<c>cmd /s /c "&lt;原文&gt;"</c> 在命令自身含双引号时会被拆错，
///    而且**不报错** —— 命令安静地少做了半件事，退出码还是 0。
///    现在改为「先写临时 .bat 再执行」，命令原文不再经过任何命令行解析。
///    完整原因与三档编码策略见 <see cref="CmdShell"/>。
///    PowerShell 走 <c>-EncodedCommand</c>（Base64 / UTF-16LE），本来就完全免疫。
///
/// ★ 2026-11-05 改：输出改为「全量落盘 + 行内头尾」。
///   起因是发现 <see cref="ProcessRunner.RunAsync"/> 的 24KB 上限只保留**开头**，
///   而命令的结论几乎总在尾部（traceback、退出原因、汇总）。模型看到一片正常日志，
///   会把失败读成成功 —— 这比"信息少"严重得多，属于误导。
///   现在：内存里恒留头尾各 24KB，完整输出落到 logs/tool_output/*.log，
///   结果里给 full_output 路径，模型可自行 ws_search / ws_read。
/// </summary>
public sealed class WsRunCmdTool : ITool
{
    private const int DefaultTimeoutSeconds = 60;
    private const int HardMaxTimeoutSeconds = 600;

    public string Name => "ws_run_cmd";

    public string Description =>
        "在工作台目录下执行一条命令（Windows cmd，或 powershell），返回退出码与输出。需要人工审批。" +
        "工作目录默认为工作台根目录，可用 cwd 指定子目录（不能超出工作台）。" +
        "命令超时会被强制结束（含子进程）。" +
        "只读的目录浏览优先用 ws_list、内容检索优先用 ws_search，它们不需要审批。" +
        "命令按批处理语法执行，因此引号和多行（用换行分隔）都能正常工作。" +
        "需要跑一段 Python 代码（而不是一条命令）时，用 ws_run_python 更省事。" +
        "预计要跑几分钟以上（下载、训练、批处理）时用 ws_bg_cmd —— 它立即返回，不占住本回合。" +
        "输出为中文时编码会自动识别，一般无需指定 encoding。" +
        "输出很长时，结果里的 stdout / stderr 只含头尾各 24KB，完整内容在 full_output 指向的文件里。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("command", Schema.String("要执行的命令原文。走 cmd 时不要自己再加外层引号")),
            ("cwd", Schema.String("工作目录（相对工作台根目录）。缺省为工作台根目录")),
            ("timeout_seconds", Schema.Integer(
                $"超时秒数，默认 {DefaultTimeoutSeconds}，上限 {HardMaxTimeoutSeconds}",
                1, HardMaxTimeoutSeconds)),
            ("shell", Schema.String("解释器：cmd（默认）或 powershell", ["cmd", "powershell"])),
            ("encoding", Schema.String(
                "输出解码方式：auto（默认，自动识别）/ utf-8 / gbk / oem",
                ["auto", "utf-8", "gbk", "oem"])),
        ],
        "command");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public async ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("command", out var command, out var err))
            return err;

        var cwdRel = args.GetString("cwd") ?? string.Empty;
        var shell = (args.GetString("shell") ?? "cmd").Trim().ToLowerInvariant();
        var encodingMode = args.GetString("encoding");
        var timeoutSeconds = Math.Clamp(
            args.GetInt("timeout_seconds", DefaultTimeoutSeconds), 1, HardMaxTimeoutSeconds);

        // ---- 1. 工作目录必须落在工作台内 ----
        var cwd = context.Guard.Resolve(cwdRel);

        if (!Directory.Exists(cwd))
        {
            return ToolResult.Failure(File.Exists(cwd)
                ? $"{cwdRel} 是文件，不能作为工作目录"
                : $"工作目录不存在：{cwdRel}");
        }

        // ---- 2. 组装进程 ----
        // 命令原文交给 CmdShell 写成临时 .bat（cmd）或 -EncodedCommand（powershell），
        // 详见 CmdScript.cs 里「为什么 cmd 要走临时 .bat」。
        var launch = CmdShell.Build(command, shell, cwd, context.Guard);

        if (launch.Psi is null)
            return ToolResult.Failure(launch.Error ?? $"shell 只支持 cmd 或 powershell，实际是 '{shell}'");

        context.Log?.Invoke($"[执行] {command}");

        // ---- 3. 跑（全量落盘 + 内存里恒留头尾）----
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

        using var capture = new ToolOutputCapture(
            Path.Combine(context.Guard.Root, "logs", "tool_output"), $"cmd_{stamp}");

        ProcessRunResult run;

        try
        {
            run = await ProcessRunner.RunStreamingAsync(
                launch.Psi,
                TimeSpan.FromSeconds(timeoutSeconds),
                onStdout: capture.WriteStdout,
                onStderr: capture.WriteStderr,
                ct: ct).ConfigureAwait(false);
        }
        finally
        {
            // 临时 .bat 无论成功、超时还是取消都要删掉。
            // 删不掉也不影响结果：进程刚被杀时文件句柄可能还没释放，
            // 而 .fatfish_tmp 本来就在忽略名单里。
            CmdShell.Cleanup(launch.TempFile);
        }

        if (run.StartError is not null)
            return ToolResult.Failure(run.StartError);

        if (run.Cancelled)
        {
            return ToolResult.Failure(
                $"命令已被用户取消（已运行 {run.ElapsedMs}ms）。") with
            {
                ElapsedMs = run.ElapsedMs,
            };
        }

        // ---- 4. 结果 ----
        var (outHead, outTail, outPath, outTotal) = capture.SnapshotStdout(encodingMode);
        var (errHead, errTail, _, errTotal) = capture.SnapshotStderr(encodingMode);

        var fullPath = outPath is not null && outTotal > 0
            ? context.Guard.ToRelative(outPath)
            : null;

        var truncated = outTotal > ToolOutputCapture.InlineBytes
                        || errTotal > ToolOutputCapture.InlineBytes;

        var notices = new List<string>(2);

        if (run.TimedOut)
            notices.Add($"超过 {timeoutSeconds} 秒被强制终止（含子进程）");

        if (truncated)
        {
            notices.Add(
                $"输出共 {outTotal + errTotal:N0} 字节，行内只保留头尾各 {ToolOutputCapture.InlineBytes / 1024}KB"
                + (fullPath is null
                    ? "（落盘不可用，中间部分已丢失）"
                    : $"；全文见 {fullPath}，可用 ws_search 查关键词或 ws_read 分段读"));
        }

        return ToolResult.Success(new
        {
            command,
            shell,
            cwd = context.Guard.ToRelative(cwd),
            exit_code = run.ExitCode,
            timed_out = run.TimedOut,
            elapsed_ms = run.ElapsedMs,
            encoding = encodingMode ?? "auto",
            stdout = outHead,
            stdout_tail = outTail,
            stdout_bytes = outTotal,
            stderr = errHead,
            stderr_tail = errTail,
            stderr_bytes = errTotal,
            full_output = fullPath,
            truncated,
        }, notice: notices.Count == 0 ? null : string.Join("；", notices));
    }

}
