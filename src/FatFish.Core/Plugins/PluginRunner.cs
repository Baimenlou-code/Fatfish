using System.Net;
using System.Text;
using System.Text.Json;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Workspace;

namespace FatFish.Core.Plugins;

/// <summary>
/// 插件工具的执行器 —— 把「清单里的一句话」变成一次真正的动作。
///
/// 三种执行面各自有一套必须守住的纪律：
///
/// <b>http</b>
///   · 只允许访问清单里声明的 host；不开自动跳转（3xx 直接返回给模型，
///     否则一次 302 就能把白名单绕过去）；
///   · 响应只读前 24KB —— 插件可能指向一个几 GB 的日志流。
///
/// <b>cmd / python</b>
///   · 参数**只通过环境变量**传入，绝不拼进命令行；
///   · 工作目录必须落在工作台内，走与内置 ws_run_cmd 完全相同的
///     <see cref="PathGuard"/> 与 <see cref="CmdShell"/>。
///
/// 共享的执行结果形状（<c>ok / data / notice</c>）与内置工具一致，
/// 这样模型不需要为「插件工具」学一套新的读法。
/// </summary>
public static class PluginRunner
{
    /// <summary>HTTP 响应体最多保留多少字节。</summary>
    public const int MaxBodyBytes = 24 * 1024;

    /// <summary>进程输出最多保留多少字节（与 ws_run_cmd 一致）。</summary>
    private const int MaxProcessBytes = ProcessRunner.DefaultMaxOutputBytes;

    /// <summary>
    /// 进程级共享的 HttpClient。
    /// </summary>
    /// <remarks>
    /// 不放走 <c>new HttpClient()</c> 的经典理由（socket 耗尽），加上两条插件特有的：
    ///   · <c>AllowAutoRedirect = false</c>：跳转目标可能不在白名单里，
    ///     自动跟随等于让白名单只对第一跳有效；
    ///   · <c>Timeout = InfiniteTimeSpan</c>：超时改由每个请求自己的 CTS 控制，
    ///     否则会和取消信号互相覆盖，报错时说不清是「谁超时了」。
    /// </remarks>
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    // ------------------------------------------------------------------
    // 入口
    // ------------------------------------------------------------------

    /// <param name="settings">
    /// 该插件的 <c>settings</c> 声明（清单里那块）；null 表示这个插件没声明，
    /// 于是"用户在插件设置里选的模型"也就无处可写。
    /// </param>
    public static async ValueTask<ToolResult> RunAsync(
        PluginToolSpec spec,
        string pluginId,
        string pluginDir,
        ToolContext context,
        JsonElement args,
        CancellationToken ct,
        PluginSettingsSpec? settings = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);

        if (args.ValueKind != JsonValueKind.Object)
            return ToolResult.Failure($"参数必须是 JSON 对象，实际是 {args.ValueKind}");

        // 必填校验放在最前面：三种执行面都需要，而且错误信息应该
        // 明确指出「缺哪个」——模型据此重试一次就能成功。
        foreach (var required in spec.RequiredParameters)
        {
            var v = ScalarToString(args, required);
            if (string.IsNullOrWhiteSpace(v))
                return ToolResult.Failure($"缺少必填参数 '{required}'");
        }

