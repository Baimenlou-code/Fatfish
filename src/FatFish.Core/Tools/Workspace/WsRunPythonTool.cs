using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_run_python —— 在工作台内运行一段 Python 代码。
///
/// 对应 Python 版 workspace.py::ws_run_python（它转手给 exec_tools.run_python）。
///
/// 做法与 Python 版一致：把代码写进 <c>&lt;cwd&gt;/.fatfish_tmp/&lt;名字&gt;.py</c>，
/// 用解释器执行，跑完删掉。写文件再执行而不是 <c>python -c</c>，理由只有一个但很实在：
/// <b>traceback 里能看到真实的文件名与行号</b>。用 <c>-c</c> 的话，
/// 出错时提示的是 <c>&lt;string&gt;</c> 第 3 行，模型既没法回头读那个文件，
/// 也没法确认自己到底写了几行。
///
/// 与 Python 版不同的四处，都是 C# 侧不得不做的适配，不是口味：
///
/// <b>1) 解释器要自己找。</b>Python 版用 <c>sys.executable</c>（它自己就是 Python 进程）。
///    C# 进程是 .NET，只能探测 —— 见 <see cref="PythonLocator"/>。
///
/// <b>2) 强制子进程用 UTF-8 写 stdio。</b>设 <c>PYTHONIOENCODING=utf-8</c> 之后，
///    <c>print("中文")</c> 出来的字节确定是 UTF-8，
///    不再依赖「先试 UTF-8、失败退 GBK」的猜测（那条路在内容恰好两边都合法时会猜错）。
///    但**只设 IO 编码，不设 PYTHONUTF8**：后者会改掉 <c>open()</c> 的默认编码，
///    让原本能读 GBK 文件的代码突然抛 UnicodeDecodeError。
///
/// <b>3) sys.path[0] 是脚本目录，不是 cwd。</b>这与 Python 版完全相同（那里也一样），
///    但正因为相同、又很容易被误解为「能 import 同目录的模块」，所以写进 Description 让模型知道。
///
/// <b>4) 非零退出码仍算「工具调用成功」。</b>Python 版返回 ok=False。
///    这里跟随 <c>ws_run_cmd</c> 的风格：进程确实跑完了，只是返回非零。
///    若返回 Failure，AgentLoop 只能把错误文本回给模型，stdout 与 traceback 全丢 ——
///    而排查 Python 报错恰恰全靠这两样。
///
/// ★ 2026-11-05 改：输出改为「全量落盘 + 行内头尾」。
///   traceback 恰恰长在输出的**尾部**，而原来 24KB 的截断只保留开头 ——
///   于是 Python 报错时最需要的那一段被丢掉。现在头尾各留 24KB，
///   完整输出落 logs/tool_output/*.log，结果里给 full_output 路径。
///   ★ 顺带修掉一处旧行为：非零退出时原来只提示「temp script 已删除，traceback 路径不可再读」，
///     现在 traceback 本身就在 stdout_tail 里，提示改成指向完整输出文件。
/// </summary>
public sealed class WsRunPythonTool : ITool
{
    private const int DefaultTimeoutSeconds = 60;
    private const int HardMaxTimeoutSeconds = 600;

    /// <summary>临时脚本目录（工作台内，已被 ws_list / ws_search 默认忽略）。</summary>
    private const string TempDirName = ".fatfish_tmp";

    public string Name => "ws_run_python";

