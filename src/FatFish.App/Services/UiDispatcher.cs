using System.Threading.Channels;
using System.Windows.Threading;

namespace FatFish.App.Services;

/// <summary>
/// UI 事件 —— 内核 → 界面 的单向消息。
///
/// 对应 Python 版 FATHFISHII.py 里那三十多个 _win_push_* / push_stream_* 函数，
/// 但这里是**类型**而不是约定：新增一种事件必须新增一个 record，编译器会逼你处理。
/// </summary>
public abstract record UiEvent
{
    public sealed record PushUser(string Text) : UiEvent;

    public sealed record PushAi(string Text) : UiEvent;

    public sealed record PushSystem(string Text) : UiEvent;

    /// <summary>流式开始：新建一个气泡。</summary>
    public sealed record StreamBegin(string StreamId) : UiEvent;

    /// <summary>流式增量：追加到指定气泡。</summary>
    public sealed record StreamDelta(string StreamId, string Text) : UiEvent;

    /// <summary>流式结束：冻结该气泡（此后不可变，才能被虚拟化）。</summary>
    public sealed record StreamEnd(string StreamId) : UiEvent;

    /// <summary>状态栏文字。</summary>
    public sealed record SetStatus(string Text) : UiEvent;

    /// <summary>
    /// 工作台根目录已切换（ws_cd / ws_cd_approve 生效后）。
    ///
    /// ★ 为什么必须是事件而不是让工具直接改界面：
    ///   工具跑在后台线程，碰控件就是跨线程访问异常。
    ///   而且「根变了」这件事影响的是**整个界面**（状态栏、文件树、后续所有操作），
    ///   交给 UI 线程统一处理才可能不漏。
    /// </summary>
    public sealed record WorkspaceChanged(string OldRoot, string NewRoot) : UiEvent;

    /// <summary>
    /// 内核请求人工确认。
    ///
    /// ★ 这是唯一一个「需要 UI 回话」的事件 —— 其他事件都是单向通知。
    ///   回话通过 <see cref="ApprovalRequested.Decision"/> 完成：
    ///   后台线程 await 这个 TaskCompletionSource，UI 线程弹完窗后 SetResult。
    ///   若用户中途取消，后台的 await 会被 ct 打断，这里由调用方 TrySetCanceled。
    /// </summary>
    public sealed record ApprovalRequested(
        Core.Tools.ApprovalRequest Request,
        TaskCompletionSource<ApprovalDecision> Decision) : UiEvent;

    /// <summary>错误提示。</summary>
    public sealed record Error(string Text) : UiEvent;
}

/// <summary>用户对一次审批请求的决定。</summary>
/// <param name="Approved">是否允许执行。</param>
/// <param name="AllowForSession">是否同时对「本次会话的同类操作」放行。</param>
public readonly record struct ApprovalDecision(bool Approved, bool AllowForSession);

/// <summary>
/// UiDispatcher —— 内核线程 与 UI 线程 之间的唯一通道。
///
/// 为什么需要它：
///   WPF 控件只能在创建它的线程（UI 线程）上访问。
///   AgentLoop / 工具执行都跑在后台线程，绝不能直接碰控件。
///
/// 设计要点：
///   1. 用 Channel 做无锁队列，Post 不会阻塞调用方（后台线程永远不被 UI 拖慢）；
///   2. 后台泵用 Dispatcher.Invoke 回到 UI 线程；
///   3. **批量出队** —— 一次 Dispatcher 调用处理队列里所有事件，
///      否则流式输出时每个 token 一次 Dispatcher 往返，CPU 会烧在上下文切换上。
/// </summary>
public sealed class UiDispatcher : IAsyncDisposable
{
    private readonly Channel<UiEvent> _channel;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly Action<UiEvent> _handler;
    private int _pending;

    public UiDispatcher(Dispatcher dispatcher, Action<UiEvent> handler)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        _channel = Channel.CreateUnbounded<UiEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _pump = Task.Run(PumpAsync);
    }

    /// <summary>后台线程调用此方法投递事件，永不阻塞。</summary>
    public void Post(UiEvent evt)
    {
        if (_channel.Writer.TryWrite(evt))
            Interlocked.Increment(ref _pending);
    }

    /// <summary>当前积压事件数（可用于背压诊断）。</summary>
    public int PendingCount => Volatile.Read(ref _pending);

    private async Task PumpAsync()
    {
        var ct = _cts.Token;

        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                // 把此刻所有已到达的事件一次取空，减少 Dispatcher 往返
                var batch = new List<UiEvent>(16);
                while (_channel.Reader.TryRead(out var evt))
                    batch.Add(evt);

                Interlocked.Add(ref _pending, -batch.Count);

                if (batch.Count == 0)
                    continue;

                try
                {
                    // 用 Invoke（同步）而不是 BeginInvoke：
                    // 保证同一批事件按序应用，且异常能在本线程捕获。
                    _dispatcher.Invoke(() =>
                    {
                        foreach (var evt in batch)
                        {
                            try
                            {
                                _handler(evt);
                            }
                            catch (Exception ex)
                            {
                                // 单个事件处理失败不能拖垮整个泵
                                System.Diagnostics.Debug.WriteLine("[UiDispatcher] 处理事件失败: " + ex);
                            }
                        }
                    }, DispatcherPriority.Background);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    // UI 线程已关闭（应用正在退出）
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
            // 退出路径上的异常无需上报
        }

        _cts.Dispose();
    }
}
