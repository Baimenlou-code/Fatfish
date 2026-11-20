using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Diagnostics;
using FatFish.Core.Llm;

namespace FatFish.App;

/// <summary>
/// 「工作台文件」树 → 对话上下文：右键菜单。
///
/// 为什么要做这个：文件树原本只有「双击用系统程序打开」。
/// 想让模型知道某个文件的内容，只能自己把内容复制到输入框里粘进去 ——
/// 遇到几万行的日志或代码，这步根本做不成。
///
/// 两档取舍（这是用户点名要的两版）：
///   · 原文读取   —— 整篇直接进历史。0 次模型调用，一个字都不丢。
///                  代价是吃上下文窗口，大文件会挤掉前面聊的内容。
///   · 摘要读取   —— 先让模型把文件读一遍、压成摘要，再把摘要进历史。
///                  多花一次模型调用，换来几十分之一的 token 占用。
///
/// ★ 不用「把内容填进输入框让用户按回车」的原因：
///   那会立刻触发一次完整回合（模型回一段话），既慢又贵；
///   而用户想要的只是「让它知道这个文件」。
/// </summary>
public partial class MainWindow
{
    /// <summary>原文进上下文的字符上限。超出截断 —— 上下文窗口不是无限的。</summary>
    private const int RawInjectLimit = 120_000;

    /// <summary>送摘要前的原文上限（超过就只摘要前面的部分）。</summary>
    private const int SummarySourceLimit = 200_000;

    /// <summary>允许整篇读入的字节上限，超过直接拒绝，不去分配那块内存。</summary>
    private const long MaxReadBytes = 16L * 1024 * 1024;

    /// <summary>在构造函数里调用一次，挂上右键菜单与右键选中行为。</summary>
    private void AttachFileReadMenu()
    {
        var menu = new ContextMenu();

        var raw = new MenuItem { Header = "原文读取（整篇进上下文）" };
        raw.ToolTip = "把整个文件按原样塞进对话上下文。\n"
                    + "不花模型调用，内容一个字都不改。\n"
                    + "代价：吃掉上下文窗口，文件很大时会挤掉前面聊的内容。";
        raw.Click += (_, _) => ReadSelectedFileRaw();
        menu.Items.Add(raw);

        var summary = new MenuItem { Header = "摘要读取（AI 先读一遍）" };
        summary.ToolTip = "先让模型把文件读一遍、压成摘要，再把摘要放进上下文。\n"
                        + "多花一次模型调用，但只占几百字 —— 适合长文档、大日志。\n"
                        + "摘要质量取决于模型，具体数字可能被压掉。";
        summary.Click += async (_, _) => await ReadSelectedFileSummaryAsync().ConfigureAwait(true);
        menu.Items.Add(summary);

        menu.Items.Add(new Separator());

        var copy = new MenuItem { Header = "复制路径" };
        copy.ToolTip = "把选中文件的完整路径复制到剪贴板。";
        copy.Click += (_, _) => CopySelectedPath();
        menu.Items.Add(copy);

        FileTree.ContextMenu = menu;

        // ★ 右键时先把光标下的那一项选中。
        //   不这么做的话，菜单永远作用于「上一次左键点过的文件」——
        //   右键 A、结果读了 B，而且毫无提示。
        FileTree.PreviewMouseRightButtonDown += OnTreeRightButtonDown;
    }

    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = ItemsControl.ContainerFromElement(FileTree, e.OriginalSource as DependencyObject);

