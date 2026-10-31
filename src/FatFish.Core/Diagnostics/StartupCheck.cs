using System.Globalization;
using System.Text;
using FatFish.Core.Config;
using FatFish.Core.Llm;
using FatFish.Core.Net;
using FatFish.Core.Tools.Workspace;

namespace FatFish.Core.Diagnostics;

/// <summary>自检条目的状态。</summary>
public enum CheckStatus
{
    /// <summary>正常。</summary>
    Ok,

    /// <summary>能用，但有值得注意的地方。</summary>
    Warn,

    /// <summary>会导致某个功能不可用。</summary>
    Fail,

    /// <summary>不适用（例如没配 Tavily，就不必报错）。</summary>
    Skip,
}

/// <summary>一条自检结果。</summary>
/// <param name="Name">检查项名称。</param>
/// <param name="Status">状态。</param>
/// <param name="Detail">实测到的事实。</param>
/// <param name="Hint">出问题时该怎么做（正常时为 null）。</param>
public sealed record CheckItem(string Name, CheckStatus Status, string Detail, string? Hint = null);

/// <summary>一次自检的完整结果。</summary>
public sealed record StartupReport(IReadOnlyList<CheckItem> Items)
{
    public bool HasFailures => Items.Any(i => i.Status == CheckStatus.Fail);

    public bool HasWarnings => Items.Any(i => i.Status == CheckStatus.Warn);

