using System.Diagnostics;
using System.Text;

namespace FatFish.Core.Tools.Workspace;

/// <summary>一次命令启动的产物。<see cref="TempFile"/> 需要在跑完后删除。</summary>
public sealed record CmdLaunch(ProcessStartInfo? Psi, string? TempFile, string? Error)
{
    public static CmdLaunch Fail(string error) => new(null, null, error);
}

/// <summary>
/// 把「一条命令」翻译成可执行的 <see cref="ProcessStartInfo"/>。
///
/// <c>ws_run_cmd</c> 与 <c>ws_bg_cmd</c> 共用这一份 —— 引号、编码、临时文件清理
/// 每一个都是踩过的坑，复制第二份必然只抄走顺眼的那一半。
///
/// ---- 为什么 cmd 要走临时 .bat（2026-10-29 修） ----
///
/// 旧做法是 <c>cmd.exe /d /s /c "&lt;命令原文&gt;"</c>。它对「命令自身含双引号」不可靠：
///
/// <code>
/// set ZZTEST=1 &amp; set ZZTEST          →  ZZTEST=1          ✔
/// set "ZZTEST2=2" &amp; set ZZTEST2      →  环境变量未定义      ✘
/// </code>
///
/// 原因在参数传递层，不在 cmd：<c>ProcessStartInfo.Arguments</c> 拼出的是
/// <c>/d /s /c "set "ZZTEST2=2" &amp; set ZZTEST2"</c>，
/// Windows 的 <c>CommandLineToArgvW</c> 按「引号成对」切分参数，
/// 内层引号把整串切成了几段 —— cmd 收到的已经不是原文。
///
/// 最要命的是**它不报错**：命令安静地少做了半件事，退出码仍然是 0。
///
/// 改成「先写临时 .bat，再执行它」之后：
///   · 命令原文不再经过<b>任何</b>命令行解析 —— 它是文件内容，引号就是引号；
///   · .bat 由 cmd 逐行解析，与用户手敲进批处理文件的行为完全一致；
///   · 顺带支持了多行命令（用 <c>\r\n</c> 分隔），这在旧写法里根本做不到；
///   · 路径由 <see cref="ProcessStartInfo.ArgumentList"/> 逐个参数传入，
///     引号由 .NET 自己负责 —— 不自己拼字符串，就不会拼错。
///
/// ---- 编码：三档 ----
///
/// cmd 解析 .bat 用的是**当前 OEM 代码页**（简中即 936/GBK），不是 UTF-8。
/// 所以：
///   1. 命令能完全用 OEM 编码表示 → 直接按 OEM 写，最稳；
///   2. 不能（例如含 emoji）→ 用 UTF-8 写，首行加 <c>chcp 65001</c> 再执行；
///   3. 写文件本身失败 → 报错，不静默降级成乱码。
///
/// 第 1 档覆盖了现实里几乎全部情况；第 2 档是兜底，不值得为它牺牲第 1 档的可靠性。
/// </summary>
public static class CmdShell
{
    public const string TempDirName = ".fatfish_tmp";

    /// <summary>UTF-8 兜底时插在 <c>@echo off</c> 之后的第一条命令。</summary>
    private const string ChcpUtf8 = "chcp 65001 >nul";

    /// <summary>
    /// 组装命令行。
    /// </summary>
    /// <param name="command">命令原文。</param>
    /// <param name="shell">cmd 或 powershell。</param>
    /// <param name="cwd">已解析、已校验的工作目录。</param>
    /// <param name="guard">用于确认临时脚本落在工作台内。</param>
    public static CmdLaunch Build(string command, string shell, string cwd, Safety.PathGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);