        if (hit is TreeViewItem item)
            item.IsSelected = true;
    }

    // ------------------------------------------------------------------
    // 两档读取
    // ------------------------------------------------------------------

    private void ReadSelectedFileRaw()
    {
        if (_agent is null)
        {
            AppendMessage("system", "内核尚未就绪，无法写入上下文。");
            return;
        }

        if (!TryGetSelectedFile(out var path, out var why))
        {
            AppendMessage("system", why);
            return;
        }

        if (!TryReadTextFile(path, RawInjectLimit, out var text, out var problem))
        {
            AppendMessage("system", $"❌ 读不了 {Path.GetFileName(path)}：{problem}");
            return;
        }

        var rel = Rel(path);

        var payload = $"【文件原文】{rel}（共 {text.Length:N0} 字）\n"
                    + "（这是用户从左侧文件树手动放进上下文的整篇内容，不是工具调用结果。）\n"
                    + "```\n" + text + "\n```";

        _agent.InjectContext(payload);

        AppendMessage("system", $"📄 已按原文读入：{rel}（{text.Length:N0} 字），已进上下文。");
        StatusText = "已读入原文：" + Path.GetFileName(path);
    }

    private async Task ReadSelectedFileSummaryAsync()
    {
        if (_agent is null || _llm is null)
        {
            AppendMessage("system", "内核尚未就绪，无法摘要。");
            return;
        }

        if (_isRunning)
        {
            AppendMessage("system", "正在跑回合，等它结束后再读。");
            return;
        }

        if (!TryGetSelectedFile(out var path, out var why))
        {
            AppendMessage("system", why);
            return;
        }

        if (!TryReadTextFile(path, SummarySourceLimit, out var text, out var problem))
        {
            AppendMessage("system", $"❌ 读不了 {Path.GetFileName(path)}：{problem}");
            return;
        }

        var rel = Rel(path);
        var name = Path.GetFileName(path);

        AppendMessage("system", $"🧠 正在让模型读一遍 {rel}（{text.Length:N0} 字）…");
        StatusText = "正在摘要：" + name;

        try
        {
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(
                    "你是一个文件摘要器。只输出摘要本身：不要客套，不要复述指令，" +
                    "不要写「好的，我来总结」这类开头。"),

                ChatMessage.User(
                    $"文件名：{name}\n\n"
                    + "--- 文件内容开始 ---\n"
                    + text + "\n"
                    + "--- 文件内容结束 ---\n\n"
                    + "请输出摘要，包含：\n"
                    + "1) 这是什么文件、大概用途；\n"
                    + "2) 主要结构与关键段落（有条目就列条目）；\n"
                    + "3) 值得记住的具体事实：路径、数字、函数名、结论 —— 原样保留，不要含糊化；\n"
                    + "4) 若有待办、风险或可疑之处，单独指出。\n"
                    + "控制在 600 字以内。"),
            };

            // 不带工具：摘要任务只需要一次纯文本生成。
            var turn = await _llm.CompleteAsync(messages, null, null, null).ConfigureAwait(true);
            var summary = (turn.Text ?? string.Empty).Trim();

            if (summary.Length == 0)
            {
                AppendMessage("system", "❌ 模型没有返回摘要（流为空）。");
                StatusText = "摘要失败";
                return;
            }

            var payload = $"【文件摘要】{rel}（原文 {text.Length:N0} 字，已由模型压缩）\n" + summary;

            _agent.InjectContext(payload);
            AppendMessage("system", payload);

            StatusText = $"已读入摘要：{name}（{summary.Length:N0} 字）";
            Log($"文件摘要入上下文：{rel}（原文 {text.Length} 字 → 摘要 {summary.Length} 字）");
        }
        catch (OperationCanceledException)
        {
            StatusText = "摘要已取消";
        }
        catch (LlmException ex)
        {
            AppendMessage("system", "❌ 摘要失败：" + ex.Message);
            StatusText = "摘要失败";
        }
        catch (Exception ex)
        {
            AppendMessage("system", "❌ 摘要失败：" + ex.GetType().Name + ": " + ex.Message);
            StatusText = "摘要失败";
        }
    }

    // ------------------------------------------------------------------
    // 选中 / 读盘 / 路径
    // ------------------------------------------------------------------

    private bool TryGetSelectedFile(out string path, out string why)
    {
        path = string.Empty;
        why = string.Empty;

        if (FileTree.SelectedItem is not TreeViewItem node || node.Tag is not string p)
        {
            why = "先在左侧文件树里选中一个文件（目录不行）。";
            return false;
        }

        if (Directory.Exists(p))
        {
            why = "选中的是目录。右键菜单只对文件生效。";
            return false;
        }

        path = p;
        return true;
    }

    /// <summary>
    /// 读成文本。二进制、空文件、超大文件都返回 false 并给出人话原因 ——
    /// 把一堆乱码塞进上下文比读失败更糟。
    /// </summary>
    private static bool TryReadTextFile(string path, int limit, out string text, out string problem)
    {
        text = string.Empty;
        problem = string.Empty;

        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
            {
                problem = "文件不存在。";
                return false;
            }

            if (info.Length == 0)
            {
                problem = "空文件。";
                return false;
            }

            if (info.Length > MaxReadBytes)
            {
                problem = $"文件 {info.Length / 1024 / 1024} MB，超过上限，没有读。";
                return false;
            }

            var bytes = File.ReadAllBytes(path);

            // 二进制判定：头 4 KB 里出现 NUL 字节就不当文本。
            // 图片 / 压缩包 / 可执行文件都会在这里挡住。
            var probe = Math.Min(bytes.Length, 4096);
            for (var i = 0; i < probe; i++)
            {
                if (bytes[i] == 0)
                {
                    problem = "看起来是二进制文件（含 NUL 字节），没有可读文本。";
                    return false;
                }
            }

            text = Encoding.UTF8.GetString(bytes);

            if (text.Length > limit)
            {
                text = text[..limit] + $"\n…（全文超过 {limit:N0} 字，已截断）";
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>尽量显示相对工作台的路径，越界时退回绝对路径。</summary>
    private string Rel(string path)
    {
        try
        {
            var root = _guard?.Root;

            if (!string.IsNullOrEmpty(root))
            {
                var rel = Path.GetRelativePath(root, path);

                if (!rel.StartsWith("..", StringComparison.Ordinal))
                    return rel;
            }
        }
        catch (Exception)
        {
            // 路径换算失败不该影响读取本身
        }

        return path;
    }

    private void CopySelectedPath()
    {
        if (FileTree.SelectedItem is not TreeViewItem node || node.Tag is not string p)
            return;

        try
        {
            Clipboard.SetText(p);
            StatusText = "已复制路径：" + p;
        }
        catch (Exception ex)
        {
            AppendMessage("system", "复制路径失败：" + ex.Message);
        }
    }
}

// ======================================================================
// 过往对话记录（logs/sessions/chat_*.jsonl）
// ======================================================================

public partial class MainWindow
{
    /// <summary>菜单里最多列多少段历史会话。多了要翻滚动条，反而挑不出来。</summary>
    private const int SessionListLimit = 20;

    /// <summary>
    /// 面板标题栏那个「读取记录」按钮 → 弹出最近会话列表。
    ///
    /// 为什么不把它们也塞进文件树的右键菜单：
    ///   会话文件在 logs/sessions/ 里，得先展开两层目录才点得到，
    ///   而「接着上次的聊」是个高频动作 —— 值得一个一步到位的入口。
    /// </summary>
    private void OnReadSessionMenu(object sender, RoutedEventArgs e)
    {
        var files = ListSessionFiles();

        if (files.Count == 0)
        {
            AppendMessage("system", "还没找到会话记录（logs/sessions 里是空的）。");
            return;
        }

        var menu = new ContextMenu { PlacementTarget = SessionReadButton };

        foreach (var f in files)
        {
            var item = new MenuItem { Header = DescribeSessionFile(f) };

            var raw = new MenuItem { Header = "原文读入" };
            raw.ToolTip = "把这段旧对话按原文放进上下文。不花模型调用。";
            raw.Click += (_, _) => ReadSessionFileRaw(f.FullName);
            item.Items.Add(raw);

            var sum = new MenuItem { Header = "摘要读入" };
            sum.ToolTip = "先让模型把这段旧对话归纳一遍，再把归纳结果放进上下文。";
            sum.Click += async (_, _) => await ReadSessionFileSummaryAsync(f.FullName).ConfigureAwait(true);
            item.Items.Add(sum);

            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var where = new MenuItem { Header = "打开 logs/sessions 目录" };
        where.Click += (_, _) => OpenSessionFolder();
        menu.Items.Add(where);

        menu.IsOpen = true;
    }

    private void OpenSessionFolder()
    {
        try
        {
            var root = _guard?.Root;

            if (string.IsNullOrEmpty(root))
                return;

            var dir = Path.Combine(root, "logs", "sessions");
            Directory.CreateDirectory(dir);

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendMessage("system", "打开目录失败：" + ex.Message);
        }
    }

    private List<FileInfo> ListSessionFiles(int max = SessionListLimit)
    {
        var empty = new List<FileInfo>();
        var root = _guard?.Root;

        if (string.IsNullOrEmpty(root))
            return empty;

        var dir = Path.Combine(root, "logs", "sessions");

        if (!Directory.Exists(dir))
            return empty;

        try
        {
            // 与 SessionStore.LoadLatest 同一套排序：文件名里编了毫秒时间戳，字典序就是时间序。
            return new DirectoryInfo(dir)
                .EnumerateFiles("chat_*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Take(max)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return empty;
        }
    }

    /// <summary>菜单里那一行的文字：文件名形如 chat_20261102_001324_012_ab12.jsonl。</summary>
    private static string DescribeSessionFile(FileInfo f)
    {
        var stamp = "时间未知";
        var parts = Path.GetFileNameWithoutExtension(f.Name).Split('_');

        if (parts.Length >= 3 && parts[0] == "chat"
            && DateTime.TryParseExact(
                parts[1] + "_" + parts[2],
                "yyyyMMdd_HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var time))
        {
            stamp = time.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        var kb = f.Length / 1024.0;

        return $"{stamp}　{kb:0.0} KB";
    }

    // ------------------------------------------------------------------
    // 两档读取（会话版）
    // ------------------------------------------------------------------

    private void ReadSessionFileRaw(string path)
    {
        if (_agent is null)
        {
            AppendMessage("system", "内核尚未就绪，无法写入上下文。");
            return;
        }

        if (!TryLoadSession(path, out var record, out var problem))
        {
            AppendMessage("system", "❌ 读不了这段记录：" + problem);
            return;
        }

        var name = Path.GetFileName(path);
        var text = RenderSession(record, RawInjectLimit);

        var payload = $"【过往对话记录】{name}\n"
                    + $"（{record.Messages.Count} 条对话 / 文件里共 {record.TotalEntries} 条记录，"
                    + $"最后写入 {record.LastWrite:yyyy-MM-dd HH:mm}。"
                    + "这是用户手动放进上下文的旧记录，不是当前会话。）\n\n"
                    + text;

        _agent.InjectContext(payload);

        AppendMessage("system", $"🗂 已按原文读入过往对话：{name}（{record.Messages.Count} 条，{text.Length:N0} 字）。");
        StatusText = "已读入旧对话：" + name;
    }

    private async Task ReadSessionFileSummaryAsync(string path)
    {
        if (_agent is null || _llm is null)
        {
            AppendMessage("system", "内核尚未就绪，无法摘要。");
            return;
        }

        if (_isRunning)
        {
            AppendMessage("system", "正在跑回合，等它结束后再读。");
            return;
        }

        if (!TryLoadSession(path, out var record, out var problem))
        {
            AppendMessage("system", "❌ 读不了这段记录：" + problem);
            return;
        }

        var name = Path.GetFileName(path);
        var text = RenderSession(record, SummarySourceLimit);

        AppendMessage("system", $"🧠 正在归纳过往对话 {name}（{record.Messages.Count} 条）…");
        StatusText = "正在归纳：" + name;

        try
        {
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(
                    "你是一个对话记录归纳器。只输出归纳结果：不要客套，" +
                    "不要复述指令，不要编造记录里没有的内容。"),

                ChatMessage.User(
                    "下面是一段**已经结束的**旧对话记录（用户与助手的往来）。\n\n"
                    + "--- 记录开始 ---\n" + text + "\n--- 记录结束 ---\n\n"
                    + "请归纳，包含：\n"
                    + "1) 用户提出过哪些需求、要求、偏好与禁忌；\n"
                    + "2) 得出了哪些结论、做了哪些决定；\n"
                    + "3) 涉及的关键文件、路径、命令、数字 —— 原样保留，不要含糊化；\n"
                    + "4) 未完成的事、待办、悬而未决的问题；\n"
                    + "5) 下次接着聊需要知道的上下文。\n"
                    + "控制在 800 字以内。"),
            };

            var turn = await _llm.CompleteAsync(messages, null, null, null).ConfigureAwait(true);
            var summary = (turn.Text ?? string.Empty).Trim();

            if (summary.Length == 0)
            {
                AppendMessage("system", "❌ 模型没有返回归纳结果（流为空）。");
                StatusText = "归纳失败";
                return;
            }

            var payload = $"【旧对话摘要】{name}（{record.Messages.Count} 条对话，"
                        + $"最后写入 {record.LastWrite:yyyy-MM-dd HH:mm}，已由模型压缩）\n" + summary;

            _agent.InjectContext(payload);
            AppendMessage("system", payload);

            StatusText = $"已读入旧对话摘要：{name}（{summary.Length:N0} 字）";
            Log($"旧对话摘要入上下文：{name}（{record.Messages.Count} 条 → {summary.Length} 字）");
        }
        catch (OperationCanceledException)
        {
            StatusText = "归纳已取消";
        }
        catch (LlmException ex)
        {
            AppendMessage("system", "❌ 归纳失败：" + ex.Message);
            StatusText = "归纳失败";
        }
        catch (Exception ex)
        {
            AppendMessage("system", "❌ 归纳失败：" + ex.GetType().Name + ": " + ex.Message);
            StatusText = "归纳失败";
        }
    }

    // ------------------------------------------------------------------
    // 会话文件解析
    // ------------------------------------------------------------------

    /// <summary>
    /// 用内核自己的 <see cref="FatFish.Core.Session.SessionStore"/> 解析，
    /// 而不是在这里自己读 JSONL。
    ///
    /// 理由：会话文件里混着 user / assistant / tool / system 四类行，
    /// 「哪些能安全回填给模型」这件事已经由 SessionStore 定义好了
    /// （剥掉工具往返、丢掉末尾悬空的 user、从 user 开始切）。
    /// 界面若自己再实现一遍，迟早和内核说得不一样。
    /// </summary>
    private static bool TryLoadSession(string path, out FatFish.Core.Session.SessionLoadResult record, out string problem)
    {
        problem = string.Empty;

        // maxMessages: 0 = 全取，截断交给下面的字符上限控制。
        var loaded = FatFish.Core.Session.SessionStore.LoadFile(path, maxMessages: 0);

        if (loaded is null || loaded.Messages.Count == 0)
        {
            record = null!;
            problem = "文件解析不出可读对话（可能只有工具记录，或者是空文件）。";
            return false;
        }

        record = loaded;
        return true;
    }

    /// <summary>把会话渲染成人类可读的纯文本。超上限就地截断。</summary>
    private static string RenderSession(FatFish.Core.Session.SessionLoadResult record, int limit)
    {
        var sb = new StringBuilder(Math.Min(limit, 64 * 1024));

        foreach (var m in record.Messages)
        {
            sb.Append(m.Role == "user" ? "用户：" : "肥鱼：")
              .AppendLine(m.Content ?? string.Empty)
              .AppendLine();

            if (sb.Length > limit)
            {
                sb.Append("…（记录过长，已截断于 ")
                  .Append(limit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
                  .AppendLine(" 字）");
                break;
            }
        }

        return sb.ToString();
    }
}
