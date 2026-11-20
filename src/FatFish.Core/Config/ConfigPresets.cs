using System.Text;

namespace FatFish.Core.Config;

/// <summary>
/// 一条「配置预设」的摘要 —— 给菜单、列表用，不含任何密钥。
/// </summary>
/// <param name="Name">预设名（= 文件名去掉 .env）。</param>
/// <param name="FilePath">磁盘上的位置。</param>
/// <param name="Model">预设里写的主模型名（没写则 null）。</param>
/// <param name="BaseUrl">预设里写的接口地址（没写则 null）。</param>
/// <param name="HasApiKey">预设里是否带了主模型 key。</param>
/// <param name="Modified">文件修改时间。</param>
public sealed record ConfigPreset(
    string Name,
    string FilePath,
    string? Model,
    string? BaseUrl,
    bool HasApiKey,
    DateTimeOffset Modified)
{
    /// <summary>菜单上那一行右侧的灰色说明，例如「grok-4.7 · api.x.ai · key 已配」。</summary>
    public string Describe()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Model))
            parts.Add(Model!);

        if (!string.IsNullOrWhiteSpace(BaseUrl))
            parts.Add(Host(BaseUrl!));

        parts.Add(HasApiKey ? "key 已配" : "缺 key");

        return string.Join(" · ", parts);
    }

    private static string Host(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch (UriFormatException)
        {
            return url;
        }
    }
}

/// <summary>
/// 预设库：把「一整套模型连接配置」存在磁盘上，切模型就是切一个名字。
///
/// <b>存在哪儿</b>：<c>&lt;工作台&gt;/config/presets/&lt;名字&gt;.env</c>。
/// 用 .env 格式而不是 JSON，理由有三条：
///   1. 解析/写入复用 <see cref="EnvDocument"/> —— 引号、转义、注释的规则与 .env 完全一致，
///      不会出现「预设里读出来的 key 带引号」这种只在切换时暴露的问题；
///   2. 用户可以直接拿记事本改、可以复制给别人，不需要先理解一套 schema；
///   3. 出问题时它本身就是一份可读的现场。
///
/// <b>为什么预设里要带 key</b>：切模型往往就是切厂商，而厂商之间 key 不能通用。
/// 如果预设只存模型名，那每次切换还得回 .env 改 key —— 「灵活变更模型」就成了空话。
///
/// <b>安全提醒</b>：这些文件里是明文 key（和 .env 一样）。放在工作台内是刻意的
/// （路径沙箱之内、模型可读），但不要把整个 config/ 目录分享出去。
/// </summary>
public sealed class ConfigPresetStore
{
    /// <summary>「当前激活哪个预设」写在 .env 里的键名。</summary>
    public const string ActiveKey = "FATFISH_PRESET";

    /// <summary>
    /// 点「保存当前配置为预设」时**抓取哪些键**。
    ///
    /// 刻意只抓「换模型时需要整套换掉」的那几项：主模型连接 + 审查员连接。
    /// 不抓 NET_BACKENDS / UI_* / 审批档位这类与「换模型」无关的东西 ——
    /// 否则你在 A 预设里调了联网后端，切到 B 又变回去，而这两件事本来无关。
    /// （想要更多键，直接在预设文件里手写即可：<see cref="Load"/> 不做白名单过滤。）
    /// </summary>
    public static readonly IReadOnlyList<string> CapturedKeys =
    [
        "FATFISH_API_KEY",
        "FATFISH_BASE_URL",
        "FATFISH_MODEL",
        "FATFISH_TEMPERATURE",
        "FATFISH_MAX_TOKENS",
        "FATFISH_TIMEOUT",
        "VERIFIER_API_KEY",
        "VERIFIER_BASE_URL",
        "VERIFIER_MODEL",
        "VERIFIER_TEMPERATURE",
        "VERIFIER_MAX_TOKENS",
    ];

