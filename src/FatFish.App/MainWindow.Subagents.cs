using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FatFish.App;

/// <summary>
/// 子代理面板 —— 顶栏按钮 → 左侧列表（主循环居首）→ 右侧查看器。
///
/// <para>
/// 三条硬规矩（对着用户的原话写的）：
/// </para>
/// <list type="number">
///   <item>
///     <b>不许阻塞。</b>所有数据都来自 logs/subagent_jobs 下的文件，用 DispatcherTimer
///     每秒增量读一次；切列表项只改 Visibility 与 TextBox.Text。
///     这里**没有**任何 ShowDialog / Wait / 起进程后等待 —— 打开面板不该让你等。
///   </item>
///   <item>
///     <b>对话与思考分开。</b>「对话」页只放 content / 插话 / 工具结果；
///     「过程」页放 reasoning + 工具调用时间线。deepseek-flash 不返回思考时，
///     过程页会明确写「该模型未返回思考」，而不是留白骗人。
///   </item>
///   <item>
///     <b>插话与叫停是写标记，不是调用。</b>插话 = 往 &lt;job&gt;.inbox.jsonl 追加一行；
///     叫停 = 往 &lt;job&gt;.stop 写一个时间戳。worker 自己在下一步开头取。
///     这样 UI 线程上永远是零等待操作。
///   </item>
/// </list>
///
/// <para>
/// 数据来源（都由插件的 worker 写）：
/// <c>&lt;job&gt;.json</c> 状态；<c>&lt;job&gt;.talk.jsonl</c> 结构化全过程；
/// <c>&lt;job&gt;.inbox.jsonl</c> 插话；<c>&lt;job&gt;.stop</c> 叫停标记。
/// </para>
/// </summary>
public partial class MainWindow
{
    private const string SubJobDir = "subagent_jobs";

    // 单个 talk 文件最多读这么多；再大就只读尾部（避免一两秒一次的 IO 把界面拖慢）
    private const long SubTalkLimit = 2 * 1024 * 1024;

    private DispatcherTimer? _subTimer;
    private readonly List<SubJob> _subJobs = new();
    private string _subSignature = "";       // 列表内容指纹：没变就不重建控件（保住选中项与滚动位置）
    private string? _selectedJob;            // 选中的 job id；null/"" = 主循环
    private string _talkStamp = "";          // 「job + 文件大小」：没变就不重新渲染

    // ---------- 「过程」页的真控制台（2026-11-13） ----------
    // 移植自 Python 版 conlog.py：conhost 窗口嵌进面板，写入只改字符格子。
    // 起不来就退回 SubTraceBox（可见性在 RenderTraceText 里切）。
    private ConsoleLog? _traceConsole;
    private ConsoleStage? _traceStage;
    private bool _traceConsoleStarting;
    private bool _tracePopped;               // true = 已弹成独立窗口
    private string _traceSent = "";          // 已经写进控制台的全文（增量用）
    private string? _traceStartError;        // 宿主起不来时的原因（日志里要说清）

    /// <summary>一个子代理的列表行所需字段。</summary>
    private sealed class SubJob
    {
        public string Id = "";
        public string Label = "";
        public string Status = "";
        public int Step;
        public int MaxSteps;
        public long Tokens;
        public string Updated = "";
        public string AnswerFile = "";
        public string TalkFile = "";
        public int Pid;
    }

    private static bool IsRunning(string status)
        => status is "starting" or "running";

    private string SubFolder => Path.Combine(_guard?.Root ?? ".", "logs", SubJobDir);

    // ------------------------------------------------------------------
    // 入口：顶栏按钮
    // ------------------------------------------------------------------