        return spec.Run.Kind switch
        {
            PluginRunKind.Http => await RunHttpAsync(spec, context, args, ct).ConfigureAwait(false),
            PluginRunKind.Cmd => await RunCmdAsync(spec, pluginId, pluginDir, context, args, ct, settings).ConfigureAwait(false),
            _ => await RunPythonAsync(spec, pluginId, pluginDir, context, args, ct, settings).ConfigureAwait(false),
        };
    }

    // ------------------------------------------------------------------
    // http
    // ------------------------------------------------------------------

    private static async ValueTask<ToolResult> RunHttpAsync(
        PluginToolSpec spec, ToolContext context, JsonElement args, CancellationToken ct)
    {
        var http = spec.Run.Http!;

        var url = http.Url;

        foreach (var placeholder in http.Placeholders)
        {
            var value = ScalarToString(args, placeholder) ?? string.Empty;
            url = url.Replace("{" + placeholder + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ToolResult.Failure($"拼装后的 URL 不合法：{url}");

        // 解析时已经校验过，这里再校一次：参数是模型填的，
        // 而 URI 的规范化（例如把 \\ 归并成 /）有可能改变 host 的形态。
        if (!http.Hosts.Any(h => PluginManifestParser.HostMatches(uri.Host, h)))
            return ToolResult.Failure($"目标域名 '{uri.Host}' 不在插件声明的 hosts 白名单里");

        using var request = new HttpRequestMessage(new HttpMethod(http.Method), uri);

        var hasBody = http.Method is "POST" or "PUT" or "PATCH" or "DELETE";

        if (hasBody)
        {
            request.Content = new StringContent(
                args.GetRawText(), new UTF8Encoding(false), "application/json");
        }

        foreach (var (name, value) in http.Headers)
        {
            // Content 类 header 必须加在 Content 上，加在 request 上会抛
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(http.TimeoutSeconds));

        context.Log?.Invoke($"[插件] {spec.Name} → {http.Method} {uri}");

        HttpResponseMessage response;

        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Failure($"请求超时（{http.TimeoutSeconds} 秒）：{uri}");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Failure($"请求失败：{ex.Message}");
        }

        using (response)
        {
            var (body, truncated) = await ReadLimitedAsync(response, MaxBodyBytes, cts.Token)
                .ConfigureAwait(false);

            var notices = new List<string>(2);

            if (truncated)
                notices.Add($"响应超过 {MaxBodyBytes / 1024}KB，已截断");

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                // 自动跳转是关掉的，所以这里必须把「要去哪」告诉模型，
                // 否则它只会看到一个空的 302 响应，完全不知道发生了什么。
                notices.Add($"服务端返回跳转，但插件不允许自动跟随；目标：{response.Headers.Location}");
            }

            return ToolResult.Success(new
            {
                status = (int)response.StatusCode,
                reason = response.ReasonPhrase,
                url = uri.ToString(),
                content_type = response.Content.Headers.ContentType?.ToString(),
                location = response.Headers.Location?.ToString(),
                body,
                truncated,
            }, notice: notices.Count == 0 ? null : string.Join("；", notices));
        }
    }

    private static async Task<(string Body, bool Truncated)> ReadLimitedAsync(
        HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (limit <= 0)
            return (string.Empty, false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);

            if (read == 0)
                break;

            var room = limit - (int)ms.Length;

            if (read > room)
            {
                ms.Write(buffer, 0, room);
                truncated = true;
                break;
            }

            ms.Write(buffer, 0, read);
        }

        // 用与子进程输出相同的解码策略：先严格 UTF-8，失败退 GBK/OEM。
        // 中文网站两种编码都常见，写死 UTF-8 会在 GBK 页面上得到一屏乱码，
        // 而模型对乱码的反应通常是「再请求一次」，白花一次调用。
        return (ProcessEncoding.Decode(ms.ToArray(), null), truncated);
    }

    // ------------------------------------------------------------------
    // cmd
    // ------------------------------------------------------------------

    private static async ValueTask<ToolResult> RunCmdAsync(
        PluginToolSpec spec, string pluginId, string pluginDir,
        ToolContext context, JsonElement args, CancellationToken ct,
        PluginSettingsSpec? settings)
    {
        var cmd = spec.Run.Cmd!;

        string cwd;

        try
        {
            cwd = context.Guard.Resolve(cmd.WorkingDirectory);
        }
        catch (Safety.PathEscapeException ex)
        {
            return ToolResult.Failure(ex.Message);
        }

        if (!Directory.Exists(cwd))
            return ToolResult.Failure($"工作目录不存在：{cmd.WorkingDirectory}");

        var launch = CmdShell.Build(cmd.Command, cmd.Shell, cwd, context.Guard);

        if (launch.Psi is null)
            return ToolResult.Failure(launch.Error ?? "命令组装失败");

        context.Log?.Invoke($"[插件] {spec.Name} ← {pluginId}（{cmd.Shell}）");

        try
        {
            var inject = InjectEnvironment(launch.Psi, spec, pluginId, pluginDir, args, settings, context.Guard.Root);

            if (inject is not null)
                return inject;

            var run = await ProcessRunner.RunAsync(
                launch.Psi,
                TimeSpan.FromSeconds(cmd.TimeoutSeconds),
                "auto",
                MaxProcessBytes,
                log: context.Log,
                ct: ct).ConfigureAwait(false);

            return FromProcess(run, cmd.Command, context.Guard.ToRelative(cwd), cmd.TimeoutSeconds);
        }
        finally
        {
            CmdShell.Cleanup(launch.TempFile);
        }
    }

    // ------------------------------------------------------------------
    // python
    // ------------------------------------------------------------------

    private static async ValueTask<ToolResult> RunPythonAsync(
        PluginToolSpec spec, string pluginId, string pluginDir,
        ToolContext context, JsonElement args, CancellationToken ct,
        PluginSettingsSpec? settings)
    {
        var py = spec.Run.Python!;

        string cwd;

        try
        {
            cwd = context.Guard.Resolve(py.WorkingDirectory);
        }
        catch (Safety.PathEscapeException ex)
        {
            return ToolResult.Failure(ex.Message);
        }

        if (!Directory.Exists(cwd))
            return ToolResult.Failure($"工作目录不存在：{py.WorkingDirectory}");

        var resolution = PythonLocator.Resolve(context.Guard.Root);

        if (resolution.Interpreter is null)
            return ToolResult.Failure(resolution.Error ?? "未找到 Python 解释器；python 类插件无法运行");

        var interp = resolution.Interpreter;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = interp.FileName,
            WorkingDirectory = cwd,
        };

        if (!string.IsNullOrWhiteSpace(interp.PrefixArguments))
        {
            foreach (var part in interp.PrefixArguments.Split(
                         ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                psi.ArgumentList.Add(part);
            }
        }

        // 用 ArgumentList 而不是拼 Arguments：脚本路径可能含空格或中文，
        // 让 .NET 负责加引号，就不会出现「路径带空格跑不起来」这类问题。
        psi.ArgumentList.Add(py.ScriptPath);

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

        var inject = InjectEnvironment(psi, spec, pluginId, pluginDir, args, settings, context.Guard.Root);

        if (inject is not null)
            return inject;

        context.Log?.Invoke($"[插件] {spec.Name} ← {pluginId}（python {interp.Describe}）");

        var run = await ProcessRunner.RunAsync(
            psi,
            TimeSpan.FromSeconds(py.TimeoutSeconds),
            "auto",
            MaxProcessBytes,
            log: context.Log,
            ct: ct).ConfigureAwait(false);

        return FromProcess(run, $"python {Path.GetFileName(py.ScriptPath)}",
            context.Guard.ToRelative(cwd), py.TimeoutSeconds);
    }

    // ------------------------------------------------------------------
    // 公共
    // ------------------------------------------------------------------

    /// <summary>
    /// 把参数（以及「用户在插件设置里做的选择」）注入环境变量。
    /// 返回非 null 表示注入失败（应直接作为结果返回）。
    /// </summary>
    private static ToolResult? InjectEnvironment(
        System.Diagnostics.ProcessStartInfo psi,
        PluginToolSpec spec,
        string pluginId,
        string pluginDir,
        JsonElement args,
        PluginSettingsSpec? settings,
        string settingsRoot)
    {
        foreach (var (paramName, envName) in spec.EnvironmentNames)
        {
            var value = ScalarToString(args, paramName);

            if (value is null)
                continue;

            // 换行是把「一个值」变成「两条命令」的唯一办法，
            // 而插件作者写模板时不会想到参数里会有换行。
            // 这里直接拒绝，比事后解释「为什么你的命令多执行了一行」便宜得多。
            if (value.Contains('\n') || value.Contains('\r'))
                return ToolResult.Failure($"参数 '{paramName}' 含换行符，不能传给命令");

            psi.Environment[envName] = value;
        }

        // 两个便利变量：脚本不必自己拼路径，也能一次拿到全部参数的 JSON。
        psi.Environment["FATFISH_PLUGIN_ID"] = pluginId;
        psi.Environment["FATFISH_PLUGIN_DIR"] = pluginDir;
        psi.Environment["FATFISH_ARGS_JSON"] = args.GetRawText();

        // ---- 插件设置里选的模型 ----
        // 为什么是"覆盖 .env 的那个键"而不是再起一个新变量名：
        // 插件脚本读的一直是那个键（例如 LIQIU_IMAGE_MODEL），
        // 换一个新变量名等于要求每个插件作者改代码 —— 那是宿主把自己的
        // 内部设计强加给插件。覆盖环境变量，脚本一行都不用动。
        if (settings is not null && !string.IsNullOrWhiteSpace(settingsRoot))
        {
            var (stored, _) = PluginSettingsStore.Load(settingsRoot, pluginId);

            // 一个插件可以有多个「模型槽位」（生图一个、改图一个）：这次该覆盖哪个键，
            // 由**正在执行的这个工具**决定，映射逻辑全在 PluginModelSlots 里。
            // 这里只负责把算好的覆盖项写进子进程环境。
            foreach (var (name, value) in
                     PluginModelSlots.BuildEnvOverrides(settings, stored, spec.Name))
            {
                psi.Environment[name] = value;
            }
        }

        return null;
    }

    private static ToolResult FromProcess(
        ProcessRunResult run, string command, string cwd, int timeoutSeconds)
    {
        if (run.StartError is not null)
            return ToolResult.Failure(run.StartError);

        if (run.Cancelled)
        {
            return ToolResult.Failure($"已被用户取消（已运行 {run.ElapsedMs}ms）")
                with { ElapsedMs = run.ElapsedMs };
        }

        var notices = new List<string>(2);

        if (run.TimedOut)
            notices.Add($"超过 {timeoutSeconds} 秒被强制终止（含子进程）");

        if (run.Truncated)
            notices.Add($"输出超过 {MaxProcessBytes / 1024}KB 已截断");

        // 退出码非 0 时**不返回 Failure** —— 这是刻意的。
        // 大量命令用退出码表达「没事发生」（grep 找不到返回 1），
        // 把它变成 ok:false 会让模型误以为工具坏了，而不是「结果为空」。
        // 真正的失败（进程起不来）已经在 StartError 分支处理。
        return ToolResult.Success(new
        {
            command,
            cwd,
            exit_code = run.ExitCode,
            timed_out = run.TimedOut,
            elapsed_ms = run.ElapsedMs,
            stdout = run.Stdout,
            stderr = run.Stderr,
            truncated = run.Truncated,
        }, notice: notices.Count == 0 ? null : string.Join("；", notices));
    }

    /// <summary>把 JSON 标量转成字符串；对象 / 数组返回其 JSON 原文。</summary>
    private static string? ScalarToString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => el.GetRawText(),
        };
    }
}