    public ConfigPresetStore(string root)
    {
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? "." : root);
        Directory = Path.Combine(Root, "config", "presets");
    }

    /// <summary>工作台根目录（预设目录相对它）。</summary>
    public string Root { get; }

    /// <summary>预设目录。可能还不存在 —— 只有 <see cref="Save"/> 会去建它。</summary>
    public string Directory { get; }

    public string PathFor(string name) => Path.Combine(Directory, Sanitize(name) + ".env");

    public bool Exists(string name)
    {
        try
        {
            return File.Exists(PathFor(name));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>列出全部预设（按名字排序）。目录不存在时返回空表，不抛异常。</summary>
    public IReadOnlyList<ConfigPreset> List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return Array.Empty<ConfigPreset>();

        var list = new List<ConfigPreset>();

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.env"))
        {
            try
            {
                var doc = EnvDocument.Load(file);
                var map = doc.Snapshot();

                list.Add(new ConfigPreset(
                    Name: Path.GetFileNameWithoutExtension(file),
                    FilePath: file,
                    Model: Trim(map, "FATFISH_MODEL"),
                    BaseUrl: Trim(map, "FATFISH_BASE_URL"),
                    HasApiKey: !string.IsNullOrWhiteSpace(Trim(map, "FATFISH_API_KEY")),
                    Modified: File.GetLastWriteTime(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件读不了不该让整个菜单空掉
                continue;
            }
        }

        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 读一个预设的全部键值。预设不存在时返回 null（调用方据此给出「名字写错了」而不是静默用空表）。
    /// </summary>
    /// <remarks>
    /// 不做键名白名单：预设文件里写什么就认什么（比如 NET_BACKENDS=tavily 一起切换）。
    /// 唯一的例外是 <see cref="ActiveKey"/> —— 那是「当前激活谁」，不该被预设自己覆盖。
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Load(string name)
    {
        string path;

        try
        {
            path = PathFor(name);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!File.Exists(path))
            return null;

        var map = EnvDocument.Load(path).Snapshot();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in map)
        {
            if (string.Equals(key, ActiveKey, StringComparison.OrdinalIgnoreCase))
                continue;

            result[key] = value;
        }

        return result;
    }

    /// <summary>写一个预设（已存在则整体覆盖，旧文件会先进 _backup）。返回写好的文件路径。</summary>
    /// <exception cref="ArgumentException">名字或键名不合法。</exception>
    public string Save(string name, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var clean = Sanitize(name);
        var path = Path.Combine(Directory, clean + ".env");

        var doc = EnvDocument.Empty();

        doc.AppendComment("# FatFish 配置预设：" + clean);
        doc.AppendComment("# 从界面「配置 ▸ 预设」保存的，也可以直接改这个文件。");
        doc.AppendComment("# 激活这个预设时，下面这些键会**覆盖** .env 里的同名键；没列在这里的键仍用 .env 的值。");
        doc.AppendComment("# 键名不限于模型相关：想连 NET_BACKENDS 一起切，直接写一行就行。");
        doc.AppendBlank();

        foreach (var (key, raw) in values)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            try
            {
                doc.Set(key.Trim(), (raw ?? string.Empty).Trim());
            }
            catch (ArgumentException)
            {
                // 键名非法或值里有换行：跳过这一条，而不是让整次保存失败
                continue;
            }
        }

        doc.Save(path, Path.Combine(Root, "_backup"));

        return path;
    }

    /// <summary>删除一个预设。文件先进 _backup 再删。返回是否真的删掉了。</summary>
    public bool Delete(string name)
    {
        string path;

        try
        {
            path = PathFor(name);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!File.Exists(path))
            return false;

        try
        {
            var backupDir = Path.Combine(Root, "_backup");
            System.IO.Directory.CreateDirectory(backupDir);

            var stamp = DateTime.Now.ToString(
                "yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);

            File.Copy(path, Path.Combine(backupDir, Path.GetFileName(path) + "." + stamp + ".bak"),
                overwrite: false);

            File.Delete(path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从一份配置快照里挑出 <see cref="CapturedKeys"/>，做成可保存的字典。
    /// 空值不写入（否则预设文件里会有一堆空键，看上去像「配了但没生效」）。
    /// </summary>
    public static Dictionary<string, string?> Capture(IReadOnlyDictionary<string, string> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in CapturedKeys)
        {
            if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                result[key] = value;
        }

        return result;
    }

    /// <summary>从当前进程环境变量抓一份（等价于「把现在正在用的这套存下来」）。</summary>
    public static Dictionary<string, string?> CaptureFromEnvironment()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in CapturedKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);

            if (!string.IsNullOrWhiteSpace(value))
                map[key] = value.Trim();
        }

        return Capture(map);
    }

    /// <summary>
    /// 预设名 → 安全的文件名。
    /// </summary>
    /// <remarks>
    /// 允许中文（用户很可能就叫「国产那个」「试用」），但必须挡住路径分隔符 ——
    /// 否则一个叫 <c>..\..\x</c> 的预设名能把文件写到工作台外面去。
    /// </remarks>
    public static string Sanitize(string name)
    {
        var raw = (name ?? string.Empty).Trim();

        if (raw.Length == 0)
            throw new ArgumentException("预设名不能为空", nameof(name));

        var sb = new StringBuilder(raw.Length);
        var invalid = Path.GetInvalidFileNameChars();

        foreach (var c in raw)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        // 首尾的点和空格在 Windows 上会被悄悄吃掉，写进文件名再读回来就对不上了
        var clean = sb.ToString().Trim().Trim('.', ' ');

        if (clean.Length == 0)
            throw new ArgumentException("预设名不能只由非法字符组成", nameof(name));

        return clean.Length <= 40 ? clean : clean[..40];
    }

    private static string? Trim(IReadOnlyDictionary<string, string> map, string key)
        => map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}