    private void OnToggleSubagents(object sender, RoutedEventArgs e)
    {
        var show = SubagentPanel.Visibility != Visibility.Visible;

        if (show)
        {
            // 与文件树共用左侧那一格：进来就把树收起来，免得两层叠着看不清
            SidePanel.Visibility = Visibility.Collapsed;

            if (SidebarColumn.ActualWidth < 1)
            {
                SidebarColumn.Width = new GridLength(240);
                SideSplitterColumn.Width = new GridLength(5);
                SideSplitter.Visibility = Visibility.Visible;
            }

            SubagentPanel.Visibility = Visibility.Visible;
            RefreshSubList(force: true);
            EnsureSubTimer();
            EnsureTraceConsole();
        }
        else
        {
            SubagentPanel.Visibility = Visibility.Collapsed;
            SidePanel.Visibility = Visibility.Visible;
            MaybeStopSubTimer();
        }
    }

    private void EnsureSubTimer()
    {
        if (_subTimer is null)
        {
            _subTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _subTimer.Tick += (_, _) =>
            {
                if (SubagentPanel.Visibility == Visibility.Visible)
                    RefreshSubList(force: false);

                // 查看器开着时刷新当前 job：文件没变就什么都不做
                if (SubagentViewer.Visibility == Visibility.Visible && _selectedJob is { Length: > 0 })
                    RenderJob(_selectedJob, force: false);

                MaybeStopSubTimer();
            };
        }

        _subTimer.Start();
    }

    private void MaybeStopSubTimer()
    {
        if (_subTimer is null)
            return;

        // 列表和查看器都不在，就别再每秒读盘了
        if (SubagentPanel.Visibility != Visibility.Visible &&
            SubagentViewer.Visibility != Visibility.Visible)
        {
            _subTimer.Stop();
        }
    }

    // ------------------------------------------------------------------
    // 列表
    // ------------------------------------------------------------------

    private void RefreshSubList(bool force)
    {
        var root = _guard?.Root;

        if (string.IsNullOrWhiteSpace(root))
            return;

        var folder = SubFolder;
        var fresh = new List<SubJob>();

        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                // <job>.ledger.json 也是 .json 结尾，但它是账本数组，不是状态
                if (file.EndsWith(".ledger.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var job = TryReadJob(file);

                if (job is not null)
                    fresh.Add(job);
            }
        }

        fresh.Sort((a, b) =>
        {
            var ra = IsRunning(a.Status) ? 0 : 1;
            var rb = IsRunning(b.Status) ? 0 : 1;

            if (ra != rb)
                return ra - rb;

            return string.CompareOrdinal(b.Updated, a.Updated);
        });

        var signature = string.Join("|", fresh.Select(j => $"{j.Id}:{j.Status}:{j.Step}"));

        if (!force && signature == _subSignature)
        {
            UpdateSubBadge(fresh);
            return;
        }

        _subSignature = signature;
        _subJobs.Clear();
        _subJobs.AddRange(fresh);

        SubagentList.Items.Clear();
        SubagentList.Items.Add(BuildMainRow());

        foreach (var job in _subJobs)
            SubagentList.Items.Add(BuildJobRow(job));

        // 恢复选中（重建会丢）
        SelectSubRow(_selectedJob);

        UpdateSubBadge(_subJobs);
    }

    private ListBoxItem BuildMainRow()
    {
        var item = new ListBoxItem { Tag = "" };

        var panel = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
        panel.Children.Add(new TextBlock
        {
            Text = "主循环（本对话）",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "← 回到聊天",
            FontSize = 10,
            Foreground = ResBrush("FgMuted"),
        });

        item.Content = panel;
        return item;
    }

    private ListBoxItem BuildJobRow(SubJob job)
    {
        var (glyph, brushKey) = job.Status switch
        {
            "running" or "starting" => ("●", "IconBlue"),
            "done" => ("✓", "IconGreen"),
            "stopped" => ("⏸", "IconAmber"),
            "failed" => ("✕", "IconRed"),
            _ => ("·", "IconGray"),
        };

        var item = new ListBoxItem { Tag = job.Id };

        var panel = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(job.Label) ? job.Id : job.Label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.ToolTip = job.Id + "\n" + job.Status;
        panel.Children.Add(title);

        var meta = new TextBlock { FontSize = 10, Foreground = ResBrush("FgMuted") };
        meta.Inlines.Add(new System.Windows.Documents.Run(glyph) { Foreground = ResBrush(brushKey) });
        meta.Inlines.Add(new System.Windows.Documents.Run(
            $" {job.Step}/{job.MaxSteps} 步 · {job.Tokens / 1000.0:0.#}k token"));
        panel.Children.Add(meta);

        item.Content = panel;
        return item;
    }

