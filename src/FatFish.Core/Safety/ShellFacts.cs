using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace FatFish.Core.Safety;

/// <summary>一条方言预检命中。</summary>
/// <param name="Level">error（必然失败）/ warn（多半失败）/ info（提示）。</param>
/// <param name="Message">哪里错了。</param>
/// <param name="Fix">该怎么改。</param>
public sealed record ShellTrap(string Level, string Message, string Fix)
{
    public string LevelText => Level switch
    {
        "error" => "必然失败",
        "warn" => "多半失败",
        _ => "提示",
    };
}

/// <summary>
/// 机器环境事实层 —— 移植自 Python 版 <c>system_facts.py</c>。
///
/// 为什么审查环节需要它
/// --------------------
/// 在它出现之前，审查员只能「凭感觉」判断一条命令对不对 —— 因为它不知道
/// 目标 shell 是什么。于是两种错误同时发生：
///
///   · 把 <c>Get-ChildItem</c> 判为「可能有问题」，但说不出为什么（其实是必然失败）；
///   · 把 <c>grep</c> 判为错误，而当时机器恰好是 Linux（那是正确的写法）。
///
/// 这两种错都源于同一件事：**审查员在猜**。把环境事实和本地正则预检一起递给它，
/// 它就从「猜」变成了「在一份确定的判据上复核」。
///
/// 设计约束（与 Python 版一致）
/// --------------------------
/// · 只读、不联网、不写文件、不抛异常；
/// · 任何一步失败都降级为空结果，绝不打断核验主流程；
/// · 结果按进程缓存（同一进程内环境不会变）。
/// </summary>
public sealed class ShellFacts
{
    private static readonly object Gate = new();
    private static ShellFacts? _cached;

    public string OsLabel { get; }
    public string Arch { get; }
    public string Shell { get; }
    public string Encoding { get; }
    public bool IsWindows => Shell == "cmd.exe";

    private ShellFacts(string osLabel, string arch, string shell, string encoding)
    {
        OsLabel = osLabel;
        Arch = arch;
        Shell = shell;
        Encoding = encoding;
    }

    /// <summary>采集（进程内缓存）。永不抛异常。</summary>
    public static ShellFacts Collect()
    {
        if (_cached is not null)
            return _cached;

        lock (Gate)
        {
            if (_cached is not null)
                return _cached;

            _cached = Build();
            return _cached;
        }
    }

    private static ShellFacts Build()
    {
        var os = "?";
        var arch = "?";
        var enc = "?";

        try
        {
            os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? $"Windows {Environment.OSVersion.Version.Build}"
                : RuntimeInformation.OSDescription;
        }
        catch (Exception)
        {
            // 采集失败就用 "?"，不打断核验
        }

        try
        {
            arch = RuntimeInformation.ProcessArchitecture.ToString();
        }
        catch (Exception)
        {
        }

        try
        {
            enc = System.Text.Encoding.UTF8.WebName;
        }
        catch (Exception)
        {
        }

        // ★ 这里报的是「本程序执行命令时真正用的 shell」，不是用户终端里那个。
        //   ProcessRunner 在 Windows 上走 cmd /c，所以 PowerShell 写法在这里一律非法。
        var shell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh";

        return new ShellFacts(os, arch, shell, enc);
    }

    /// <summary>送审材料里的【运行环境事实】一段。</summary>
    public string RenderFactsBlock()
    {
        var sb = new StringBuilder();
        sb.Append("【运行环境事实（由主程序采集，可信；判断命令方言以此为准）】").Append('\n');
        sb.Append("· 系统：").Append(OsLabel).Append(" ｜ ").Append(Arch).Append('\n');
        sb.Append("· 执行 shell：").Append(Shell);

        if (IsWindows)
            sb.Append("（注意：**不是** PowerShell）");

        sb.Append('\n');
        sb.Append("· 控制台编码：").Append(Encoding);

        return sb.ToString();
    }

    // ==================================================================
    // 方言预检
    // ==================================================================

