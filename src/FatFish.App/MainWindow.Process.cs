using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FatFish.App.ViewModels;

namespace FatFish.App;

/// <summary>
/// 「过程」面板 —— 把模型的思考链与工具调用实时摊开在右侧独立一栏。
///
/// <para>
/// <b>为什么要单独开一栏，而不是塞进对话气泡：</b>
/// 思考链比正文长得多（实测一个复杂回合能写几千字），混在气泡里会把真正的回答挤到屏幕外。
/// 分成独立一栏之后：左边专心看对话，右边专心看它「在想什么、调用了什么」，互不打扰。
/// </para>
///
/// <para>
/// <b>行为对齐 Python 版 <c>workspace/stream_core.py</c> 的 <c>feed_think()</c>：</b>
/// </para>
/// <list type="number">
///   <item>第一次收到思考增量时，先空一行，再打一行表头 <c>💭 思考中…</c>；</item>
///   <item>正文<b>连续吐字</b>，缩进只在行首补一次 ——
///         绝不能「一个增量一行」。推理链的传输粒度是一个 token
///         （中文 1 字、英文 2~3 字母），按增量断行会得到锯齿状碎片。
///         这是 Python 版 2026-10-02 修过的 bug，这里直接继承正确做法：
///         <b>只有源文本里真的出现 \n 才断行</b>；</item>
///   <item>思考块结束时补一个换行收口（停在半行时），再允许工具行插入。</item>
/// </list>
///
/// <para>
/// <b>性能：</b>思考增量是逐 token 到达的，直接改 TextBlock.Text 会每个 token 触发一次
/// 全量布局。这里用 StringBuilder 累积 + 80ms 定时器批量刷新，把布局次数压到约 1/12。
/// </para>
/// </summary>
public partial class MainWindow
{
    private const double ProcessPaneDefaultWidth = 320;
    private const int ProcessBufferLimit = 200_000;   // 字符数上限，超了从头部截断
    private const int ProcessBufferKeep = 120_000;
    private const int ProcessFlushMs = 80;

    private ColumnDefinition? _processSplitterColumn;
    private ColumnDefinition? _processColumn;
    private Border? _processPane;
    private TextBlock? _processText;
    private ScrollViewer? _processScroll;
    private Button? _processToggleButton;
    private DispatcherTimer? _processTimer;

    private readonly StringBuilder _processBuffer = new();
    private bool _processDirty;
    private bool _processVisible = true;
    private double _processLastWidth = ProcessPaneDefaultWidth;

    private bool _processThinkOpen;      // 思考块是否开着
    private bool _processAtLineStart = true;
    private long _processThinkChars;
    private int _processToolCount;

    // ---------------------------------------------------------------- 安装

