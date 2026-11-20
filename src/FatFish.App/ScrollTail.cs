using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FatFish.App;

/// <summary>
/// 「跟随最新」：把滚动框钉在底部，直到用户自己往上翻。
/// </summary>
/// <remarks>
/// ★ 为什么要有这个类（2026-11-06）：
///   消息区早有一套成熟的贴底逻辑（见 MainWindow.ScrollToBottom 的注释 ——
///   旧写法在滚动回调里现算「离底部多远」，被流式追加本身推出 48px，
///   于是把自己判成「用户上翻了」，跟随永久失效且不报错）。
///   而进程输出、子代理对话、子代理轨迹、底部日志/工具调用几个框是**无条件**滚动：
///   你想往回看一眼，任何一行新内容都会把你拽回底部。
///   这里把那套判断收成一份，免得再各写一遍、再各踩一次。
///
/// ★ 2026-11-06 实测修正（真机 UIA 读数得出的）：
///   ① **列表不能用 ScrollToEnd()**。虚拟化面板的 ExtentHeight 是「已测量到的」范围，
///      不是真实总数；ScrollToEnd 只滚到那个边界，于是每次追加都差一截、越差越远。
///      实测日志列表停在 33%，而且内容越长百分比越低。列表一律改走 ScrollIntoView。
///   ② **滚动容器会换实例**。页签切走再切回时模板可能重建，旧的 ScrollViewer
///      已从可视树摘下（IsLoaded=false），原先只挂一次，之后对着死容器发指令 ——
///      不报错、不生效。改成每次 Bind 重挂，Loaded / IsVisibleChanged 都补绑。
///   ③ **不能再靠「位置」猜意图**（2026-11-06 第二次实测）：
///      日志框一切正常，工具调用框「滚上去后新记录一到就跳回底部」。
///      位置推算法有两种失效方式：虚拟化重测时短暂的 extent/offset 为 0
///      会被算成「就在底部」，于是 _stick 又被翻回 true；或者容器压根没挂上，
///      位置回调一次都收不到。两种都会表现成「上翻后被拽回底部」。
///      现在改成**只认明确的意图信号**：
///        · 滚轮向上        → 离开底部（挂在元素本身，不依赖模板/容器）
///        · 向下滚到接近底部 → 回到跟随
///        · 内容长短变化     → 一律不改意图（RemoveAt(0) 会同时改 extent 和 offset）
///      宁可少跟随一次，也不能把用户按在底部。
/// </remarks>
internal sealed class ScrollTail
{
    /// <summary>离底部多近算「还在底部」。</summary>
    private const double StickThreshold = 48;

    /// <summary>诊断输出文件（只写状态翻转，正常用不到，排查「为什么没跟随」时看它）。</summary>
    private static readonly string DiagPath =
        Path.Combine(AppContext.BaseDirectory, "logs", "scrolltail.log");

    /// <summary>被跟随的元素（ScrollViewer、TextBox，或 ListBox 这类列表）。</summary>
    private readonly DependencyObject _owner;

    /// <summary>列表形态时用于 ScrollIntoView；非列表为 null。</summary>
    /// <remarks>
    /// 类型必须是 ListBox 而不是 ItemsControl：ScrollIntoView 定义在 ListBox 上，
    /// ItemsControl 没有这个方法。
    /// </remarks>
    private readonly ListBox? _list;

    /// <summary>诊断里用来区分是哪个框。</summary>
    private readonly string _tag;

    private ScrollViewer? _view;
    private bool _stick = true;
    private bool _queued;

    /// <summary>上一次写诊断用的去重键（见 Diag）。</summary>
    private string _lastDiagKey = string.Empty;

    private ScrollTail(DependencyObject owner)
    {
        _owner = owner;
        _list = owner as ListBox;
        _tag = owner is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name)
            ? fe.Name
            : owner.GetType().Name;

