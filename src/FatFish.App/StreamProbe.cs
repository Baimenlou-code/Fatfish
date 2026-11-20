using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FatFish.App;

/// <summary>
/// 流式渲染探针（只在 <c>FATFISH_STREAM_PROBE=1</c> 时启用，平时完全不参与）。
///
/// 存在的理由：以前判断「流畅与否」靠的是 UIA 每隔 250ms 读一次控件文本 ——
/// 那个采样器自己就要 300ms，而且会占住 UI 线程，量出来的东西既粗又不准，
/// 还可能是它自己造成的卡顿。这里改成**从进程内部量**：
///
///   · <c>arrive</c>   分片到达 UI 线程（HandleUiEvent 入口）的时刻；
///   · <c>append</c>   FlushStreamDelta 里真正写控件那段的耗时；
///   · <c>latency</c>  从「这批分片开始到达」到「一次布局完成」的间隔 ——
///                     也就是「字已经到了，屏幕上要等多久才看得到」。
///
/// 三个数各自能指认不同的病：
///   append 大  → 每次写控件都很贵（整段重排），要换写法；
///   latency 大 → 中间有人在排队（节拍器/低优先级泵）；
///   两者都不大却仍觉得卡 → 那是网络/服务端在攒包，不是界面的问题。
/// </summary>
internal static class StreamProbe
{
    private static bool _on;
    private static MainWindow? _win;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<Row> _rows = new();

    private static long _batchStartTicks = -1;
    private static int _pendingRow = -1;
    private static int _totalChars;
    private static string _outDir = string.Empty;

    private sealed class Row
    {
        public double ArriveMs;
        public double AppendMs;
        public double LatencyMs = -1;
        public int Chars;
    }

    public static bool On => _on;