    /// <summary>
    /// 在主区右侧插一栏「过程」。整块界面用代码建，不改 MainWindow.xaml。
    ///
    /// 插入点：<c>SidePanel.Parent</c> —— 就是那个三列 Grid（左栏 220 / 分隔条 5 / 内容 *）。
    /// 往后追加两列：列 3 = 分隔条，列 4 = 过程面板（固定宽）。
    /// 因为内容列是 <c>*</c>，面板占固定宽度，窗口缩放时缩的是中间那块，符合直觉。
    /// </summary>
    private void InstallProcessPane()
    {
        if (_processPane is not null)
            return;

        try
        {
            if (SidePanel?.Parent is not Grid host)
            {
                Log("过程面板：未找到主区 Grid，跳过");
                return;
            }

            // ★ 这里原先缓存了 5 个 Brush 局部变量。换肤之后那套写法会留旧颜色 ——
            //   资源值被换掉，但缓存下来的实例还是旧的。所以统一改成
            //   `元素.SetResourceReference(属性, 键)`：颜色跟着资源走，切主题自动刷新。

            // ---- 列定义 ----
            _processSplitterColumn = new ColumnDefinition { Width = new GridLength(5) };
            _processColumn = new ColumnDefinition
            {
                Width = new GridLength(ProcessPaneDefaultWidth),
                MinWidth = 0,
            };
            host.ColumnDefinitions.Add(_processSplitterColumn);
            host.ColumnDefinitions.Add(_processColumn);

            // ---- 分隔条 ----
            var splitter = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ResizeDirection = GridResizeDirection.Columns,
            };
            if (TryFindResource("CeSplitter") is Style splitterStyle)
                splitter.Style = splitterStyle;

            splitter.SetResourceReference(Control.BackgroundProperty, "LineSoft");
            splitter.SetValue(Grid.ColumnProperty, 3);
            host.Children.Add(splitter);

            // ---- 面板本体 ----
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 6, 6, 6) };

            var title = new TextBlock
            {
                Text = "过程",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

            var clearButton = new Button
            {
                Content = "清空",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "清空过程面板（只影响这一栏的显示，不影响对话）",
            };
            if (TryFindResource("ToolButton") is Style toolStyle)
                clearButton.Style = toolStyle;
            clearButton.Click += (_, _) => ClearProcessPane();
            DockPanel.SetDock(clearButton, Dock.Right);

            _processText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 17,
                Margin = new Thickness(10, 0, 10, 10),
            };

            // 灰色：与 Python 版思考链同色，醒目但不抢正文
            _processText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");

            _processScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _processText,
            };

            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_processScroll, 1);
            body.Children.Add(header);
            body.Children.Add(_processScroll);

            header.Children.Add(clearButton);
            header.Children.Add(title);

            _processPane = new Border
            {
                BorderThickness = new Thickness(1, 0, 0, 0),
                Child = body,
            };
            _processPane.SetValue(Grid.ColumnProperty, 4);
            _processPane.SetResourceReference(Border.BackgroundProperty, "BgPanel");
            _processPane.SetResourceReference(Border.BorderBrushProperty, "Line");
            host.Children.Add(_processPane);

            // ---- 工具调用：镜像到底部「工具」页的数据源 ----
            _toolLog.CollectionChanged += OnProcessToolLogChanged;

            // ---- 批量刷新定时器 ----
            _processTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(ProcessFlushMs),
            };
            _processTimer.Tick += (_, _) => FlushProcessPane();
            _processTimer.Start();

            InstallProcessToggleButton();

            AppendProcessLineOnUi("过程面板就绪：思考链与工具调用会实时出现在这里。");
            Log("过程面板已就绪（右侧「过程」栏）");
        }
        catch (Exception ex)
        {
            Log("过程面板安装失败：" + ex.GetType().Name + " - " + ex.Message);
        }
    }

    /// <summary>顶栏加一个「过程」按钮，控制这一栏显示 / 收起。</summary>
    private void InstallProcessToggleButton()
    {
        if (_processToggleButton is not null)
            return;
        if (SidebarButton?.Parent is not Panel toolbar)
            return;

        _processToggleButton = new Button
        {
            Content = new TextBlock
            {
                // 用汉字而不是 Segoe MDL2 私有区字形：实测该码位会被渲染成「?」，
                // 文字标签在任何字体回落下都不会坏，也比抽象图标好认。
                Text = "过程",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
            },
            ToolTip = "过程面板\n显示模型的思考链与工具调用（灰色连续吐字）。\n点击切换显示 / 收起。",
        };

        // 图标色走资源引用：换肤时这个「过程」按钮也跟着变
        if (_processToggleButton.Content is TextBlock iconBlock)
            iconBlock.SetResourceReference(TextBlock.ForegroundProperty, "IconViolet");

        if (TryFindResource("ToolButton") is Style style)
            _processToggleButton.Style = style;

        _processToggleButton.Click += (_, _) => ToggleProcessPane();

        int index = toolbar.Children.IndexOf(SidebarButton);
        toolbar.Children.Insert(index < 0 ? 0 : index, _processToggleButton);
    }

    // ---------------------------------------------------------------- 显示 / 收起

    private void ToggleProcessPane()
    {
        if (_processColumn is null || _processSplitterColumn is null)
            return;

        if (_processVisible)
        {
            _processLastWidth = _processColumn.ActualWidth > 40
                ? _processColumn.ActualWidth
                : ProcessPaneDefaultWidth;
            _processColumn.Width = new GridLength(0);
            _processSplitterColumn.Width = new GridLength(0);
            _processVisible = false;
            Log("过程面板：已收起");
        }
        else
        {
            _processColumn.Width = new GridLength(_processLastWidth);
            _processSplitterColumn.Width = new GridLength(5);
            _processVisible = true;
            FlushProcessPane();
            Log("过程面板：已展开");
        }
    }

    // ---------------------------------------------------------------- 对外接口

    /// <summary>
    /// 思考链增量入口。<b>由后台线程调用</b>（IAgentObserver.OnReasoningDelta）。
    ///
    /// 与 Python 版的差异：那边直接往控制台写，这边抛回 UI 线程再拼。
    /// 逐 token 投递 BeginInvoke 是可接受的 —— 真正贵的布局已被 80ms 定时器合并。
    /// </summary>
    internal void AppendReasoning(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        PostProcessToUi(() => AppendReasoningOnUi(text));
    }

    // ---------------------------------------------------------------- 内部实现

    private void AppendReasoningOnUi(string text)
    {
        if (_processText is null)
            return;

        // 表头只在「进入思考」时打一次
        if (!_processThinkOpen)
        {
            _processThinkOpen = true;
            _processAtLineStart = true;
            _processBuffer.Append(Environment.NewLine);
            _processBuffer.Append("  \uD83D\uDCAD 思考中…").Append(Environment.NewLine);
        }

        _processThinkChars += text.Length;

        // ★ 只有源文本真的含 \n 才断行；缩进只在行首补一次。
        //   绝不能「一个增量一行」—— 推理链是一个 token 一个增量过来的。
        string normalized = text.Replace("\r", "");
        string[] segments = normalized.Split('\n');

        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                _processBuffer.Append('\n');
                _processAtLineStart = true;
            }

            string segment = segments[i];
            if (segment.Length == 0)
                continue;

            if (_processAtLineStart)
            {
                _processBuffer.Append("  ");
                _processAtLineStart = false;
            }

            _processBuffer.Append(segment);
        }

        TrimProcessBuffer();
        _processDirty = true;
    }

    /// <summary>收口思考块：停在半行就补一个换行。</summary>
    private void CloseThinkOnUi()
    {
        if (!_processThinkOpen)
            return;

        if (!_processAtLineStart)
            _processBuffer.Append(Environment.NewLine);

        _processThinkOpen = false;
        _processAtLineStart = true;
        _processDirty = true;
    }

    private void AppendProcessLineOnUi(string line)
    {
        CloseThinkOnUi();
        _processBuffer.Append(line).Append(Environment.NewLine);
        _processAtLineStart = true;
        TrimProcessBuffer();
        _processDirty = true;
    }

    private void TrimProcessBuffer()
    {
        if (_processBuffer.Length <= ProcessBufferLimit)
            return;

        string keep = _processBuffer.ToString(_processBuffer.Length - ProcessBufferKeep, ProcessBufferKeep);
        _processBuffer.Clear();
        _processBuffer.Append("…（较早内容已截断，只保留最近 ")
                     .Append(ProcessBufferKeep)
                     .Append(" 字）")
                     .Append(Environment.NewLine)
                     .Append(keep);
    }

    private void FlushProcessPane()
    {
        if (!_processDirty || _processText is null)
            return;

        _processDirty = false;
        _processText.Text = _processBuffer.ToString();
        _processScroll?.ScrollToEnd();
    }

    private void ClearProcessPane()
    {
        _processBuffer.Clear();
        _processThinkOpen = false;
        _processAtLineStart = true;
        _processThinkChars = 0;
        _processToolCount = 0;

        if (_processText is not null)
            _processText.Text = "";

        _processDirty = false;
        Log("过程面板：已清空");
    }

    /// <summary>工具「开始」镜像：新条目进集合时写一行。</summary>
    private void OnProcessToolLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
            return;

        foreach (object obj in e.NewItems)
        {
            if (obj is not ToolLogItem item)
                continue;

            _processToolCount++;
            AppendProcessLineOnUi("\uD83D\uDD27 " + item.ToolName + "  " + CompactArguments(item.Arguments));

            // 结束时刻靠属性通知：State 从「运行中」变为「成功 / 失败」
            item.PropertyChanged += OnProcessToolItemChanged;
        }
    }

    private void OnProcessToolItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolLogItem.State))
            return;
        if (sender is not ToolLogItem item || item.State == "运行中")
            return;

        string mark = item.Ok ? "\u2714" : "\u2716";
        string detail = string.IsNullOrWhiteSpace(item.Detail) ? item.State : item.State + " · " + item.Detail;

        PostProcessToUi(() => AppendProcessLineOnUi("   " + mark + " " + item.ToolName + "  " + detail));
    }

    private static string CompactArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "";

        string s = arguments.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 110 ? s : string.Concat(s.AsSpan(0, 110), "…");
    }

    private void PostProcessToUi(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}
