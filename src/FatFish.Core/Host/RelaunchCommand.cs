namespace FatFish.Core.Host;

/// <summary>一次「重启自己」所需的命令。</summary>
public sealed record RelaunchPlan(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>拼成 ProcessStartInfo.Arguments 用的命令行。</summary>
    public string ToCommandLine() => string.Join(" ", Arguments.Select(Quote));

    /// <summary>日志/提示里给人看的样子。</summary>
    public string Describe() => ToCommandLine().Length == 0 ? FileName : FileName + " " + ToCommandLine();

    /// <summary>按 Windows 命令行规则加引号。路径带空格是最常见的情况。</summary>
    private static string Quote(string s)
    {
        if (s.Length == 0)
            return "\"\"";

        if (s.IndexOfAny([' ', '\t', '"']) < 0)
            return s;

        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}

/// <summary>
/// 算出「重启自己」要执行什么。
/// </summary>
/// <remarks>
/// 为什么要单独抽出来（而不是在窗口里写两行 Process.Start）：
/// 这个程序有三种启动方式，argv 的形状各不相同，而写错了**不会有任何报错** ——
/// 只会表现为「点了重启，什么都没发生」或者「重启后参数丢了」。
///
///   · <c>dotnet run</c>：ProcessPath = dotnet.exe，argv[0] = …\FatFish.dll
///     → 必须带上 argv[0]，否则 dotnet 不知道要跑哪个 dll；
///   · 直接跑 <c>bin\…\FatFish.exe</c>：ProcessPath = FatFish.exe，argv[0] = 同一个路径
///     → 不能再把 argv[0] 当参数传，否则它会以为你要跑「一个叫 FatFish.exe.dll 的东西」；
///   · 发布后的单文件 exe：同第二种。
///
/// 判据是 argv[0] 的后缀：<c>.dll</c> 就需要宿主，其余都把它当可执行文件本身。
/// </remarks>
public static class RelaunchCommand
{
    public static RelaunchPlan Build(string? processPath, IReadOnlyList<string> commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        if (commandLine.Count == 0)
            throw new ArgumentException("命令行参数为空，无法重建重启命令", nameof(commandLine));

        var entry = commandLine[0];

        // 托管入口：dotnet <dll> [args...]
        if (entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var host = string.IsNullOrWhiteSpace(processPath) ? "dotnet" : processPath;

            return new RelaunchPlan(host, commandLine.ToArray());
        }

        // 原生入口：<exe> [args...]
        return new RelaunchPlan(
            string.IsNullOrWhiteSpace(processPath) ? entry : processPath,
            commandLine.Skip(1).ToArray());
    }

    // EntryAssemblyPath / CommandLine 这类「怎么拿到 argv」的逻辑不放这里：
    // 它依赖运行时 API，而运行时 API 在单测里给出的值与真实启动不一致。
    // 这里只做「给定 argv 该拼什么」这一件可测的事。
}