    /// <summary>cmd.exe 上会踩的坑。</summary>
    /// <remarks>
    /// 每条：(正则, 级别, 说明, 建议)。
    /// 刻意保留 Python 版里那些「注释掉才知道为什么」的判断，例如 curl 不算错。
    /// </remarks>
    private static readonly (string Pattern, string Level, string Message, string Fix)[] CmdTraps =
    {
        (@"\$env\s*:", "error",
            "cmd.exe 不认识 $env: 语法（那是 PowerShell）",
            "改用 set NAME=value 设置、%NAME% 引用"),

        (@"\b(Get|Set|Remove|New|Add|Clear|Copy|Move|Rename|Join|Split|Measure|Compare|" +
         @"Group|Sort|Format|Convert|Import|Export|Update|Restart|Resume|Suspend|Wait|" +
         @"Receive|Send|Register|Unregister|Enable|Disable|Show|Hide|Read|Enter|Exit|" +
         @"Push|Pop|Redo|Undo|Save|Lock|Unlock|Protect|Unprotect|Use|Unblock|Debug|" +
         @"Checkpoint|Invoke|Start|Stop|Select|Where|ForEach|Out|Write|Test)-[A-Z]\w+",
            "error",
            "这是 PowerShell cmdlet，cmd.exe 里没有",
            "改用对应的 cmd 命令，或显式写 powershell -Command \"...\""),

        (@"(^|[\s;&|])\$\{?\w+\}?(\s|=|$|"")", "warn",
            "cmd.exe 不展开 $NAME（那是 shell 变量）",
            "用 %NAME%"),

        (@"^\s*export\s+\w+=", "error",
            "export 是 bash/sh 语法，cmd 不支持",
            "改用 set NAME=value"),

        // 注：curl 不算 —— Windows 10 1803+ 起自带 curl.exe，写 curl 是**对的**。
        //     wget 仍算（cmd 里确实没有，除非用户自己装了）。
        (@"(^|[\s;&|])(grep|awk|sed|chmod|chown|which|wget|touch|ln)\b", "warn",
            "这些是 Unix 工具，cmd.exe 里通常不存在",
            "用 findstr / where，或直接写 Python 做"),

        (@"(^|[\s;&|])ls(\s|$)", "warn",
            "cmd 里 ls 不是内置命令（除非装了别名工具）",
            "用 dir"),

        (@"(^|[\s;&|])rm\s+-", "error",
            "rm -rf 是 Unix 写法，cmd 里没有",
            "用 rmdir /s /q 或 del /q"),

        (@"(^|[\s;&|])cp\s+", "warn", "cp 是 Unix 写法", "用 copy 或 xcopy/robocopy"),

        (@"(^|[\s;&|])cat\s+", "warn", "cat 是 Unix 写法", "用 type"),

        (@"\.ps1\b", "warn",
            "cmd 不能直接执行 .ps1 脚本",
            "写成 powershell -ExecutionPolicy Bypass -File x.ps1"),

        (@"(?<![=!<>])'(?:[^']*)'(?![=!<>])", "warn",
            "cmd.exe 不把单引号当引号，会连同引号一起传给程序",
            "改用双引号"),

        (@"(^|[|;&]\s*)head\b|(^|[|;&]\s*)tail\b", "warn",
            "cmd 没有 head / tail（那是 Unix 的）",
            "用 findstr，或改用 Python 读前/后 N 行"),

        (@"(^|[\s;&|])mkdir\s+-[pP]\b", "error",
            "cmd 的 mkdir 不支持 -p（那是 Unix 的递归建目录）",
            "改用 mkdir a\\b（cmd 默认就会建中间目录）"),

        (@"&&\s*$", "info", "以 && 结尾会让命令等待后文", "检查是否写完整"),
    };

    /// <summary>/bin/sh 上会踩的坑。</summary>
    private static readonly (string Pattern, string Level, string Message, string Fix)[] ShTraps =
    {
        (@"^\s*(dir|copy|del|type|set\s+\w+=|move|ren|cls|echo\s+%|tasklist|taskkill)\b",
            "error", "这是 Windows cmd 命令，/bin/sh 里不存在",
            "改用 ls / cp / rm / cat / export"),

        (@"%\w+%", "error", "%VAR% 是 cmd 语法，sh 里不展开", "改用 $VAR"),
    };

