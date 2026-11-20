using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_bg_python —— 把一段 Python 代码丢到后台跑，立即返回。
///
/// 与 <see cref="WsRunPythonTool"/> 的关系，跟 <see cref="WsBgCmdTool"/> 与
/// <see cref="WsRunCmdTool"/> 的关系完全一样：只差「等不等」。
///
/// 刻意复用了 <see cref="WsRunPythonTool.BuildScriptName"/>：
/// 临时脚本的命名规则（毫秒 + 随机数、只取最后一段文件名、Windows 保留名检查）
/// 在这里一模一样地需要，各写一份迟早会分叉 —— 而且分叉的后果是
/// 「后台脚本偶尔撞名互相覆盖」，表现得像代码写错了。
///
/// 脚本的生命周期不同：前台跑完就删；这里要**等任务结束**才删（成功、失败、超时、被 Kill 都删）。
/// 原因不是洁癖：后台脚本可能跑几小时，中途删了进程还握着句柄；而任务结束时不删，
/// 工作台里就会攒下一堆看不出用途的 snippet_*.py。
/// </summary>
public sealed class WsBgPythonTool : ITool
{
    private const int HardMaxTimeoutSeconds = 24 * 60 * 60;

    private const string TempDirName = ".fatfish_tmp";

    public string Name => "ws_bg_python";

    public string Description =>
        "在后台运行一段 Python 代码，**立即返回 job_id**，不阻塞当前回合。需要人工审批。" +
        "代码写成 <cwd>/.fatfish_tmp/*.py 再执行，traceback 里有真实文件名与行号；任务结束后删除。" +
        "解释器自动查找：FATFISH_PYTHON → 工作台内 venv/.venv/env → PATH 里的 python。" +
        "输出（含 stderr）会写进缓冲区（保留最近 4MB）：用 ws_bg_tail 增量读取，" +
        "ws_bg_list 看状态，ws_bg_kill 中止。任务结束时程序会自动播报结果。" +
        "适合长时间计算、批量处理；几十秒内能跑完的用 ws_run_python 更直接。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("code", Schema.String("要执行的完整 Python 源码（UTF-8）")),
            ("desc", Schema.String("任务简述（展示用，例如「全市场因子重算」）。缺省取代码首行")),
            ("cwd", Schema.String("工作目录（相对工作台根目录）")),
            ("timeout_seconds", Schema.Integer(
                "超时秒数。0（默认）表示不限时", 0, HardMaxTimeoutSeconds)),
            ("filename", Schema.String(
                "临时脚本文件名（可选，仅用于让 traceback 更好读）。只取文件名部分")),
        ],
        "code");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public async ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("code", out var code, out var err))
            return err;

        if (context.Jobs is null)
            return ToolResult.Failure("后台任务管理器未初始化，无法启动后台任务");

        var cwdRel = args.GetString("cwd") ?? string.Empty;
        var timeoutSeconds = Math.Clamp(
            args.GetInt("timeout_seconds", 0), 0, HardMaxTimeoutSeconds);

        var cwd = context.Guard.Resolve(cwdRel);

        if (!Directory.Exists(cwd))
        {
            return ToolResult.Failure(File.Exists(cwd)
                ? $"{cwdRel} 是文件，不能作为工作目录"
                : $"工作目录不存在：{cwdRel}");
        }

        var resolution = PythonLocator.Resolve(context.Guard.Root);

        if (resolution.Interpreter is null)
            return ToolResult.Failure(resolution.Error ?? "未找到 Python 解释器");

        var interp = resolution.Interpreter;

        var scriptName = WsRunPythonTool.BuildScriptName(args.GetString("filename"), out var nameError);
        if (scriptName is null)
            return ToolResult.Failure(nameError ?? "filename 不合法");

        var tmpDir = Path.Combine(cwd, TempDirName);
        var scriptPath = Path.Combine(tmpDir, scriptName);

        if (!context.Guard.IsInside(scriptPath))
            return ToolResult.Failure($"临时脚本路径越界：{scriptPath}");

        ct.ThrowIfCancellationRequested();

        try
        {
            Directory.CreateDirectory(tmpDir);
            await File.WriteAllTextAsync(scriptPath, code, new UTF8Encoding(false), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Failure($"写入临时脚本失败：{ex.Message}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = interp.FileName,
            WorkingDirectory = cwd,
            Arguments = interp.PrefixArguments.Length == 0
                ? "\"" + scriptPath + "\""
                : interp.PrefixArguments + " \"" + scriptPath + "\"",
        };

        // 与前台工具一致：只改 stdio 编码，不动 open() 的默认编码（那是 PYTHONUTF8）。
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

        var desc = BuildDesc(args.GetString("desc"), code);

        var jobId = context.Jobs.Start(new BackgroundJobRequest(
            Kind: "python",
            Desc: desc,
            TimeoutSeconds: timeoutSeconds,
            Work: async (handle, jobCt) =>
            {
                try
                {
                    var run = await ProcessRunner.RunStreamingAsync(
                        psi,
                        timeout: null,
                        onStdout: block => handle.Append(block.Span),
                        onStderr: block => handle.Append(block.Span),
                        ct: jobCt).ConfigureAwait(false);

                    if (run.StartError is not null)
                        handle.Note("进程启动失败：" + run.StartError);
                    else if (run.ExitCode is int rc && rc != 0)
                        handle.Note($"\n[退出码 {rc}：执行失败]");

                    return run.ExitCode ?? -1;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(scriptPath))
                            File.Delete(scriptPath);
                    }
                    catch (IOException)
                    {
                        // 进程刚被杀时句柄可能还在；.fatfish_tmp 本就被忽略，留着无害
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }));

        context.Log?.Invoke($"[后台] {jobId} 启动 Python：{desc}（{interp.Describe}）");

        return ToolResult.Success(new
        {
            job_id = jobId,
            kind = "python",
            desc,
            status = "running",
            interpreter = interp.Describe,
            interpreter_source = interp.Source,
            cwd = context.Guard.ToRelative(cwd),
            script = context.Guard.ToRelative(scriptPath),
            timeout_seconds = timeoutSeconds,
        }, notice: "已启动，不阻塞本回合。用 ws_bg_tail 读增量输出、ws_bg_list 看状态、ws_bg_kill 中止。");
    }

    private static string BuildDesc(string? given, string code)
    {
        if (!string.IsNullOrWhiteSpace(given))
            return given.Trim();

        var firstLine = code
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'))
            ?? "python";

        return firstLine.Length <= 40 ? firstLine : firstLine[..40] + "…";
    }
}
