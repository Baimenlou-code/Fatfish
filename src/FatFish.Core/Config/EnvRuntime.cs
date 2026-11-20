namespace FatFish.Core.Config;

/// <summary>
/// 一次热重载的结果。给 UI / 命令行回话用，也可直接写进日志。
/// </summary>
/// <param name="EnvPath">这次实际读的 .env（null = 没解析到）。</param>
/// <param name="PresetName">叠加了哪个预设（null = 只用 .env）。</param>
/// <param name="Changed">值变了的键。</param>
/// <param name="Added">之前进程里没有、这次新增的键。</param>
/// <param name="Removed">上次由配置文件带进来、这次文件里没有了（已清空）的键。</param>
/// <param name="Warnings">不影响继续跑、但必须让人看见的问题。</param>
public sealed record EnvReloadResult(
    string? EnvPath,
    string? PresetName,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Warnings)
{
    /// <summary>有没有真的改动到什么。false 时 UI 应该说「没有变化」，而不是「重载成功」。</summary>
    public bool Touched => Changed.Count + Added.Count + Removed.Count > 0;

    /// <summary>
    /// 这次真正写进进程环境变量的键数（新增 + 变更）。
    /// 展示「装载了几项」要用它，别用 <see cref="Added"/>：从父进程继承过同名键时
    /// 所有键都会算「变更」而不是「新增」，只看 Added 会显示成「载入 0 项」。
    /// </summary>
    public int AppliedCount => Changed.Count + Added.Count;

    public string Describe()
    {
        var bits = new List<string>
        {
            EnvPath is null ? "配置：未找到 .env" : "配置：" + EnvPath,
        };

        if (!string.IsNullOrWhiteSpace(PresetName))
            bits.Add("预设：" + PresetName);

        bits.Add(!Touched
            ? "没有变化"
            : $"变更 {Changed.Count} / 新增 {Added.Count} / 清除 {Removed.Count}");

        return string.Join("｜", bits);
    }
}

/// <summary>
/// 运行时配置：把 .env（+ 激活的预设）重新灌进进程环境变量，让改动**不用重启**就生效。
///
/// <b>为什么必须有这一层</b>（2026-11-06 的教训）：
///   <see cref="EnvFile.Load"/> 刻意「已存在的环境变量不覆盖」（系统环境变量优先），
///   而且只读一次。于是程序跑起来以后改 .env 完全无效；更坑的是「保存并重启」
///   是 <c>Process.Start</c> 继承父进程环境变量 —— 新进程一看「这些键已存在」，又跳过 .env。
///   表现为「怎么改都没反应」，而且不报任何错。
///   所以热重载必须换一套语义：<b>文件里的值强制覆盖进程环境变量</b>。
///
/// <b>和 Load 的区别（很重要，别混用）</b>：
///   | | Load（启动时） | Reload（运行时） |
///   |---|---|---|
///   | 文件值 vs 进程环境变量 | 环境变量优先（不覆盖） | 文件优先（强制覆盖） |
///   | 同一文件重复调用 | 跳过（去重） | 每次都真读 |
///   | 文件里消失的键 | 不管 | 上次带进来过的 → 清空 |
///
/// 最后一条是为了让「删掉 .env 里的一行 = 回到程序默认值」成立。
/// 只清「本进程自己曾经从文件里应用过」的键，绝不碰真正的系统环境变量 ——
/// 否则一次重载就会把别人的 <c>PATH</c> 之类抹掉（那就不再是配置工具，是破坏工具了）。
/// </summary>
public static class EnvRuntime
{
    private static readonly object Gate = new();

    /// <summary>本进程曾经从 .env / 预设里应用过的键（用于「文件里删掉了就清空」）。</summary>
    private static readonly HashSet<string> Applied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析「实际生效的那份 .env」。不读文件、不改环境变量。</summary>
    public static string? ResolveEnvPath(string root) => EnvFile.ResolvePath(root);

    /// <summary>
    /// 组装「这次生效的完整键值表」= .env 全文，再被激活的预设覆盖。
    /// 不碰进程环境变量 —— 想知道「值是什么」而不想改变任何东西时用它。
    /// </summary>
    public static (IReadOnlyDictionary<string, string> Values, string? EnvPath, string? PresetName,
        IReadOnlyList<string> Warnings) Compose(string root)
    {
        var warnings = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var envPath = EnvFile.ResolvePath(root);

        if (envPath is null)
        {
            warnings.Add("没找到 .env（工作台根、启动目录、程序目录向上 8 层都没有）");
        }
        else if (!File.Exists(envPath))
        {
            warnings.Add("指定的配置文件不存在：" + envPath);
        }
        else
        {
            try
            {
                foreach (var (key, value) in EnvDocument.Load(envPath).Snapshot())
                    values[key] = value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add(".env 读取失败：" + ex.Message);
            }
        }

        string? presetName = null;

        if (values.TryGetValue(ConfigPresetStore.ActiveKey, out var rawActive)
            && !string.IsNullOrWhiteSpace(rawActive))
        {
            try
            {
                presetName = ConfigPresetStore.Sanitize(rawActive);

                var store = new ConfigPresetStore(root);
                var preset = store.Load(presetName);

                if (preset is null)
                {
                    warnings.Add($"激活的预设「{presetName}」不存在（{store.PathFor(presetName)}），本次只用 .env");
                }
                else
                {
                    foreach (var (key, value) in preset)
                        values[key] = value;
                }
            }
            catch (ArgumentException)
            {
                warnings.Add($"FATFISH_PRESET 的值「{rawActive}」不是合法的预设名，已忽略");
            }
        }

        return (values, envPath, presetName, warnings);
    }