    /// <summary>
    /// 显式调用另一个 shell 时豁免预检。
    ///
    /// ★ 这条规则是踩过坑补上的：预检曾把 <c>powershell -NoProfile -Command "Get-ChildItem"</c>
    ///   判为「必然失败」—— 可那正是同一份文档推荐的正确写法。
    ///   自相矛盾的判据比没有判据更糟：审查员会因此把正确方案打回，
    ///   而执行者按「要求」改完后仍然被打回，最后陷入死循环。
    /// </summary>
    private static readonly Regex ForeignShell = new(
        @"(^|[\s;&|""(])(powershell|pwsh|bash|wsl)(\.exe)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex PythonInline = new(
        @"python", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>正则表编译一次就够（每轮核验都会用到）。</summary>
    private static readonly (Regex Rx, string Level, string Message, string Fix)[] CmdCompiled =
        CmdTraps.Select(t => (new Regex(t.Pattern, RegexOptions.Multiline | RegexOptions.Compiled),
            t.Level, t.Message, t.Fix)).ToArray();

    private static readonly (Regex Rx, string Level, string Message, string Fix)[] ShCompiled =
        ShTraps.Select(t => (new Regex(t.Pattern, RegexOptions.Multiline | RegexOptions.Compiled),
            t.Level, t.Message, t.Fix)).ToArray();

    /// <summary>
    /// 对一条命令做方言预检。永不抛异常；空命令返回空列表。
    /// </summary>
    public IReadOnlyList<ShellTrap> CheckCommand(string? command)
    {
        var hits = new List<ShellTrap>();

        if (string.IsNullOrWhiteSpace(command))
            return hits;

        try
        {
            if (ForeignShell.IsMatch(command))
                return hits;

            var table = IsWindows ? CmdCompiled : ShCompiled;

            foreach (var (rx, level, msg, fix) in table)
            {
                try
                {
                    if (rx.IsMatch(command))
                        hits.Add(new ShellTrap(level, msg, fix));
                }
                catch (RegexMatchTimeoutException)
                {
                    // 单条正则超时不该让整次核验失败
                }
            }

            // 单引号那条容易误报（Python 代码里常有），对 python -c 放行
            if (PythonInline.IsMatch(command) && command.Contains("-c", StringComparison.Ordinal))
                hits.RemoveAll(h => h.Message.Contains("单引号", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return hits;
        }

        // 去重（同一说明只留一条）
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<ShellTrap>(hits.Count);

        foreach (var hit in hits)
        {
            if (seen.Add(hit.Message))
                unique.Add(hit);
        }

        return unique;
    }

    /// <summary>
    /// 送审材料里的【机器预检结果】一段。
    /// </summary>
    /// <param name="commands">(动作序号后缀, 命令文本) 的列表；非命令类动作不要传进来。</param>
    public string RenderPrecheckBlock(IReadOnlyList<(string Name, string Command)> commands)
    {
        var hits = new List<(string Name, string Command, ShellTrap Trap)>();

        foreach (var (name, command) in commands)
        {
            if (string.IsNullOrWhiteSpace(command))
                continue;

            foreach (var trap in CheckCommand(command))
                hits.Add((name, command, trap));
        }

        if (hits.Count == 0)
        {
            return "【机器预检结果】\n" +
                   "已对送审命令做方言扫描，**未发现明显的 shell 方言错误**。" +
                   "（这只说明写法与目标 shell 相容，不代表业务逻辑正确。）";
        }

        var sb = new StringBuilder();
        sb.Append("【机器预检结果（本地正则判定，高可信）】").Append('\n');

        foreach (var (name, command, trap) in hits.Take(8))
        {
            sb.Append("· [").Append(trap.LevelText).Append("] ").Append(name)
              .Append('：').Append(trap.Message).Append('\n');
            sb.Append("    命令片段：").Append(Clip(command, 160)).Append('\n');
            sb.Append("    建议：").Append(trap.Fix).Append('\n');
        }

        sb.Append("★ 上述条目若确认为真，请直接判「变更」并在「要求」里写明正确写法。");

        return sb.ToString();
    }

    private static string Clip(string s, int n)
        => s.Length <= n ? s : s[..n] + $"…（已截断，原长 {s.Length}）";
}
