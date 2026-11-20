using FatFish.Core.Net;

namespace FatFish.App;

/// <summary>
/// 主窗口的「余额定时刷新」部分。
/// </summary>
/// <remarks>
/// ★ 2026-11-05 新增。原来余额只在**两个时刻**查：窗口加载完一次、用户点那一格一次，
///   再压一层 60 秒进程缓存。结果就是：聊一晚上，那格数字一次都不动 ——
///   而那正是真正在花钱的时候。用户的原话是「下方的余额计量好像失效了」，
///   其实它没坏，是没人再去取新数字。
///
/// ★ 为什么单独开一个 partial 文件而不是写进 MainWindow.xaml.cs：
///   那个文件已经 2000 行，且这一整块和主窗口的装配、发送、事件泵无关，
///   拼在一起只会让「余额逻辑在哪」更难找。
///
/// ★ 为什么是 force 刷新而不是吃缓存：
///   缓存 TTL 也是 60 秒，和这个间隔一样长 —— 不 force 的话，
///   每次 tick 拿到的正好是上一次的快照，等于没刷（或者变成两分钟才刷一次）。
///
/// ★ Tavily 查不通不需要在这里处理：BalanceService 会退回本地台账估算，
///   并把 Estimated 标成 true，状态栏显示成「TV ~872」。
/// </remarks>
public partial class MainWindow
{
    /// <summary>每分钟一次的余额刷新计时器。</summary>
    private System.Windows.Threading.DispatcherTimer? _balanceTimer;

    /// <summary>上一轮还没回来就不再起一轮（网络卡住时避免 tick 堆积）。</summary>
    private bool _balanceBusy;

    /// <summary>
    /// 启动定时刷新。必须在 <c>BootKernel()</c> 之后调用 ——
    /// 那时 .env 才被载入进程环境变量，否则每次都会报「未找到 key」。
    /// </summary>
    private void StartBalanceTimer()
    {
        if (_balanceTimer is not null)
            return;

        _balanceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1),
        };

        _balanceTimer.Tick += OnBalanceTick;
        _balanceTimer.Start();

        Log("余额定时刷新已启动（每 60 秒）");
    }

    /// <summary>停止定时刷新（窗口已关时它自己也会停，见 OnBalanceTick）。</summary>
    private void StopBalanceTimer()
    {
        if (_balanceTimer is null)
            return;

        _balanceTimer.Stop();
        _balanceTimer.Tick -= OnBalanceTick;
        _balanceTimer = null;
    }

    private async void OnBalanceTick(object? sender, EventArgs e)
    {
        // 窗口已经关掉还收到 tick（关闭过程中排在队列里的那一下）：
        // 停掉自己，不要再去碰已经释放的控件。
        if (!IsLoaded)
        {
            StopBalanceTimer();
            return;
        }

        if (_balanceBusy)
            return;

        _balanceBusy = true;

        try
        {
            // ledger 传 null：BalanceService 会自己去打开台账（同一个文件，谁开都读到同一份账）。
            var svc = new BalanceService();
            var snap = await svc.FetchAsync(force: true).ConfigureAwait(true);

            var (_, _, overall) = snap.Grade(svc);

            BalanceText = BalanceService.LevelIcon(overall) + " " + BalanceService.OneLine(snap);
        }
        catch (Exception ex)
        {
            // 定时刷新失败了不要抹掉上一轮的数字 —— 那比显示「余额 ?」有用得多。
            Log("余额定时刷新失败（保留上次数字）：" + ex.Message);
        }
        finally
        {
            _balanceBusy = false;
        }
    }
}