    public string Description =>
        "在工作台内运行一段 Python 代码，返回退出码、stdout 与 stderr。需要人工审批。" +
        "代码会写成 <cwd>/.fatfish_tmp/*.py 再执行，因此 traceback 里有真实文件名与行号；跑完即删。" +
        "解释器自动查找：FATFISH_PYTHON 环境变量 → 工作台内 venv/.venv/env → PATH 里的 python。" +
        "注意脚本所在目录（不是 cwd）会成为 sys.path[0]：要 import 工作台里的模块，" +
        "请自己 sys.path.insert(0, r'工作台路径')。" +
        "适合做计算、数据处理、跑测试；只是执行一条现成的命令时用 ws_run_cmd 更直接。" +
        "输出很长时，stdout / stderr 只含头尾各 24KB（traceback 在 stdout_tail 里），完整内容在 full_output 指向的文件。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("code", Schema.String("要执行的完整 Python 源码（UTF-8）")),
            ("cwd", Schema.String("工作目录（相对工作台根目录）。脚本与执行都在这个目录下，缺省为工作台根目录")),
            ("timeout_seconds", Schema.Integer(
                $"超时秒数，默认 {DefaultTimeoutSeconds}，上限 {HardMaxTimeoutSeconds}",
                1, HardMaxTimeoutSeconds)),
            ("filename", Schema.String(
                "临时脚本文件名（可选，仅用于让 traceback 更好读，例如 explore_prices.py）。只取文件名部分，不能借此跳出临时目录")),
        ],
        "code");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public async ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("code", out var code, out var err))
            return err;

        var cwdRel = args.GetString("cwd") ?? string.Empty;
        var requestedName = args.GetString("filename");
        var timeoutSeconds = Math.Clamp(
            args.GetInt("timeout_seconds", DefaultTimeoutSeconds), 1, HardMaxTimeoutSeconds);

        // ---- 1. 工作目录 ----
        var cwd = context.Guard.Resolve(cwdRel);

        if (!Directory.Exists(cwd))
        {
            return ToolResult.Failure(File.Exists(cwd)
                ? $"{cwdRel} 是文件，不能作为工作目录"
                : $"工作目录不存在：{cwdRel}");
        }

        // ---- 2. 解释器 ----
        var resolution = PythonLocator.Resolve(context.Guard.Root);

        if (resolution.Interpreter is null)
            return ToolResult.Failure(resolution.Error ?? "未找到 Python 解释器");

        var interp = resolution.Interpreter;

        // ---- 3. 临时脚本 ----
        var scriptName = BuildScriptName(requestedName, out var nameError);
        if (scriptName is null)
            return ToolResult.Failure(nameError ?? "filename 不合法");

        var tmpDir = Path.Combine(cwd, TempDirName);
        var scriptPath = Path.Combine(tmpDir, scriptName);

        // 兜底：即使 BuildScriptName 已经剥掉了目录部分，这里再确认一次落点没越界。
        // 两道检查看着冗余，但「文件名清洗」这种逻辑一旦被后来的人改错，
        // 这第二道就是唯一能挡住写入工作台之外的东西。
        if (!context.Guard.IsInside(scriptPath))
            return ToolResult.Failure($"临时脚本路径越界：{scriptPath}");

        ct.ThrowIfCancellationRequested();

        try
        {
            Directory.CreateDirectory(tmpDir);

            // UTF-8 无 BOM：Python 3 的源码默认编码就是 UTF-8（PEP 3120），
            // 加 BOM 虽然也能跑，但会让 traceback 里第一行的列号偏移。
            await File.WriteAllTextAsync(scriptPath, code, new UTF8Encoding(false), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Failure($"写入临时脚本失败：{ex.Message}");
        }

        context.Log?.Invoke(
            $"[执行 Python] {context.Guard.ToRelative(scriptPath)}（{interp.Describe}，来源 {interp.Source}）");

        // ---- 4. 跑（全量落盘 + 内存里恒留头尾）----
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

        using var capture = new ToolOutputCapture(
            Path.Combine(context.Guard.Root, "logs", "tool_output"), $"py_{stamp}");

        ProcessRunResult run;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = interp.FileName,
                WorkingDirectory = cwd,
                Arguments = interp.PrefixArguments.Length == 0
                    ? Quote(scriptPath)
                    : interp.PrefixArguments + " " + Quote(scriptPath),
            };

            // 强制 stdio 用 UTF-8。只影响 stdout/stderr 的编码，
            // 不改 open() 的默认编码（那是 PYTHONUTF8 干的事，故意不设）。
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            // 不生成 .pyc：这次跑完脚本就删了，__pycache__ 只会变成工作台里的垃圾。
            psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

            run = await ProcessRunner.RunStreamingAsync(
                psi,
                TimeSpan.FromSeconds(timeoutSeconds),
                onStdout: capture.WriteStdout,
                onStderr: capture.WriteStderr,
                ct: ct).ConfigureAwait(false);
        }
        finally
        {
            // 无论成功、超时还是取消，都不留垃圾文件。
            // 删除失败（被杀进程仍持有句柄）也不能影响结果 —— 静默忽略。
            try
            {
                if (File.Exists(scriptPath))
                    File.Delete(scriptPath);
            }
            catch (IOException)
            {
                // 文件仍被占用：.fatfish_tmp 本来就会被忽略，留着无害
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (run.StartError is not null)
            return ToolResult.Failure(run.StartError);

        if (run.Cancelled)
        {
            return ToolResult.Failure(
                $"Python 代码已被用户取消（已运行 {run.ElapsedMs}ms）。") with
            {
                ElapsedMs = run.ElapsedMs,
            };
        }

        // ---- 5. 结果 ----
        var (outHead, outTail, outPath, outTotal) = capture.SnapshotStdout("auto");
        var (errHead, errTail, _, errTotal) = capture.SnapshotStderr("auto");

        var fullPath = outPath is not null && (outTotal > 0 || errTotal > 0)
            ? context.Guard.ToRelative(outPath)
            : null;

        var truncated = outTotal > ToolOutputCapture.InlineBytes
                        || errTotal > ToolOutputCapture.InlineBytes;

        var notices = new List<string>(3);

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

        if (!run.TimedOut && run.ExitCode is int rc && rc != 0)
            notices.Add($"退出码 {rc}：执行失败。traceback 见 stdout_tail / stderr");

        return ToolResult.Success(new
        {
            interpreter = interp.Describe,
            interpreter_source = interp.Source,
            cwd = context.Guard.ToRelative(cwd),
            script = context.Guard.ToRelative(scriptPath),
            exit_code = run.ExitCode,
            timed_out = run.TimedOut,
            elapsed_ms = run.ElapsedMs,
            encoding = "auto",
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

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    /// <summary>
    /// 决定临时脚本叫什么名字。
    /// </summary>
    /// <remarks>
    /// internal 而不是 private：<c>ws_bg_python</c> 要用同一套命名规则。
    /// 两个工具各生成一套名字的话，将来改一次规则（比如加任务前缀）
    /// 就会出现「前台脚本叫 snippet_*、后台脚本叫 bg_*」这种没有理由的不一致。
    /// </remarks>
    /// <returns>合法文件名；不合法时返回 null，<paramref name="error"/> 里是原因。</returns>
    internal static string? BuildScriptName(string? requested, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(requested))
        {
            // 带毫秒 + 随机数：同一毫秒内并发两次调用也不会撞名。
            // 只靠秒级时间戳，快速连续调用会互相覆盖脚本，traceback 指向错误的代码。
            return $"snippet_{DateTime.Now:HHmmss_fff}_{Random.Shared.Next(100, 999)}.py";
        }

        var name = requested.Trim();

        // ★ 只取最后一段路径。允许模型写 "sub/dir/try.py"（它可能这么想），
        //   但绝不允许 "../.."、盘符、UNC 之类真的把文件写到临时目录外面。
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
            name = name[(slash + 1)..];

        if (name.Length == 0)
        {
            error = "filename 只包含路径分隔符，没有文件名";
            return null;
        }

        if (!name.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            name += ".py";

        var invalid = Safety.PathGuard.ValidateFileName(name);
        if (invalid is not null)
        {
            error = $"filename 不合法：{invalid}";
            return null;
        }

        return name;
    }

    /// <summary>给命令行参数加引号（路径里常有空格，例如 AppData\Local\Temp）。</summary>
    private static string Quote(string value) => "\"" + value + "\"";
}