        switch (shell)
        {
            case "cmd":
            case "cmd.exe":
                return BuildCmd(command, cwd, guard);

            case "powershell":
            case "pwsh":
                return BuildPowerShell(command, cwd);

            default:
                return CmdLaunch.Fail($"shell 只支持 cmd 或 powershell，实际是 '{shell}'");
        }
    }

    // ------------------------------------------------------------------
    // cmd：临时 .bat
    // ------------------------------------------------------------------

    private static CmdLaunch BuildCmd(string command, string cwd, Safety.PathGuard guard)
    {
        var tmpDir = Path.Combine(cwd, TempDirName);

        var batName = $"cmd_{DateTime.Now:HHmmss_fff}_{Random.Shared.Next(100, 999)}.bat";
        var batPath = Path.Combine(tmpDir, batName);

        // 落点再确认一次：文件名是程序生成的，理论上不会越界，
        // 但这条检查是「将来有人改了文件名生成逻辑」时唯一的护栏。
        if (!guard.IsInside(batPath))
            return CmdLaunch.Fail($"临时脚本路径越界：{batPath}");

        var (content, encoding) = ComposeBatchFile(command);

        try
        {
            Directory.CreateDirectory(tmpDir);
            File.WriteAllBytes(batPath, encoding.GetBytes(content));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CmdLaunch.Fail($"写入临时批处理失败：{ex.Message}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            WorkingDirectory = cwd,
        };

        // ★ 用 ArgumentList 而不是 Arguments：
        //   .NET 会按 Windows 规则为每个参数加引号，路径里有空格也不会被拆开。
        //   自己拼 "/d /s /c \"" + path + "\"" 就是重犯旧错误。
        psi.ArgumentList.Add("/d");   // 跳过 AutoRun 注册表项
        psi.ArgumentList.Add("/s");   // 严格处理首尾引号

        // ★ 为什么是 `call "path"` 而不是直接 `"path"`（踩过一次，记在这）：
        //
        //   cmd 的 /s /c 有一条古怪规则：命令串以引号开头、以引号结尾时，
        //   会**剥掉首尾引号**再把剩余的当命令。于是
        //       cmd /d /s /c "C:\my dir\x.bat"
        //   被剥成
        //       C:\my dir\x.bat
        //   引号没了，空格就露出来了 —— cmd 把 `C:\my` 当成要执行的程序，
        //   报「不是内部或外部命令」，退出码 1，stdout 空。
        //   症状是「路径里有空格就跑不了」，而路径恰好是最不容易被怀疑的那一环。
        //
        //   加上 call 之后，命令串不再以引号开头（以 `call` 开头），
        //   上面那条规则不触发；引号原样留给 call 解析，空格被正确保护。
        //   实测这是 Windows 上最稳的写法，且不需要手动拼双层引号。
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("call");
        psi.ArgumentList.Add(batPath);

        return new CmdLaunch(psi, batPath, null);
    }

    /// <summary>
    /// 拼出批处理文件内容，并决定用哪个编码写。
    /// </summary>
    private static (string Content, Encoding Encoding) ComposeBatchFile(string command)
    {
        // 行尾统一 CRLF：.bat 里只有 LF 时，少数结构（label / goto / 多行括号块）
        // 会出现难以复现的诡异行为。既然写文件了，就别省这一步。
        var body = command.Replace("\r\n", "\n").Replace('\r', '\n')
                          .Replace("\n", "\r\n")
                          .TrimEnd('\r', '\n');

        // @echo off 必须在首行：否则 .bat 会把每条命令先回显一遍到 stdout，
        // 输出里全是 "E:\>echo hi" 这种噪音，模型的上下文就被这些占满了。
        var plain = "@echo off\r\n" + body + "\r\n";

        if (TryEncodeOem(plain, out var oem))
            return (plain, oem);

        // OEM 表示不了的字符（emoji 之类）→ UTF-8 + 切换代码页
        return ("@echo off\r\n" + ChcpUtf8 + "\r\n" + body + "\r\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// 尝试用 OEM 代码页编码。不能表示时返回 false（而不是写出一串 '?'）。
    /// </summary>
    private static bool TryEncodeOem(string text, out Encoding encoding)
    {
        encoding = ProcessEncoding.Oem;

        try
        {
            var strict = Encoding.GetEncoding(
                ProcessEncoding.OemCodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);

            _ = strict.GetBytes(text);
            encoding = strict;
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // 该代码页不可用（非 Windows 平台）
            return false;
        }
    }

    // ------------------------------------------------------------------
    // powershell：-EncodedCommand
    // ------------------------------------------------------------------

    private static CmdLaunch BuildPowerShell(string command, string cwd)
    {
        // -EncodedCommand 接收 Base64(UTF-16LE)，彻底绕开引号与转义问题。
        // 相比拼 "-Command \"" + command + "\""，这个写法对
        // 中文、`$`、反引号、嵌套引号都免疫。
        //
        // 前面那半句不是装饰：输出被重定向时，PowerShell 会把**进度流**
        // 序列化成 CLIXML 塞进 stderr（实测一条 Get-ChildItem 就多出 596 字节
        // "#< CLIXML ... <Obj S=\"progress\""）。模型会把它当成错误信息读，
        // 既费 token 又误导。关掉进度后实测 stderr 归零。
        const string Quiet = "$ProgressPreference='SilentlyContinue';";

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Quiet + command));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = cwd,
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);

        return new CmdLaunch(psi, null, null);
    }

    /// <summary>
    /// 删除临时批处理。失败静默 —— 进程刚被杀时文件句柄可能还没释放，
    /// 而 <c>.fatfish_tmp</c> 本来就在忽略名单里，留一个不影响任何事。
    /// </summary>
    public static void Cleanup(string? tempFile)
    {
        if (string.IsNullOrEmpty(tempFile))
            return;

        try
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
