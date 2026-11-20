using System.Globalization;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// 工作台遍历的共用约定 —— ws_list 与 ws_search 必须用同一套过滤规则。
///
/// 如果两个工具各写一份「跳过哪些目录」，迟早会漂移：
/// 搜索能搜到 list 看不到的东西（或反过来），排查起来极其费劲。
/// </summary>
public static class WorkspaceFilters
{
    /// <summary>
    /// 默认不遍历的目录。三类：
    ///
    ///   1. 版本库元数据（.git / .svn / .hg）—— 内容不是"文件"，是对象库；
    ///   2. 构建与依赖产物（bin / obj / node_modules / venv / site-packages / dist …）——
    ///      由工具生成，读它对理解项目毫无帮助，却占了绝大多数文件数。
    ///      实测本机工作台：39,397 个文件里 venv 占 9,016 个、logs 8,860 个，
    ///      全量串行扫描要 72 秒 —— 光靠"跳过 venv"就能砍掉四分之一；
    ///   3. 编辑器与缓存（.vs / .idea / __pycache__ / .pytest_cache …）。
    ///
    /// ★ 这份名单是「默认值」，不是「禁止访问」：
    ///   ws_list 与 ws_search 都提供 include_ignored 参数作为逃生门。
    ///   宁可让人偶尔显式打开一次，也不要默认让一次检索变成几分钟的等待。
    /// </summary>
    public static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        // 版本库
        ".git", ".svn", ".hg",

        // 依赖与虚拟环境
        "node_modules", "bower_components",
        "venv", ".venv", "site-packages", "env",
        ".nuget", "packages",

        // 构建产物
        "bin", "obj", "Debug", "Release", "x64", "x86", "publish",
        "dist", "build", "target",

        // 缓存与编辑器
        "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox",
        ".vs", ".idea", ".vscode", "coverage", "htmlcov",

        // 本程序自己的临时目录
        ".fatfish_tmp",

        // 写前备案的旧副本（2026-11-06 起深层文件也会镜像到这里）。
        // 默认跳过：里面的 .bak 是完整源码副本，一次全文搜索会被同一个词命中十几遍，
        // 真正要找的当前版本反而被淹掉。确实要翻备份请显式传 include_ignored=true。
        "_backup",
    };

    /// <summary>
    /// 单文件视为「可搜索文本」的大小上限。
    /// 超过这个体积的文件基本是数据/日志/压缩包，全文扫描收益远小于开销。
    /// </summary>
    public const long MaxSearchFileBytes = 4L * 1024 * 1024;

    /// <summary>判断二进制用的采样长度。</summary>
    public const int BinarySampleBytes = 8 * 1024;

    /// <summary>人类可读的体积，例如 12B / 1.2K / 3.4M。</summary>
    public static string HumanSize(long bytes) => bytes switch
    {
        < 0 => "?",
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + "B",
        < 1024L * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + "G",
    };

    /// <summary>
    /// 去掉末尾换行与首尾空白，并把 CRLF 归一成 LF。
    /// 工具结果要进模型上下文，\r 是纯浪费的 token。
    /// </summary>
    public static string Normalize(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
}