    private void SelectSubRow(string? id)
    {
        foreach (var obj in SubagentList.Items)
        {
            if (obj is ListBoxItem item && (string?)item.Tag == (id ?? ""))
            {
                if (!ReferenceEquals(SubagentList.SelectedItem, item))
                    SubagentList.SelectedItem = item;

                return;
            }
        }

        SubagentList.SelectedIndex = 0;
    }

    private void UpdateSubBadge(List<SubJob> jobs)
    {
        var running = jobs.Count(j => IsRunning(j.Status));

        SubagentBadge.Text = running > 0 ? running.ToString() : "";
        SubagentBadge.Foreground = running > 0 ? ResBrush("IconBlue") : ResBrush("FgMuted");

        SubagentHint.Text = jobs.Count == 0 ? "" : $"{running} 在跑 / {jobs.Count} 个";
    }

    private static SubJob? TryReadJob(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string Str(string name)
                => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? (v.GetString() ?? string.Empty)
                    : string.Empty;

            int Int(string name)
                => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32()
                    : 0;

            long Tokens()
            {
                if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                    return 0;

                return usage.TryGetProperty("total_tokens", out var t) && t.ValueKind == JsonValueKind.Number
                    ? t.GetInt64()
                    : 0;
            }

            return new SubJob
            {
                Id = Str("job_id"),
                Label = Str("label"),
                Status = Str("status"),
                Step = Int("step"),
                MaxSteps = Int("max_steps"),
                Pid = Int("pid"),
                Tokens = Tokens(),
                Updated = Str("updated_at"),
                AnswerFile = Str("answer_file"),
                TalkFile = Str("talk_file"),
            };
        }
        catch (Exception)
        {
            return null;      // 单个文件坏掉不该拖垮整个列表
        }
    }

    // ------------------------------------------------------------------
    // 选中某一行
    // ------------------------------------------------------------------

    private void OnSubagentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SubagentList.SelectedItem is not ListBoxItem item)
            return;

        var id = (string?)item.Tag ?? "";

        if (string.IsNullOrEmpty(id))
        {
            // 选「主循环」＝回到聊天，但不关左侧列表
            SubagentViewer.Visibility = Visibility.Collapsed;
            _selectedJob = "";
            MaybeStopSubTimer();
            return;
        }

        _selectedJob = id;
        SubagentViewer.Visibility = Visibility.Visible;

        EnsureSubTimer();
        RenderJob(id, force: true);
    }

    private void OnSubBackClick(object sender, RoutedEventArgs e)
    {
        SubagentViewer.Visibility = Visibility.Collapsed;
        _selectedJob = "";

        // 列表还在，就继续每秒刷新；不在就停
        MaybeStopSubTimer();
    }

    // ------------------------------------------------------------------
    // 渲染一个 job 的全过程
    // ------------------------------------------------------------------

    private void RenderJob(string id, bool force)
    {
        var job = _subJobs.FirstOrDefault(j => j.Id == id);

        if (job is null)
        {
            SubTitle.Text = id;
            SubMeta.Text = "（状态文件已不存在）";
            SubTalkBox.Text = "";
            SubTraceBox.Text = "";
            return;
        }

        SubTitle.Text = string.IsNullOrWhiteSpace(job.Label) ? job.Id : job.Label;
        SubMeta.Text = $"{job.Status} · {job.Step}/{job.MaxSteps} 步 · {job.Tokens:N0} token · {job.Updated}";

        var path = Path.Combine(_guard!.Root, job.TalkFile.Replace('/', Path.DirectorySeparatorChar));

        long size = 0;

        try
        {
            if (File.Exists(path))
                size = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            // 正在写：这一轮先不看
        }

        var stamp = id + ":" + size;

        if (!force && stamp == _talkStamp)
            return;

        _talkStamp = stamp;

        var talk = new StringBuilder();
        var trace = new StringBuilder();

        if (!File.Exists(path))
        {
            talk.AppendLine("（还没有全过程记录：子代理刚开始跑，或这一版 worker 还没写 talk.jsonl）");
        }
        else
        {
            AppendTalk(talk, trace, path);
        }

        SubTalkBox.Text = talk.ToString();

        // ★ 2026-11-13：过程页不再整段赋给 TextBox ——
        //   控制台就绪时按增量写进去（只改字符格子，不重排）；
        //   没就绪或已关闭时退回原来的 TextBox 全文显示。
        RenderTraceText(trace.ToString());

        FollowSubTalk();
        FollowSubTrace();
    }

    private static void AppendTalk(StringBuilder talk, StringBuilder trace, string path)
    {
        var shown = 0;

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (reader.ReadLine() is { } line)
            {
                shown++;

                if (shown > 4000)
                {
                    talk.AppendLine("…（事件太多，只显示前 4000 条）");
                    break;
                }

                JsonDocument doc;

                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;     // 半行（正在写）：跳过
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    var kind = root.TryGetProperty("kind", out var k) ? (k.GetString() ?? "") : "";
                    var step = root.TryGetProperty("step", out var s) && s.ValueKind == JsonValueKind.Number
                        ? s.GetInt32()
                        : 0;

                    string Get(string name)
                        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                            ? (v.GetString() ?? "")
                            : "";

                    switch (kind)
                    {
                        case "inject":
                            talk.AppendLine("【你】（主控插话）" + Get("text"));
                            trace.AppendLine($"[第 {step} 步] 主控插话：{Get("text")}");
                            break;

                        case "assistant":
                        {
                            var content = Get("content");
                            var reasoning = Get("reasoning");

                            talk.AppendLine(content.Length == 0 ? "（这一步没有文字回复，只有工具调用）" : content);

                            trace.AppendLine($"──────── 第 {step} 步 ────────");
                            trace.AppendLine(reasoning.Length == 0
                                ? "思考：（该模型未返回思考内容）"
                                : "思考：" + reasoning);
                            break;
                        }

                        case "tool":
                        {
                            var name = Get("name");
                            var args = Get("args");
                            var result = Get("result");

                            talk.AppendLine($"  ⚙ {name}  {Clip(args, 160)}");
                            trace.AppendLine($"工具：{name}");
                            trace.AppendLine("参数：" + Clip(args, 600));
                            trace.AppendLine("结果：" + Clip(result, 800));
                            break;
                        }

                        case "final":
                        {
                            var answer = Get("answer");
                            var status = Get("status");
                            var error = Get("error");

                            talk.AppendLine();
                            talk.AppendLine($"── 收尾（{status}）──");
                            talk.AppendLine(answer.Length == 0 ? "（没有结论）" : answer);
                            trace.AppendLine();
                            trace.AppendLine($"══ 收尾：{status} ══");
                            if (error.Length > 0)
                                trace.AppendLine("错误：" + error);
                            break;
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
            talk.AppendLine("（读不到 talk.jsonl：文件正被占用，下一秒会重试）");
        }
    }

    private static string Clip(string text, int limit)
    {
        text = text.Replace("\r", "").Replace("\n", " ").Trim();
        return text.Length <= limit ? text : text[..limit] + "…";
    }

    // ------------------------------------------------------------------
    // 三个动作：插话 / 叫停 / 打开产出
    // ------------------------------------------------------------------

    private void OnSubSayClick(object sender, RoutedEventArgs e)
    {
        if (_selectedJob is not { Length: > 0 })
        {
            SetSubStatus("先在上面选中一个子代理");
            return;
        }

        var text = SubSayBox.Text.Trim();

        if (text.Length == 0)
        {
            SetSubStatus("插话是空的");
            return;
        }

        try
        {
            var path = Path.Combine(SubFolder, _selectedJob + ".inbox.jsonl");
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                text,
            });

            File.AppendAllText(path, line + "\n", Utf8NoBom);

            SubSayBox.Text = "";
            SetSubStatus("已插话：它下一步开头会看到（当前这一步不打断）");
        }
        catch (Exception ex)
        {
            SetSubStatus("插话失败：" + ex.Message);
        }
    }

    private void OnSubStopClick(object sender, RoutedEventArgs e)
    {
        if (_selectedJob is not { Length: > 0 })
        {
            SetSubStatus("先选中一个子代理");
            return;
        }

        var job = _subJobs.FirstOrDefault(j => j.Id == _selectedJob);

        if (job is null || !IsRunning(job.Status))
        {
            SetSubStatus("它已经结束了，不用叫停");
            return;
        }

        try
        {
            // 软停：只写标记，worker 下一步开头收尾（能留下半成品）
            File.WriteAllText(
                Path.Combine(SubFolder, _selectedJob + ".stop"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Utf8NoBom);

            var hard = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (hard && job.Pid > 0)
            {
                try
                {
                    using var proc = Process.GetProcessById(job.Pid);
                    proc.Kill(entireProcessTree: true);
                    SetSubStatus("已强制结束（进程树已杀，半成品可能没落盘）");
                    return;
                }
                catch (Exception ex)
                {
                    SetSubStatus("强制结束失败（" + ex.Message + "），已下软停");
                    return;
                }
            }

            SetSubStatus("已叫停：它做完当前这一步就收尾（最多约 90 秒）");
        }
        catch (Exception ex)
        {
            SetSubStatus("叫停失败：" + ex.Message);
        }
    }

    private void OnSubOpenOutput(object sender, RoutedEventArgs e)
    {
        if (_selectedJob is not { Length: > 0 })
        {
            SetSubStatus("先选中一个子代理");
            return;
        }

        var job = _subJobs.FirstOrDefault(j => j.Id == _selectedJob);
        var root = _guard?.Root;

        if (job is null || string.IsNullOrWhiteSpace(root))
            return;

        var rel = string.IsNullOrWhiteSpace(job.AnswerFile)
            ? Path.Combine("workspace", "subagent")
            : job.AnswerFile;

        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            if (File.Exists(full))
                Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(
                    Directory.Exists(Path.GetDirectoryName(full)!) ? Path.GetDirectoryName(full)! : root)
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetSubStatus("打不开：" + ex.Message);
        }
    }

    private void SetSubStatus(string text)
    {
        SubMeta.Text = text;
        StatusText = text;
    }

    // ------------------------------------------------------------------
    // 「过程」页的真控制台（2026-11-13）
    //
    // 移植自 Python 版 workspace/conhost_log/conlog.py：
    //   起宿主（FatFish.exe 的 loghost 模式）→ 找到它的 conhost 窗口
    //   → SetParent 进面板 → stdin 喂日志。
    //
    // 为什么值得：TextBox 末尾追加一个字也可能要重排整条消息；conhost 是
    // 固定字符网格，写入只改格子、滚动只挪视口。子代理跑几十步也不会变卡。
    // ------------------------------------------------------------------

    /// <summary>
    /// 把宿主拉起来（后台线程，不阻塞 UI）。
    ///
    /// <para>
    /// ★ 必须可重建（2026-11-13 修）：原来开头是
    /// <c>if (_traceConsole is not null || …) return;</c> —— 宿主一旦死掉，
    /// 这个字段永远非空，于是「过程」页既不会再起宿主，也永远拿不回控制台，
    /// 点「弹出」只会得到一句「还没起来（或已经退出）」，而且刷新面板也救不回来。
    /// </para>
    /// </summary>
    private void EnsureTraceConsole()
    {
        if (_traceConsoleStarting)
            return;

        if (_traceConsole is { IsAlive: true })
            return;

        if (_traceConsole is not null)
            ResetTraceConsole();

        _traceConsoleStarting = true;

        var title = "FATFISH_TRACE_" + Environment.ProcessId + "_"
                    + Guid.NewGuid().ToString("N")[..8];

        // 宿主按偏移 tail 这个文件；路径放在工作台的 logs/live 下，事后能复查。
        var logFile = Path.Combine(
            _guard?.Root ?? Environment.CurrentDirectory,
            "logs", "live", "trace_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

        var console = new ConsoleLog();
        _traceConsole = console;

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            bool ok;

            try
            {
                ok = console.Start(title, logFile);
            }
            catch (Exception ex)
            {
                ok = false;
                _traceStartError = ex.Message;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _traceConsoleStarting = false;

                if (!ok)
                {
                    // 降级：退回原来的只读 TextBox，行为与改动前完全一致
                    SubTraceBox.Visibility = Visibility.Visible;

                    var why = _traceStartError ?? console.LastFailure ?? "宿主被拦截 / 系统没有 conhost";

                    Log("过程页：真控制台起不来，已退回文本框（" + why + "）");
                    TraceDiag("start failed: " + why);
                    return;
                }

                BuildTraceStage();
                Log("过程页：真控制台已就绪（" + console.LogFile + "）");
                TraceDiag("start ok pid=" + console.ProcessId
                          + " hwnd=0x" + console.WindowHandle.ToString("X")
                          + " file=" + console.LogFile);
            }));
        });
    }

    /// <summary>
    /// 彻底收掉当前这一套（舞台 + 宿主）。宿主已死时用 —— 必须先把舞台从
    /// TraceHost 里摘掉，让它走一次 DestroyWindowCore 把窗口收回去，
    /// 再 Dispose 杀掉宿主进程，否则会留下一个看不见的宿主进程。
    /// </summary>
    private void ResetTraceConsole()
    {
        try
        {
            if (_traceStage is not null)
            {
                TraceHost.Children.Remove(_traceStage);
                _traceStage = null;
            }
        }
        catch
        {
            // 摘不掉也不阻止后面收尾
        }

        try
        {
            _traceConsole?.Dispose();
        }
        catch
        {
            // 退出路径上不抛
        }

        _traceConsole = null;
        _tracePopped = false;
        _traceSent = "";
        TracePopButton.Content = "弹出";
        SubTraceBox.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 过程控制台的诊断落盘。窗口底部「日志」页签只进内存（Log()），
    /// 宿主出问题时根本查不到现场，所以另写一份到 logs/live/trace_console.log。
    /// </summary>
    private void TraceDiag(string text)
    {
        try
        {
            var root = _guard?.Root ?? Environment.CurrentDirectory;
            var dir = Path.Combine(root, "logs", "live");

            Directory.CreateDirectory(dir);

            File.AppendAllText(
                Path.Combine(dir, "trace_console.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text + Environment.NewLine,
                Utf8NoBom);
        }
        catch
        {
            // 诊断本身绝不能影响主流程
        }
    }

    /// <summary>把 ConsoleStage 放进「过程」页的 Grid，并挂上生命周期回调。</summary>
    private void BuildTraceStage()
    {
        if (_traceConsole is null || _traceStage is not null)
            return;

        // 先声明再挂回调：对象初始化器里不能引用「正在声明的那个变量」，
        // 而 Built 回调又需要拿 stage 的句柄，所以拆成两句。
        var stage = new ConsoleStage();

        // 面板/页签销毁之前：必须先把 conhost 窗口收回去，
        // 否则父窗口一销毁会把控制台窗口一起带走（Python 版留的教训）。
        stage.BeforeDestroy = () =>
        {
            if (!_tracePopped)
                _traceConsole?.Detach();
        };

        stage.Sized = (w, h) =>
        {
            if (!_tracePopped)
                _traceConsole?.Fit(w, h);
        };

        stage.Built = () =>
        {
            if (_tracePopped)
                return;

            var handle = stage.StageHandle;

            if (handle == IntPtr.Zero)
                return;

            var attached = _traceConsole?.Attach(
                handle, (int)stage.ActualWidth, (int)stage.ActualHeight) ?? false;

            TraceDiag("attach " + (attached ? "ok" : "failed")
                      + " hwnd=0x" + (_traceConsole?.WindowHandle ?? IntPtr.Zero).ToString("X")
                      + " parent=0x" + handle.ToString("X")
                      + " size=" + (int)stage.ActualWidth + "x" + (int)stage.ActualHeight);

            // 挂上之后把当前 job 的内容整段落一次（此前都写在降级文本框里）
            if (_selectedJob is { Length: > 0 } id)
                RenderJob(id, force: true);
        };

        _traceStage = stage;
        TraceHost.Children.Insert(0, stage);
        SubTraceBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>写过程正文：控制台活着就增量写，否则退回 TextBox。</summary>
    private void RenderTraceText(string full)
    {
        if (_traceConsole is { IsAlive: true })
        {
            var sent = _traceSent;

            if (full.Length >= sent.Length && full.StartsWith(sent, StringComparison.Ordinal))
            {
                var delta = full[sent.Length..];

                if (delta.Length > 0)
                    _traceConsole.Write(delta);
            }
            else
            {
                // 换了 job 或者文件被重写：清屏重画（ANSI 清屏，宿主侧已开 VT）
                _traceConsole.Clear();
                _traceConsole.Write(full);
            }

            _traceSent = full;
            return;
        }

        // 降级路径：跟改动前一样整段赋值
        SubTraceBox.Visibility = Visibility.Visible;

        // ★ HwndHost 永远画在 WPF 内容之上：宿主死了但舞台还留在树里时，
        //   过程页会是一片空白（文本框被原生窗口盖住），用户只会觉得「没内容了」。
        //   摘掉舞台会让它走一次 DestroyWindowCore，对死宿主没有副作用。
        if (_traceStage is not null)
        {
            try
            {
                TraceHost.Children.Remove(_traceStage);
                _traceStage = null;
            }
            catch
            {
                // 摘不掉就只能让它盖着
            }
        }

        SubTraceBox.Text = full;
    }

    /// <summary>
    /// 「弹出 / 收回」。
    /// 弹出后控制台是独立的顶层窗口 —— 主窗口卡死（UI 线程被占住）时，
    /// 它由 conhost 自己渲染，照样滚、照样能翻历史。
    /// </summary>
    private void OnTracePopClick(object sender, RoutedEventArgs e)
    {
        if (_traceConsole is not { IsAlive: true })
        {
            // ★ 不能只报一句就 return：那样这个按钮就永久废了 ——
            //   EnsureTraceConsole 以前看到字段非空就直接返回，宿主死了也不会重建。
            //   现在收掉残骸、重新起一套，并让用户再点一次。
            var why = _traceConsole?.LastFailure ?? "宿主已退出";

            TraceDiag("popout aborted, host dead: " + why);
            SetSubStatus("控制台已退出，正在重启…起来后再点一次「弹出」");

            ResetTraceConsole();
            EnsureTraceConsole();
            return;
        }

        if (_tracePopped)
        {
            _tracePopped = false;
            TracePopButton.Content = "弹出";

            if (_traceStage is not null)
            {
                _traceConsole.Attach(
                    _traceStage.StageHandle,
                    (int)_traceStage.ActualWidth,
                    (int)_traceStage.ActualHeight);
            }

            SetSubStatus("已收回面板");
            return;
        }

        var ok = _traceConsole.PopOut(
            (int)(Left + ActualWidth * 0.15),
            (int)(Top + 80),
            960,
            620);

        if (!ok)
        {
            SetSubStatus("弹出失败：控制台窗口已经不在了");
            return;
        }

        _tracePopped = true;
        TracePopButton.Content = "收回";
        SetSubStatus("已弹出：主窗口卡住它也不受影响，点「收回」放回面板");
    }

    /// <summary>窗口关闭时收尾（把宿主进程一起结束掉）。</summary>
    private void ShutdownTraceConsole()
    {
        try
        {
            _traceConsole?.Dispose();
        }
        catch
        {
            // 退出路径上不抛
        }

        _traceConsole = null;
        _traceStage = null;
        _tracePopped = false;
        _traceSent = "";
    }

    private Brush ResBrush(string key)
        => TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>写 jsonl 一律不要 BOM —— 追加式文件里混进 BOM 会变成一条坏行。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
}