    public CheckItem? this[string name]
        => Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));

    public string ToText(bool includeSkip = false)
    {
        var sb = new StringBuilder();

        foreach (var item in Items)
        {
            if (!includeSkip && item.Status == CheckStatus.Skip)
                continue;

            var icon = item.Status switch
            {
                CheckStatus.Ok => "OK  ",
                CheckStatus.Warn => "WARN",
                CheckStatus.Fail => "FAIL",
                _ => "SKIP",
            };

            sb.Append(icon).Append(' ').Append(item.Name.PadRight(12)).Append(item.Detail).Append('\n');

            if (item.Hint is not null)
                sb.Append("     → ").Append(item.Hint).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 启动自检。
///
/// ★ 为什么要在启动时做这件事（而不是等出错再说）：
///   本程序有 5 类互相独立的依赖 —— 工作台目录、.env、LLM key、联网 key、Python。
///   其中任何一个缺失，表现出来都是**同一个症状**：「它好像不动了」。
///   用户（以及模型自己）会先去怀疑最难排查的那一层（网络、模型、代码），
///   而真正的原因往往是「工作台目录是只读的」。
///
/// ★ 为什么**不**在这里调用模型做连通性测试：
///   自检会花掉一次请求的钱，而绝大多数失败（缺 key、目录只读）在本地就能判断。
///   真需要连通性验证时用 <c>fatfish --probe</c>，那是个显式动作。
///
/// ★ 每一条 CheckItem 都必须给出「可操作的 Hint」，不能只说「失败」。
///   「.env 未找到」是事实，而「在工作台根放一个 .env，写上 FATFISH_API_KEY=sk-xxx」
///   才是用户需要的东西。
/// </summary>
public static class StartupCheck
{
    /// <summary>磁盘剩余空间低于此值时告警（本程序会写会话、台账、后台任务输出）。</summary>
    private const long LowDiskBytes = 200L * 1024 * 1024;

    /// <summary>
    /// 跑一遍自检。
    /// </summary>
    /// <param name="workspaceRoot">工作台根目录。</param>
    /// <param name="needsPython">是否检查 Python（只有用到 ws_run_python / ws_bg_python 时才需要）。</param>
    public static StartupReport Run(string workspaceRoot, bool needsPython = true)
    {
        var items = new List<CheckItem>
        {
            CheckWorkspace(workspaceRoot),
            CheckEnvFile(workspaceRoot),
            CheckApiKey(),
            CheckModel(),
            CheckPython(workspaceRoot, needsPython),
            CheckNet(),
            CheckWritableDirs(workspaceRoot),
            CheckDisk(workspaceRoot),
            CheckConsoleEncoding(),
        };

        return new StartupReport(items);
    }

    // ------------------------------------------------------------------

    private static CheckItem CheckWorkspace(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return new CheckItem("工作台", CheckStatus.Fail, "未指定", "设置 FATFISH_WORKSPACE 或从目标目录启动");

        if (!Directory.Exists(root))
            return new CheckItem("工作台", CheckStatus.Fail, $"不存在：{root}", "确认路径拼写；目录必须先存在");

        return new CheckItem("工作台", CheckStatus.Ok, root);
    }

    private static CheckItem CheckEnvFile(string root)
    {
        try
        {
            var (path, count) = EnvFile.LoadNearest(root);

            if (path is null)
            {
                return new CheckItem(
                    "配置文件", CheckStatus.Warn, "未找到 .env",
                    $"在 {root} 放一个 .env，内容形如 FATFISH_API_KEY=sk-xxxx" +
                    "（也可以在系统环境变量里设置同名变量）");
            }

            return new CheckItem(
                "配置文件", CheckStatus.Ok, $"{path}（载入 {count} 项）");
        }
        catch (Exception ex)
        {
            return new CheckItem("配置文件", CheckStatus.Warn, $".env 解析失败：{ex.Message}", "检查文件编码与 = 号附近是否有全角字符");
        }
    }

    private static CheckItem CheckApiKey()
    {
        if (!LlmOptions.TryResolveApiKey(out var key, out var source))
        {
            return new CheckItem(
                "API Key", CheckStatus.Fail, "未找到",
                "在 .env 或系统环境变量里设置 FATFISH_API_KEY（旧写法 DEEPSEEK_API_KEY 也认）");
        }

        // 只露尾 4 位。前缀（sk-）能说明是哪个平台，中间那段是秘密。
        var masked = key.Length <= 8 ? "****" : $"***{key[^4..]}";

        var extra = source == "DEEPSEEK_API_KEY"
            ? "（用的是旧写法 DEEPSEEK_API_KEY —— 如果它与 FATFISH_API_KEY 不是同一个账号，账单会分在两个账上）"
            : string.Empty;

        return new CheckItem("API Key", CheckStatus.Ok, $"{source} = {masked}{extra}");
    }

    private static CheckItem CheckModel()
    {
        try
        {
            var o = LlmOptions.FromEnvironment();

            var warn = o.MaxTokens >= 100_000
                ? "（max_tokens 偏大：长回答更完整，但单次失败也更贵）"
                : string.Empty;

            return new CheckItem(
                "模型", CheckStatus.Ok,
                $"model={o.Model} base={o.BaseUrl} max_tokens={o.MaxTokens} temp={o.Temperature:0.##}{warn}");
        }
        catch (Exception ex)
        {
            return new CheckItem("模型", CheckStatus.Fail, ex.Message, "多半是 API Key 缺失导致，先修上面那条");
        }
    }

    private static CheckItem CheckPython(string root, bool needsPython)
    {
        if (!needsPython)
            return new CheckItem("Python", CheckStatus.Skip, "宿主未启用 Python 工具");

        try
        {
            var res = PythonLocator.Resolve(root);

            if (res.Interpreter is null)
            {
                return new CheckItem(
                    "Python", CheckStatus.Warn, res.Error ?? "未找到解释器",
                    "ws_run_python / ws_bg_python 将不可用；其余功能不受影响。" +
                    "把 Python 加进 PATH，或在 .env 里设置 FATFISH_PYTHON=<解释器完整路径>");
            }

            return new CheckItem("Python", CheckStatus.Ok, $"{res.Interpreter.Describe}（{res.Interpreter.Source}）");
        }
        catch (Exception ex)
        {
            return new CheckItem("Python", CheckStatus.Warn, ex.Message);
        }
    }

    private static CheckItem CheckNet()
    {
        try
        {
            var o = NetOptions.FromEnvironment();

            if (!o.AnyProvider)
            {
                return new CheckItem(
                    "联网", CheckStatus.Warn, "没有任何可用后端",
                    "web_search / web_fetch 将不可用。设置 TAVILY_API_KEY，" +
                    "或确认 FATFISH_API_KEY 可用于官方 Search/Fetch");
            }

            var backends = new List<string>();
            if (o.HasDeepSeek) backends.Add("deepseek");
            if (o.HasTavily) backends.Add("tavily");

            var spec = o.ResolveBackends();

            var mode = spec is null
                ? "多后端 auto"
                : spec.Count == 0
                    ? "单后端"
                    : "多后端 " + string.Join("+", spec);

            return new CheckItem("联网", CheckStatus.Ok, $"可用 [{string.Join(" ", backends)}]，{mode}");
        }
        catch (Exception ex)
        {
            return new CheckItem("联网", CheckStatus.Warn, ex.Message);
        }
    }

    /// <summary>
    /// 检查要写的目录真的能写。
    /// </summary>
    /// <remarks>
    /// ★ 这条是本自检里最有价值的：程序启动完全正常、聊天也正常，
    ///   直到关窗口时才发现「会话一条都没存下来」。
    ///   实测写一次是唯一可靠的办法 —— 目录存在 ≠ 有权限 ≠ 磁盘没满。
    /// </remarks>
    private static CheckItem CheckWritableDirs(string root)
    {
        var targets = new (string Name, string Path)[]
        {
            ("会话", Path.Combine(root, "logs", "sessions")),
            ("台账", Path.Combine(root, "logs", "usage")),
            ("备份", Path.Combine(root, "_backup")),
        };

        var bad = new List<string>();
        var okCount = 0;

        foreach (var (name, dir) in targets)
        {
            try
            {
                Directory.CreateDirectory(dir);

                var probe = Path.Combine(dir, $".write_probe_{Environment.ProcessId}");
                File.WriteAllText(probe, "x", Encoding.UTF8);
                File.Delete(probe);

                okCount++;
            }
            catch (Exception ex)
            {
                bad.Add($"{name}({ex.GetType().Name})");
            }
        }

        if (bad.Count > 0)
        {
            return new CheckItem(
                "可写目录", CheckStatus.Fail, "无法写入：" + string.Join("、", bad),
                $"检查 {root} 的权限，或把工作台换到有写权限的目录");
        }

        return new CheckItem("可写目录", CheckStatus.Ok, $"{okCount} 个目录均可写");
    }

    private static CheckItem CheckDisk(string root)
    {
        try
        {
            var full = Path.GetFullPath(root);
            var driveRoot = Path.GetPathRoot(full);

            if (string.IsNullOrEmpty(driveRoot))
                return new CheckItem("磁盘", CheckStatus.Skip, "无法确定盘符");

            var di = new DriveInfo(driveRoot);

            if (!di.IsReady)
                return new CheckItem("磁盘", CheckStatus.Warn, $"{di.Name} 未就绪");

            var free = di.AvailableFreeSpace;

            var detail = $"{di.Name} 剩余 {Human(free)} / 共 {Human(di.TotalSize)}";

            if (free < LowDiskBytes)
            {
                return new CheckItem(
                    "磁盘", CheckStatus.Warn, detail,
                    "空间不足 200MB：会话与后台任务输出可能写失败。" +
                    "日志在 logs/ 下，确认没用的可以删");
            }

            return new CheckItem("磁盘", CheckStatus.Ok, detail);
        }
        catch (Exception ex)
        {
            return new CheckItem("磁盘", CheckStatus.Skip, ex.Message);
        }
    }

    private static CheckItem CheckConsoleEncoding()
    {
        try
        {
            var enc = Console.OutputEncoding.WebName;

            // Windows 控制台默认 GBK 时，中文会变成乱码；UTF-8 才正常。
            if (!enc.Contains("utf", StringComparison.OrdinalIgnoreCase))
            {
                return new CheckItem(
                    "终端编码", CheckStatus.Warn, $"OutputEncoding={enc}",
                    "中文可能显示为乱码；在 Windows 上执行 chcp 65001，或让程序自动设置 UTF-8");
            }

            return new CheckItem("终端编码", CheckStatus.Ok, enc);
        }
        catch (Exception)
        {
            // 没有控制台（WPF 进程）时会抛 —— 这不是问题
            return new CheckItem("终端编码", CheckStatus.Skip, "无控制台（GUI 模式）");
        }
    }

    private static string Human(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;

        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return v.ToString("0.#", CultureInfo.InvariantCulture) + units[i];
    }
}
