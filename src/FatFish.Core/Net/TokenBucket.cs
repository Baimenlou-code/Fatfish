using System.Diagnostics;

namespace FatFish.Core.Net;

/// <summary>
/// 令牌桶限速器。
///
/// 官方 Search / Fetch 各 5 QPS（**账号级**，不是连接级）。超了返回 429：
/// 429 不计费，但白等一轮也费时间，而且连续 429 有被临时封禁的风险。
/// 所以主动把自己压到 QPS 以内，比「撞了再退避」划算。
///
/// ★ 容量取 <c>min(qps, 10)</c>：
///   1. 至少留 1 个令牌，否则一笔请求都不许发；
///   2. 不允许「攒 100 个令牌然后一次性打 100 个请求」——
///      突发流量正是最容易触发风控的形态。
///
/// ★ 实现是异步的（<see cref="AcquireAsync"/>），不是 <c>Thread.Sleep</c>：
///   本内核的调用点全都在 async 流程里，同步阻塞会把 UI 线程一起冻住。
/// </summary>
public sealed class TokenBucket
{
    private readonly double _qps;
    private readonly double _capacity;
    private readonly object _gate = new();

    private double _tokens;
    private long _lastTicks;

    public TokenBucket(double qps)
    {
        _qps = Math.Max(0.1, qps);
        _capacity = Math.Max(1.0, Math.Min(_qps, 10.0));
        _tokens = _capacity;
        _lastTicks = Stopwatch.GetTimestamp();
    }

    public double Qps => _qps;

    /// <summary>
    /// 取一个令牌；拿不到就异步等。等待超过 <paramref name="timeout"/> 返回 false。
    /// </summary>
    /// <remarks>
    /// 最多等 <paramref name="timeout"/> —— 不设上限的话，一次卡住的限速会让
    /// 整轮对话静默挂死几分钟，而界面上看不出任何异常。
    /// </remarks>
    public async ValueTask<bool> AcquireAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = Stopwatch.GetTimestamp()
                       + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            double wait;

            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
                _lastTicks = now;

                _tokens = Math.Min(_capacity, _tokens + elapsed * _qps);

                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return true;
                }

                wait = (1.0 - _tokens) / _qps;
            }

            if (Stopwatch.GetTimestamp() + (long)(wait * Stopwatch.Frequency) > deadline)
                return false;

            // 分段睡：长等待时能被取消信号及时打断
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, 0.25)), ct).ConfigureAwait(false);
        }
    }

    /// <summary>当前可用令牌数（诊断/测试用）。</summary>
    public double AvailableTokens
    {
        get
        {
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
                return Math.Min(_capacity, _tokens + elapsed * _qps);
            }
        }
    }
}