    /// <summary>
    /// 启动时装载配置：<c>.env</c> + 激活的预设 → 写进进程环境变量（文件优先）。
    /// </summary>
    /// <remarks>
    /// ★ 2026-11-17 修。启动路径原来用的是 <see cref="EnvFile.LoadNearest"/>，它有两个致命错配：
    ///   <b>①它压根不看 <c>FATFISH_PRESET</c></b>，只读 .env 的裸值 —— 于是每次启动都退回
    ///   「切换预设之前」的那套配置，用户必须手动去「配置 ▸ 预设」点一下才切回来；
    ///   <b>②它「已存在不覆盖」</b>，程序自己 Relaunch 时从父进程继承来的旧值还会把文件里的新值挡在外面。
    ///   所以启动与热重载必须同一套语义：组装 .env + 预设，然后强制覆盖。
    ///   与 <see cref="Reload"/> 的差别只在调用时机 —— 启动时 <see cref="Applied"/> 还是空的，
    ///   自然走不到「文件里消失的键要清空」那一段。
    /// </remarks>
    public static EnvReloadResult ApplyStartup(string root) => Reload(root);

    /// <summary>
    /// 热重载：重新读 .env + 激活的预设，强制写进本进程的环境变量。
    /// </summary>
    /// <remarks>
    /// ★ 调用方拿到结果后**必须重建依赖这些值的对象**（模型客户端、联网服务、审查员）。
    ///   环境变量变了不等于已经在用的对象会跟着变 —— 它们都是在构造时把值抄进去的。
    ///   这一点是本功能最容易「看起来成功、其实没生效」的地方。
    /// </remarks>
    public static EnvReloadResult Reload(string root)
    {
        lock (Gate)
        {
            var (values, envPath, presetName, composeWarnings) = Compose(root);

            // 拷一份可变的：Compose 返回的是只读列表，而下面还要追加「值里有非法字符」这类临时警告
            var warnings = new List<string>(composeWarnings);

            var changed = new List<string>();
            var added = new List<string>();
            var removed = new List<string>();

            // 1. 文件里删掉的键：只有「上次是它自己带进来的」才清空
            foreach (var stale in Applied.Where(k => !values.ContainsKey(k)).ToList())
            {
                Environment.SetEnvironmentVariable(stale, null);

                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(stale)))
                    continue; // 清不掉（极少见）：别谎报成功

                Applied.Remove(stale);
                removed.Add(stale);
            }

            // 2. 应用文件里的值（强制覆盖 —— 这是与启动时 Load 的唯一但关键的差别）
            foreach (var (key, value) in values)
            {
                var before = Environment.GetEnvironmentVariable(key);
                var after = value ?? string.Empty;

                try
                {
                    Environment.SetEnvironmentVariable(key, after);
                }
                catch (ArgumentException)
                {
                    warnings.Add($"键「{key}」的值里有非法字符，已跳过");
                    continue;
                }

                Applied.Add(key);

                if (before is null)
                    added.Add(key);
                else if (!string.Equals(before, after, StringComparison.Ordinal))
                    changed.Add(key);
            }

            changed.Sort(StringComparer.OrdinalIgnoreCase);
            added.Sort(StringComparer.OrdinalIgnoreCase);
            removed.Sort(StringComparer.OrdinalIgnoreCase);

            return new EnvReloadResult(envPath, presetName, changed, added, removed, warnings);
        }
    }

    /// <summary>读「当前激活哪个预设」。只读 .env，不看进程环境变量（避免被上一次重载的残留误导）。</summary>
    public static string? ActivePresetName(string root)
    {
        var envPath = EnvFile.ResolvePath(root);

        if (envPath is null || !File.Exists(envPath))
            return null;

        try
        {
            var raw = EnvDocument.Load(envPath).Get(ConfigPresetStore.ActiveKey);

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return ConfigPresetStore.Sanitize(raw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把「激活某个预设」写进 .env（传 null 表示取消激活）。
    /// </summary>
    /// <returns>写好的 .env 路径；备份文件路径在 <paramref name="backup"/> 里。</returns>
    public static string SetActivePreset(string root, string? presetName, out string? backup)
    {
        var envPath = EnvFile.ResolvePath(root) ?? Path.Combine(Path.GetFullPath(root), ".env");

        var doc = File.Exists(envPath) ? EnvDocument.Load(envPath) : EnvDocument.Empty();

        if (string.IsNullOrWhiteSpace(presetName))
            doc.Remove(ConfigPresetStore.ActiveKey);
        else
            doc.Set(ConfigPresetStore.ActiveKey, ConfigPresetStore.Sanitize(presetName));

        backup = doc.Save(envPath, Path.Combine(Path.GetFullPath(root), "_backup"));

        return envPath;
    }
}
