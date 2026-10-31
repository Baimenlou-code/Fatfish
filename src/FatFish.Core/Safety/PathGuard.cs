namespace FatFish.Core.Safety;

/// <summary>
/// 路径逃逸异常 —— 请求的路径落在工作台根目录之外。
/// </summary>
public sealed class PathEscapeException : Exception
{
    public string Requested { get; }
    public string Root { get; }

    public PathEscapeException(string requested, string root)
        : base($"路径越界：'{requested}' 不在工作台根 '{root}' 之内")
    {
        Requested = requested;
        Root = root;
    }
}

/// <summary>
/// 敏感文件的判定结果。
/// </summary>
public readonly record struct SensitiveVerdict(bool IsSensitive, string? Reason)
{
    public static SensitiveVerdict Clean => new(false, null);
    public static SensitiveVerdict Sensitive(string reason) => new(true, reason);
}

/// <summary>
/// PathGuard —— 工作台路径守卫。
///
/// 对应 Python 版 workspace.py 的：_safe_path / _rel / is_inside_default /
/// is_sensitive_path / sensitive_reason / _is_root_level_file。
///
/// 比 Python 版额外加固的三点：
///   1. 符号链接 / junction 逃逸检测（Python 版的 os.path.abspath 拦不住这类）；
///   2. Windows 设备名（CON / PRN / NUL…）与尾随点空格；
///   3. 前缀比较时强制补分隔符，避免 "C:\root2" 被 "C:\root" 误判为在内部。
/// </summary>
public sealed class PathGuard
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    private readonly string _root;
    private readonly string _rootWithSep;

    /// <summary>Windows 保留设备名。即使带扩展名（CON.txt）也非法。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 敏感文件名模式（对应 Python 版 SENSITIVE_NAME_GLOBS）。
    /// 匹配时大小写不敏感，与 Windows 语义一致。
    /// </summary>
    private static readonly string[] SensitiveNamePatterns =
    [
        ".env", ".env.*", "*.env",
        "*.key", "*.pem", "*.pfx", "*.p12", "*.pfx",
        "id_rsa", "id_rsa.*", "id_ed25519", "id_ed25519.*",
        "*.keystore", "*.jks",
        "credentials", "credentials.*",
        "*.secret", "secrets.*",
    ];

    /// <summary>
    /// 敏感目录前缀（对应 Python 版 _SENSITIVE_DIR_PREFIXES）。
    /// 命中即整棵子树视为敏感。
    /// </summary>
    private static readonly string[] SensitiveDirPrefixes =
    [
        "_backup/.env",
        ".git/",
        "_ssh_setup/",
    ];

    /// <summary>超过此大小的文件不计算全量哈希（读凭证用元数据判定）。</summary>
    private const long FullHashLimit = 8L * 1024 * 1024;

    public PathGuard(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        // 根目录本身必须先存在，否则后续所有相对解析都无意义
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"工作台根目录不存在：{full}");

        _root = full.Length > 1 ? full.TrimEnd(Sep) : full;
        _rootWithSep = _root.EndsWith(Sep) ? _root : _root + Sep;
    }

    /// <summary>规范化后的工作台根目录（无尾部分隔符）。</summary>
    public string Root => _root;

    /// <summary>
    /// 把工作台内的相对路径解析成绝对路径。
    /// 越界时抛 <see cref="PathEscapeException"/>。
    /// </summary>
    /// <param name="relativePath">相对路径；空字符串表示根目录自身。</param>
    public string Resolve(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return _root;

        var raw = relativePath.Trim();

        // "--" 这类纯符号不是合法路径，拒绝而不是静默当成文件名
        if (raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new PathEscapeException(raw, _root);

        // 绝对路径 / UNC / 盘符一律拒绝：工具层只接受相对路径
        if (Path.IsPathRooted(raw))
            throw new PathEscapeException(raw, _root);

        var full = Path.GetFullPath(raw, _root);

        if (!IsInside(full))
            throw new PathEscapeException(raw, _root);

        EnsureNoLinkEscape(full);
        return full;
    }

    /// <summary>尝试解析，越界时返回 false 而不抛异常。</summary>
    public bool TryResolve(string? relativePath, out string resolved)
    {
        try
        {
            resolved = Resolve(relativePath);
            return true;
        }
        catch (PathEscapeException)
        {
            resolved = _root;
            return false;
        }
    }

    /// <summary>判断一个绝对路径是否落在工作台内。</summary>
    public bool IsInside(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(absolutePath);
        }
        catch
        {
            return false;
        }

        if (full.Equals(_root, StringComparison.OrdinalIgnoreCase))
            return true;

        return full.StartsWith(_rootWithSep, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>绝对路径 → 相对工作台的路径（统一用 '/' 分隔，便于跨版本一致）。</summary>
    public string ToRelative(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        if (full.Equals(_root, StringComparison.OrdinalIgnoreCase))
            return ".";

        if (!IsInside(full))
            throw new PathEscapeException(absolutePath, _root);

        var rel = Path.GetRelativePath(_root, full);
        return rel.Replace(Sep, '/');
    }

    /// <summary>
    /// 判断是否「根一级文件」—— Python 版对这些文件在覆盖/追加/删除前会自动备份。
    /// </summary>
    public bool IsRootLevelFile(string relativePath)
    {
        var rel = relativePath.Replace('\\', '/').Trim('/');
        if (rel.Length == 0)
            return false;

        return rel.IndexOf('/') < 0;
    }

    /// <summary>
    /// 敏感文件判定。命中 .env / 密钥 / .git 等时返回原因。
    /// 注意：这是**读取前的报批依据**，不是访问控制 —— 真正放行与否由审批层决定。
    /// </summary>
    public SensitiveVerdict CheckSensitive(string relativePath)
    {
        var rel = relativePath.Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0)
            return SensitiveVerdict.Clean;

        var lower = rel.ToLowerInvariant();

        foreach (var prefix in SensitiveDirPrefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal))
                return SensitiveVerdict.Sensitive($"位于敏感目录 '{prefix.TrimEnd('/')}'");
        }

        var fileName = rel[(rel.LastIndexOf('/') + 1)..];

        foreach (var pattern in SensitiveNamePatterns)
        {
            if (WildcardMatch(fileName, pattern))
                return SensitiveVerdict.Sensitive($"文件名匹配敏感模式 '{pattern}'");
        }

        return SensitiveVerdict.Clean;
    }

    /// <summary>
    /// 校验 Windows 文件名合法性（设备名、尾随点/空格）。
    /// 返回 null 表示合法，否则返回原因。
    /// </summary>
    public static string? ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "文件名为空";

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "文件名含非法字符";

        var stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem))
            return $"'{stem}' 是 Windows 保留设备名";

        if (name.EndsWith('.') || name.EndsWith(' '))
            return "文件名不能以点或空格结尾";

        return null;
    }

    /// <summary>
    /// 逐级检查路径上的符号链接 / junction，防止「链接指向根之外」造成逃逸。
    /// Path.GetFullPath 只做字符串规范化，无法发现这种逃逸。
    /// </summary>
    private void EnsureNoLinkEscape(string full)
    {
        if (full.Equals(_root, StringComparison.OrdinalIgnoreCase))
            return;

        var rel = Path.GetRelativePath(_root, full);
        var parts = rel.Split(Sep, StringSplitOptions.RemoveEmptyEntries);

        var current = _root;
        foreach (var part in parts)
        {
            if (part == "..")
                throw new PathEscapeException(full, _root);

            current = Path.Combine(current, part);

            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);

            if (!info.Exists)
                break; // 尚不存在的路径段不可能是链接

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null && !IsInside(target.FullName))
                    throw new PathEscapeException(
                        $"{full}（经由链接 {current} → {target.FullName}）", _root);
            }
        }
    }

    /// <summary>
    /// 对外暴露的通配符匹配（供 ws_search 的 glob 参数复用）。
    /// 与其在那里再实现一份、迟早和这里的行为对不上，不如直接共用。
    /// </summary>
    public static bool GlobMatch(string input, string pattern)
        => WildcardMatch(input, pattern);

    /// <summary>简单的通配符匹配：支持 '*' 与 '?'，大小写不敏感。</summary>
    private static bool WildcardMatch(string input, string pattern)
        => WildcardMatch(input.AsSpan(), pattern.AsSpan());

    private static bool WildcardMatch(ReadOnlySpan<char> input, ReadOnlySpan<char> pattern)
    {
        var i = 0;
        var p = 0;
        var starIdx = -1;
        var match = 0;

        while (i < input.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(input[i])))
            {
                i++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                match = i;
                p++;
            }
            else if (starIdx >= 0)
            {
                p = starIdx + 1;
                match++;
                i = match;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

    /// <summary>全量哈希上限（供 ReadTicket 复用）。</summary>
    internal static long FullHashLimitBytes => FullHashLimit;
}