        if (owner is FrameworkElement element)
        {
            // 容器/模板可能晚于构造出现，两者都补绑。
            element.Loaded += (_, _) => Bind();
            element.IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true)
                    Bind();
            };

            // ★ 滚轮是「用户意图」最硬的一手：挂在元素本身上，
            //   不需要拿到滚动容器，也不怕页签没选中。容器那条路全断了它照样有效。
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }

        Bind();
    }

    /// <summary>当前是否还在跟随（用户手动上翻后为 false）。</summary>
    public bool IsStuck => _stick;

    /// <summary>是否已成功拿到滚动容器（没拿到时仍能跟列表，但检测不到拖动滚动条）。</summary>
    public bool HasView => _view is not null;

    /// <summary>挂到一个 ScrollViewer 上；为空则不挂。</summary>
    public static ScrollTail? Attach(ScrollViewer? view)
        => view is null ? null : new ScrollTail(view);

    /// <summary>挂到某个元素上：是列表就用 ScrollIntoView，是 TextBox 就取模板里的 ScrollViewer。</summary>
    public static ScrollTail? Attach(DependencyObject? owner)
        => owner is null ? null : new ScrollTail(owner);

    /// <summary>向上滚轮 = 明确要离开底部。向下滚交给 ScrollChanged 判断是否到底。</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0 && _stick)
        {
            _stick = false;
            Diag("wheelup", "wheel-up -> stick=false");
        }
    }

    /// <summary>找到滚动容器并订阅；容器为空、或旧的已掉线（模板重建）时调用。</summary>
    /// <param name="follow">绑定成功后是否立刻贴一次底（从 ToEnd 里回绑时传 false，避免多滚一遍）。</param>
    private void Bind(bool follow = true)
    {
        var found = _owner as ScrollViewer ?? Find(_owner);

        // 已经挂在同一个活容器上：不必重挂，只补一次贴底。
        if (found is not null && ReferenceEquals(found, _view) && found.IsLoaded)
        {
            if (follow && _stick)
                ToEnd();

            return;
        }

        if (_view is not null)
        {
            _view.ScrollChanged -= OnScrollChanged;
            _view = null;
        }

        if (found is null)
        {
            Diag("noview", "bind: no ScrollViewer yet");
            return;
        }

        // ★ 页签没选中时，整棵子树已经从可视树摘下，找到的那个容器是「死」的：
        //   订阅它收不到任何事件，还会让 HasView 说谎。这里只记一笔，
        //   等 Loaded / IsVisibleChanged 再来 —— 那时才真的挂得上。
        if (!found.IsLoaded)
        {
            Diag("unloaded", "bind: container not in visual tree (tab hidden)");
            return;
        }

        _view = found;
        _view.ScrollChanged += OnScrollChanged;

        // 补绑时**不**按位置重算 _stick。位置推算法正是 ③ 里那个坏掉的东西：
        // 容器还没测量时 extent/offset 都是 0，任何公式都会得出「在底部」。
        // 意图只由滚轮/向下到底两条明确信号改变。
        Diag($"ok:{RuntimeHelpers.GetHashCode(found)}",
             $"bind: ok extent={found.ExtentHeight:F0} " +
             $"off={found.VerticalOffset:F0} view={found.ViewportHeight:F0} stick={_stick}");

        // ★ 第一次挂上容器时如果仍在跟随，立刻贴一次底。
        //   典型场景：第一次切到「工具调用」页签，ListBox 刚上树，滚动位置在顶部，
        //   不贴底就会看到最旧的几条 —— 正是「得自己往下拖」的另一种表现。
        if (follow && _stick)
            ToEnd();
    }

    /// <summary>请求滚到最新；用户上翻过就什么都不做。</summary>
    public void ToEnd()
    {
        if (!_stick || _queued)
            return;

        _queued = true;

        // 优先级提到 Render：Background 会被后续刷新一路挤掉，表现为「滚得一顿一顿」。
        _owner.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _queued = false;

                if (!_stick)
                    return;

                // 模板可能重建过（页签切走再切回）：旧的容器已经摘下来了，重新绑。
                if (_view is null || !_view.IsLoaded)
                    Bind(follow: false);

                // ★ 列表走这条：虚拟化下 ScrollToEnd 只到「已测量到的末尾」。
                if (_list is not null && _list.Items.Count > 0)
                {
                    _list.ScrollIntoView(_list.Items[_list.Items.Count - 1]);
                    return;
                }

                _view?.ScrollToEnd();
            }),
            DispatcherPriority.Render);
    }

    /// <summary>按事件判断用户意图；内容长短变化不算。</summary>
    /// <remarks>
    /// ★ 只认两条明确信号（见类注释 ③）：
    ///   向上位移 → 离开底部；向下位移且已到接近底部 → 回到跟随。
    ///   ExtentHeightChange ≠ 0 的位移（RemoveAt(0) 截断日志、虚拟化重测）
    ///   一律不改意图，否则会莫名其妙地停止跟随。
    /// ★ 不会形成循环：ToEnd 自己合并请求；滚动本身产生的事件里 extent 不变，
    ///   走到下面两条分支只会把 _stick 判成当前同样的值。
    /// </remarks>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 内容长短变化：仍在跟随就补滚一次，意图不变。
        if (e.ExtentHeightChange != 0)
        {
            if (_stick)
                ToEnd();

            return;
        }

        var atBottom = e.ExtentHeight - e.VerticalOffset - e.ViewportHeight < StickThreshold;

        if (e.VerticalChange < -0.5)
        {
            if (_stick)
            {
                _stick = false;
                Diag("scrollup", $"scroll-up -> stick=false (v={e.VerticalChange:F0} atBottom={atBottom})");
            }

            return;
        }

        if (e.VerticalChange > 0.5 && atBottom && !_stick)
        {
            _stick = true;
            Diag("scrolldown", "scroll-down-to-bottom -> stick=true");
        }
    }

    private static ScrollViewer? Find(DependencyObject? node)
    {
        if (node is null)
            return null;

        if (node is ScrollViewer hit)
            return hit;

        var count = VisualTreeHelper.GetChildrenCount(node);

        for (var i = 0; i < count; i++)
        {
            if (Find(VisualTreeHelper.GetChild(node, i)) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>只在状态翻转/绑定结果时写一行，正常使用几乎不产生 I/O。</summary>
    /// <param name="key">去重键：同一种状态反复出现只记第一行（否则每来一条内容都会写一次）。</param>
    /// <param name="message">写进日志的正文。</param>
    private void Diag(string key, string message)
    {
        if (key == _lastDiagKey)
            return;

        _lastDiagKey = key;

        try
        {
            var info = new FileInfo(DiagPath);

            if (info.Exists && info.Length > 512 * 1024)
                info.Delete();

            var dir = info.Directory;

            if (dir is { Exists: false })
                dir.Create();

            File.AppendAllText(
                DiagPath,
                $"{DateTime.Now:HH:mm:ss.fff} [{_tag}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 诊断写不进去不影响功能。
        }
    }
}