    public static void Attach(MainWindow win)
    {
        _on = Environment.GetEnvironmentVariable("FATFISH_STREAM_PROBE") == "1";
        if (!_on)
            return;

        _win = win;

        _outDir = Environment.GetEnvironmentVariable("FATFISH_PROBE_DIR") ?? string.Empty;
        if (_outDir.Length == 0)
            _outDir = Path.Combine(Environment.GetEnvironmentVariable("FATFISH_WORKSPACE") ?? ".", "logs", "probe");

        // 预热历史消息：模拟「已经聊了很久」的状态。
        // 这是量「单次追加成本会不会随对话变长而变慢」的唯一办法。
        var seedRaw = Environment.GetEnvironmentVariable("FATFISH_PROBE_SEED");
        if (int.TryParse(seedRaw, out var seed) && seed > 0)
            win.SeedProbeMessages(seed);

        win.Loaded += (_, _) =>
        {
            // 用 CompositionTarget.Rendering 而不是 LayoutUpdated：
            // 后者在窗口不重排时不触发（实测 150 次 flush 只报了 2 次），
            // 前者是"这一帧要画了"的信号，才是用户真正看到字的时刻。
            CompositionTarget.Rendering += (_, _) => OnFrame();

            // 跟随测试：3 秒后把视图拉回顶部，模拟「用户在流式中途往上翻」。
            // 期望：此后不再被强制拉到底（报告里 atBottom=False），而流照常继续。
            if (Environment.GetEnvironmentVariable("FATFISH_PROBE_SCROLLTEST") == "1")
            {
                var scrollTest = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                scrollTest.Tick += (_, _) =>
                {
                    scrollTest.Stop();
                    var sv = FindChild<ScrollViewer>(win.MessageList);
                    if (sv is not null)
                    {
                        sv.ScrollToVerticalOffset(0);
                        Diag("scrolltest: pulled to top");
                    }
                };
                scrollTest.Start();
            }

            var auto = Environment.GetEnvironmentVariable("FATFISH_PROBE_AUTOSEND");
            if (!string.IsNullOrEmpty(auto))
            {
                win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        win.InputBox.Text = auto;
                        win.StartProbeSend();
                        Diag("autosend ok | " + win.ProbeState());
                    }
                    catch (Exception ex)
                    {
                        Diag("autosend FAIL " + ex);
                    }
                }), DispatcherPriority.Background);

                // 探针自诊断：8 秒后无论成功失败，都把界面状态落一份下来。
                // 上一次「一条报告都没有」时就是靠这个找到原因的 ——
                // 报告只在 StreamEnd 写，而流根本没起来，于是什么都没留下。
                var diagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                diagTimer.Tick += (_, _) =>
                {
                    diagTimer.Stop();
                    Diag("t+8s | " + win.ProbeState());
                    Report("diag-8s");
                };
                diagTimer.Start();
            }
        };
    }

    private static void Diag(string msg)
    {
        try
        {
            Directory.CreateDirectory(_outDir);
            File.AppendAllText(Path.Combine(_outDir, "diag.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}\n{new string('-', 40)}\n");
        }
        catch
        {
            // 诊断失败也不许影响主流程
        }
    }

    /// <summary>分片到达 UI 线程。</summary>
    public static void NoteDelta(int chars)
    {
        if (!_on)
            return;

        if (_batchStartTicks < 0)
            _batchStartTicks = Clock.ElapsedTicks;

        _totalChars += chars;
    }

    /// <summary>一次 flush 写控件的实际耗时。</summary>
    public static void NoteFlush(int chars, double appendMs)
    {
        if (!_on)
            return;

        _rows.Add(new Row
        {
            ArriveMs = Ms(_batchStartTicks < 0 ? Clock.ElapsedTicks : _batchStartTicks),
            AppendMs = appendMs,
            Chars = chars,
        });

        _pendingRow = _rows.Count - 1;
    }

    private static void OnFrame()
    {
        // 一帧到了：把还没配上帧的最后一次 flush 补上延迟，然后复位
        if (!_on || _pendingRow < 0 || _batchStartTicks < 0)
            return;

        var row = _rows[_pendingRow];

        if (row.LatencyMs < 0)
            row.LatencyMs = Ms(Clock.ElapsedTicks) - Ms(_batchStartTicks);

        _batchStartTicks = -1;
        _pendingRow = -1;
    }

    public static void Report(string tag)
    {
        if (!_on || _win is null)
            return;

        try
        {
            Directory.CreateDirectory(_outDir);

            var items = _win.MessageList.Items.Count;
            var realized = "?";
            var vsp = FindChild<VirtualizingStackPanel>(_win.MessageList);
            if (vsp is not null)
                realized = vsp.Children.Count.ToString(CultureInfo.InvariantCulture);

            var sv = FindChild<ScrollViewer>(_win.MessageList);
            var canContentScroll = sv?.CanContentScroll.ToString() ?? "?";

            // 跟随到底的判据：流结束后视图应当停在底部（离底 < 4px）。
            // 没有这一行的话，「跟随失效」只能靠肉眼，而肉眼分不清是没跟随还是内容本来就不长。
            var atBottom = sv is null
                ? "?"
                : (sv.ExtentHeight - sv.VerticalOffset - sv.ViewportHeight < 4).ToString(CultureInfo.InvariantCulture)
                  + string.Format(CultureInfo.InvariantCulture, " offset={0:F0} extent={1:F0} viewport={2:F0}",
                      sv.VerticalOffset, sv.ExtentHeight, sv.ViewportHeight);


            // 同一个控件上一次 flush 之后 Text 的长度：用来确认「没丢字」
            var flushed = _rows.Sum(r => (long)r.Chars);

            var sb = new StringBuilder();
            sb.AppendLine("# stream probe");
            sb.AppendLine($"tag={tag} at={DateTime.Now:HH:mm:ss}");
            sb.AppendLine($"items={items} realized={realized} canContentScroll={canContentScroll} vsp={(vsp is not null)}");
            sb.AppendLine("scroll atBottom=" + atBottom);
            sb.AppendLine($"flushes={_rows.Count} chars_in={flushed} chars_total={_totalChars}");
            sb.AppendLine($"append_ms  {Stats(_rows.Select(r => r.AppendMs))}");
            sb.AppendLine($"latency_ms {Stats(_rows.Select(r => r.LatencyMs).Where(v => v >= 0))}");

            var path = Path.Combine(_outDir, $"stream-{DateTime.Now:HHmmss}.txt");
            File.WriteAllText(path, sb.ToString());

            var csv = new StringBuilder("arrive_ms,chars,append_ms,latency_ms\n");
            foreach (var r in _rows)
                csv.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:F2},{1},{2:F2},{3:F2}", r.ArriveMs, r.Chars, r.AppendMs, r.LatencyMs));
            File.WriteAllText(Path.ChangeExtension(path, ".csv"), csv.ToString());
        }
        catch
        {
            // 探针绝不能影响正常运行
        }
    }

    public static void Reset()
    {
        _rows.Clear();
        _batchStartTicks = -1;
        _pendingRow = -1;
        _totalChars = 0;
    }

    private static string Stats(IEnumerable<double> values)
    {
        var arr = values.OrderBy(v => v).ToArray();
        if (arr.Length == 0)
            return "(no data)";

        double P(double q) => arr[Math.Min(arr.Length - 1, (int)Math.Round(q * (arr.Length - 1)))];

        return string.Format(CultureInfo.InvariantCulture,
            "n={0} min={1:F1} med={2:F1} p90={3:F1} max={4:F1}",
            arr.Length, arr[0], P(0.5), P(0.9), arr[^1]);
    }

    private static double Ms(long ticks)
        => ticks * 1000.0 / Stopwatch.Frequency;

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
                return hit;

            var deeper = FindChild<T>(child);
            if (deeper is not null)
                return deeper;
        }

        return null;
    }
}

public partial class MainWindow
{
    /// <summary>探针专用：塞 N 条假历史消息，模拟「长对话」。</summary>
    internal void SeedProbeMessages(int n)
    {
        for (var i = 0; i < n; i++)
        {
            var body = $"历史消息 {i}：" + string.Concat(Enumerable.Repeat("这是一段用来撑高对话长度的占位文本。", 8));
            _messages.Add(new ViewModels.MessageItem(Guid.NewGuid().ToString("N"), body,
                i % 3 == 0 ? "user" : "ai"));
        }

        UpdateMessageCount();
    }

    /// <summary>探针专用：像用户按下「发送」一样发起一轮。</summary>
    internal void StartProbeSend() => OnSendClick(this, new RoutedEventArgs());

    /// <summary>探针专用：把「界面此刻到底什么状态」捞成一行，用来解释量不到的原因。</summary>
    internal string ProbeState()
    {
        var last = _messages.Count > 0 ? _messages[^1] : null;

        return string.Format(CultureInfo.InvariantCulture,
            "status={0} | running={1} agent={2} llm={3} msgs={4} lastLen={5}",
            StatusText, _isRunning, _agent is not null, _llm is not null,
            _messages.Count, last?.Text.Length ?? -1);
    }
}
