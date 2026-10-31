using System.Globalization;
using System.Text;

namespace FatFish.Core.Config;

/// <summary>
/// .env 文件加载器 —— 直接复用 Python 版已有的那份配置。
///
/// 为什么需要：
///   C# 版没有 python-dotenv。而用户的 .env 里已经有 FATFISH_API_KEY 等
///   一整套配置，让 C# 版再维护一份配置是重复劳动，也容易两边不一致。
///
/// 与 python-dotenv 的行为对齐：
///   · KEY=VALUE，等号两侧空格会被去掉；
///   · 以 # 开头的行是注释；
///   · 支持 export KEY=VALUE（某些人习惯这么写）；
///   · 支持单引号/双引号包裹的值（会去掉引号）；
///   · 值里允许出现 =（只按第一个 = 切分）；
///   · **不覆盖已存在的环境变量**（系统环境变量优先级更高，这是通行约定）。
///
/// 刻意不支持：多行值、变量插值（${VAR}）。
///   Python 版也没用到，加了反而让「值里恰好有 $」的情况出问题。
/// </summary>
public static class EnvFile
{
    /// <summary>已加载过的文件（同一文件重复 Load 直接跳过）。</summary>
    private static readonly HashSet<string> LoadedFiles = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>
    /// 加载 .env 文件到进程环境变量。
    /// </summary>
    /// <returns>
    /// 从文件里**解析出的有效键数量**（无论是否因「已存在」而跳过）。
    /// 文件不存在 / 读不了时返回 0。
    /// </returns>
    /// <remarks>
    /// ★ 2026-10-29 修正返回值语义。
    ///   原实现只在「真正写进环境变量」时 count++，于是：
    ///     · 系统环境变量里已经有这些键（程序重启、被父进程带着跑）→ 全部跳过 → 返回 0；
    ///     · 界面/自检显示「载入 0 项」，用户以为 .env 没生效，而 key 其实好好的。
    ///   实测就撞上了：同一个 .env，直接跑显示「载入 0 项」，把环境变量清空再跑显示「载入 3 项」。
    ///   现在改成统计「解析出多少个键」—— 这才是调用方想知道的事实。
    ///   「已存在不覆盖」的行为保持不变（系统环境变量优先）。
    /// </remarks>
    public static int Load(string path, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return 0;

        var full = Path.GetFullPath(path);

        lock (Gate)
        {
            if (!force && !LoadedFiles.Add(full))
                return 0;
        }

        var parsed = 0;
        string[] lines;

        try
        {
            lines = File.ReadAllLines(full, Encoding.UTF8);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line[0] == '#')
                continue;

            // export KEY=VALUE
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            if (key.Length == 0)
                continue;

            // 键名合法性：字母/数字/下划线，且不以数字开头
            if (!EnvValueCodec.IsValidKey(key))
                continue;

            var value = EnvValueCodec.Decode(line[(eq + 1)..].Trim());

            parsed++;

            // 已存在则不覆盖（系统环境变量优先）
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                continue;

            try
            {
                Environment.SetEnvironmentVariable(key, value);
            }
            catch (ArgumentException)
            {
                // 值里有非法字符（极罕见），跳过这一条而不是整体失败
            }
        }

        return parsed;
    }

    /// <summary>
    /// 从若干候选起点向上找 .env，找到最近的一个就加载。
    ///
    /// 为什么要多个起点：
    ///   · `dotnet run` 时 cwd 是项目根；
    ///   · 直接双击 exe 时 cwd 是 bin\Debug\net10.0-windows；
    ///   · 发布后的单文件 exe 可能在任意目录运行。
    ///   三种情况的「根目录」位置完全不同，所以两个起点都要试。
    ///
    /// ★ 起点 0：<c>FATFISH_ENV</c> 环境变量（2026-10-29 新增）。
    ///   背景：开发时程序目录与仓库根不一定同处，两处可能各有一份 .env；
    ///   向上搜索会先撞见离得近的那份就停下，
    ///   永远到不了工作台根的那份 —— 两份 .env 里的 API Key 不同，
    ///   于是「用哪个 key」取决于从哪个目录启动，而且完全不报错。
    ///   指定 FATFISH_ENV 就能把「配置从哪来」这件事钉死，与工作台根解耦。
    /// </summary>
    public static (string? Path, int Count) LoadNearest(string startDirectory)
    {
        // ---- 起点 0：显式指定 ----
        // 找不到也照样返回这个路径（Count=0），让上层把「路径写错了」直接显示出来。
        // 静默回退到自动搜索会让配置问题变成「明明改了却不生效」。
        var explicitEnv = Environment.GetEnvironmentVariable("FATFISH_ENV");
        if (!string.IsNullOrWhiteSpace(explicitEnv))
        {
            var explicitPath = explicitEnv.Trim().Trim('"');
            return (explicitPath, Load(explicitPath));
        }

        var roots = new List<string>();

        // 起点 1：显式指定的工作台（优先级最高）
        var ws = Environment.GetEnvironmentVariable("FATFISH_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(ws))
            roots.Add(ws);

        // 起点 2：调用方给的目录
        if (!string.IsNullOrWhiteSpace(startDirectory))
            roots.Add(startDirectory);

        // 起点 3：程序自身所在目录
        roots.Add(AppContext.BaseDirectory);

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var start in roots)
        {
            DirectoryInfo? dir;
            try
            {
                dir = new DirectoryInfo(Path.GetFullPath(start));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            // 向上找 8 层：足够覆盖 bin\Debug\net10.0-windows 这种深路径
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                if (!tried.Add(dir.FullName))
                    continue;

                var candidate = Path.Combine(dir.FullName, ".env");
                if (File.Exists(candidate))
                    return (candidate, Load(candidate));
            }
        }

        return (null, 0);
    }

    // ---------- 取值助手 ----------

    public static string? Get(string key, string? fallback = null)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
    }

    public static int GetInt(string key, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    public static double GetDouble(string key, double fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    public static bool GetBool(string key, bool fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(v))
            return fallback;

        return v.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }

    // ---------- 内部 ----------

    // ★ 2026-10-29：IsValidKey / Unquote 搬到了 EnvValueCodec。
    //   原因：窗口化编辑器要「写」.env，而写入必须用与这里**完全一致**的引号规则。
    //   两份实现迟早会分叉，分叉后的症状是「编辑器里显示 sk-xxx，程序读到 "sk-xxx"」——
    //   两边都不报错，只表现为「key 明明填了却说没配置」。
    //   现在读写共用 EnvValueCodec，测试只需守住一份（EnvDocumentTests）。
}
