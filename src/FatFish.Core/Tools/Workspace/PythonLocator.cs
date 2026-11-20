namespace FatFish.Core.Tools.Workspace;

/// <summary>找到的 Python 解释器。</summary>
/// <param name="FileName">可执行文件完整路径。</param>
/// <param name="PrefixArguments">调用时插在脚本路径前的参数（<c>py.exe</c> 需要 <c>-3</c>，其余为空）。</param>
/// <param name="Source">从哪儿找到的（FATFISH_PYTHON / venv / PATH …），用于把「用的是哪个 python」讲清楚。</param>
public sealed record PythonInterpreter(string FileName, string PrefixArguments, string Source)
{
    /// <summary>给人看的一行，形如 <c>E:\venv\Scripts\python.exe</c> 或 <c>C:\Windows\py.exe -3</c>。</summary>
    public string Describe => PrefixArguments.Length == 0
        ? FileName
        : FileName + " " + PrefixArguments;
}

/// <summary>查找结果。找不到时 <see cref="Error"/> 说明试过哪些地方。</summary>
public sealed record PythonResolution(PythonInterpreter? Interpreter, string? Error)
{
    public static PythonResolution Found(PythonInterpreter interpreter) => new(interpreter, null);
    public static PythonResolution NotFound(string error) => new(null, error);
}

/// <summary>
/// 找 Python 解释器。
///
/// Python 版的 <c>ws_run_python</c> 直接用了 <c>sys.executable</c> ——
/// 因为那个程序本身就是 Python 进程，随手就能拿到。
/// C# 版没有这个便利：本进程是 .NET，必须去系统里找一个。
/// 找错解释器的后果很隐蔽 —— 代码跑了，但 import 的库不在，
/// 报 ModuleNotFoundError，人会以为是自己的代码问题。
/// 所以这里不仅找，还要把「从哪儿找到的」原样带回给模型看。
///
/// 优先级（越高越先）：
///   1. <c>FATFISH_PYTHON</c> 环境变量 —— 显式指定，**找错了也不再回退**；
///   2. 工作台内的虚拟环境（venv / .venv / env 的 Scripts|bin）；
///   3. PATH 里的 python.exe / python3.exe；
///   4. PATH 里的 py.exe（Windows launcher），带 <c>-3</c> 参数。
///
/// 第 1 条刻意不做「找不到就继续往下找」：用户既然显式指定了路径，
/// 静默换成另一个解释器只会让人以为配置生效了，然后对着莫名其妙的
/// ModuleNotFoundError 排查半天。显式配置失败就该显式报错。
/// </summary>
public static class PythonLocator
{
    /// <summary>工作台内常见的虚拟环境目录名。</summary>
    private static readonly string[] VenvDirNames = ["venv", ".venv", "env"];

    /// <summary>虚拟环境内解释器的相对位置（Windows 在前，POSIX 在后）。</summary>
    private static readonly string[] VenvPythonPaths =
    [
        "Scripts/python.exe",
        "bin/python3",
        "bin/python",
    ];

    public static PythonResolution Resolve(string workspaceRoot)
    {
        // ---- 1. 显式指定 ----
        var configured = Environment.GetEnvironmentVariable("FATFISH_PYTHON");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = configured.Trim().Trim('"');

            if (!File.Exists(path))
            {
                return PythonResolution.NotFound(
                    $"FATFISH_PYTHON 指向的文件不存在：{path}。" +
                    "请把它改成 python.exe 的完整路径，或清空这个环境变量让程序自动查找。");
            }

            if (!IsUsable(path))
            {
                return PythonResolution.NotFound(
                    $"FATFISH_PYTHON 指向的 {path} 是一个 0 字节占位文件" +
                    "（通常是微软商店的 python 存根）。请指向真实的 python.exe。");
            }

            return PythonResolution.Found(new PythonInterpreter(path, string.Empty, "FATFISH_PYTHON"));
        }

        // ---- 2. 工作台内的虚拟环境 ----
        foreach (var dirName in VenvDirNames)
        {
            foreach (var rel in VenvPythonPaths)
            {
                var candidate = Path.Combine(
                    workspaceRoot,
                    dirName.Replace('/', Path.DirectorySeparatorChar),
                    rel.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(candidate) && IsUsable(candidate))
                {
                    return PythonResolution.Found(
                        new PythonInterpreter(candidate, string.Empty, $"工作台虚拟环境 {dirName}/"));
                }
            }
        }

        // ---- 3. PATH ----
        foreach (var exe in new[] { "python.exe", "python3.exe" })
        {
            var found = SearchPath(exe);
            if (found is not null)
                return PythonResolution.Found(new PythonInterpreter(found, string.Empty, "PATH"));
        }

        // ---- 4. Windows launcher ----
        var launcher = SearchPath("py.exe");
        if (launcher is not null)
            return PythonResolution.Found(new PythonInterpreter(launcher, "-3", "PATH (py launcher)"));

        return PythonResolution.NotFound(
            "未找到 Python 解释器。可以：" +
            "① 在工作台根目录建一个 venv（python -m venv venv）；" +
            "② 确保 python.exe 在 PATH 中；" +
            "③ 设置环境变量 FATFISH_PYTHON 指向 python.exe 的完整路径。");
    }

    /// <summary>在 PATH 里按目录逐个找可执行文件。</summary>
    private static string? SearchPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0)
                continue;

            string candidate;
            try
            {
                candidate = Path.Combine(dir, fileName);
            }
            catch (ArgumentException)
            {
                // PATH 里混了非法字符的条目，跳过
                continue;
            }

            if (File.Exists(candidate) && IsUsable(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// 是不是一个能用的可执行文件。
    ///
    /// 关键判断是「长度 > 0」：Windows 10/11 默认在
    /// <c>%LOCALAPPDATA%\Microsoft\WindowsApps\</c> 放一个 0 字节的 python.exe 存根，
    /// 执行它只会弹出微软商店。它 File.Exists 为 true，不拦就会中招。
    /// </summary>
    private static bool IsUsable(string path)
    {
        try
        {
            return new FileInfo(path).Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
