using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 事件流长驻订阅的实现。
///
/// 生命周期只有一个后台循环：<c>订阅 → （引擎退出/异常）→ 退避 → 用上一条事件时间续接重订</c>。
/// 首次订阅会回补一段历史（默认 6 小时），这样活动记录页一打开就有内容，
/// 而不是盯着空列表等下一次操作。
///
/// 线程纪律：<see cref="State"/> 与缓冲都在同一把锁下读写；
/// <see cref="EventReceived"/> 在锁<b>外</b>触发，避免订阅方回调用到服务时自锁。
/// </summary>
public sealed class EventStreamService : IEventStreamService
{
    /// <summary>首次订阅回补的时长。</summary>
    private static readonly TimeSpan BackfillWindow = TimeSpan.FromHours(6);

    /// <summary>续接时往前多要一点，避免边界事件因时间戳取整被漏掉；重复由去重表压掉。</summary>
    private static readonly TimeSpan ResumeOverlap = TimeSpan.FromSeconds(2);

    private const int MaxRecentEvents = 500;
    private const int MaxDedupKeys = 2000;

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

    private readonly IWslcClient _client;
    private readonly object _gate = new();

    private readonly List<WslcEvent> _recent = new();
    private readonly HashSet<string> _dedupKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _dedupOrder = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset? _lastEventTime;
    private EventStreamState _state = EventStreamState.Stopped;

    public EventStreamService(IWslcClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public EventStreamState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// 订阅是否<b>有效</b>——含"已建立但还没有事件"。
    ///
    /// 不能只算 <see cref="EventStreamState.Running"/>：回补窗口内没有事件时，
    /// 首连会长时间停在 <see cref="EventStreamState.Subscribed"/>，
    /// 若那里返回 false，轮询降级就永远不生效（会按 3 秒一圈空转）。
    /// </summary>
    public bool IsRunning => State is EventStreamState.Subscribed or EventStreamState.Running;

    public IReadOnlyList<WslcEvent> RecentEvents
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }

    public event EventHandler<WslcEvent>? EventReceived;

    public event EventHandler<EventStreamStatus>? StatusChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;

        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // 取消失败不影响后续状态复位
        }

        // 不等太久：窗口正在关闭，没必要为收尾卡住界面
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 循环内部异常已被 RunAsync 吞掉，这里只兜 Wait 抛出的聚合异常
        }

        cts?.Dispose();
        SetState(EventStreamState.Stopped);
    }

    public void Clear()
    {
        lock (_gate)
        {
            // 只清展示缓冲。去重表是内部正确性结构：清掉会让重连重叠的事件重新冒出来
            _recent.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var backoff = InitialBackoff;
        var isFirstConnect = true;

        while (!token.IsCancellationRequested)
        {
            SetState(isFirstConnect ? EventStreamState.Connecting : EventStreamState.Reconnecting);

            DateTimeOffset since;
            lock (_gate)
            {
                // 收到过事件就按"上一条时间 - 重叠量"续接；否则回补一段历史
                since = _lastEventTime is DateTimeOffset last
                    ? last - ResumeOverlap
                    : DateTimeOffset.Now - BackfillWindow;
            }

            var receivedAny = false;

            try
            {
                await _client.StreamEventsAsync(
                    evt =>
                    {
                        receivedAny = true;
                        Publish(evt);
                    },
                    since,
                    token,
                    // 订阅一建立就先报出去：否则"进程起来了、但这轮没有事件"会一直显示成"连接中"
                    MarkSubscribed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 取消也可能以普通异常的形式从底层冒出来（如 ObjectDisposedException）：
                // 已取消就不改状态，否则 Stop() 刚置的 Stopped 会被打回 Reconnecting
                if (token.IsCancellationRequested) break;

                SetState(EventStreamState.Reconnecting, $"事件流中断：{ex.Message}");
            }

            if (token.IsCancellationRequested) break;

            isFirstConnect = false;

            if (receivedAny)
            {
                // 这一轮曾经是通的，说明是订阅被断掉了（WSL 重启、引擎升级等）
                backoff = InitialBackoff;
                SetState(EventStreamState.Reconnecting, "事件流已断开，正在重连…");
            }
            else
            {
                // 一次都没收到就返回，多半是引擎不可用；指数退避，别空转
                backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
                SetState(EventStreamState.Reconnecting, "未能订阅到引擎事件，正在重试…");
            }

            try
            {
                await Task.Delay(backoff, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetState(EventStreamState.Stopped);
    }

    private void Publish(WslcEvent evt)
    {
        bool stateChanged;

        lock (_gate)
        {
            // 续接会重复送达少量事件，压掉
            if (!_dedupKeys.Add(evt.DedupKey)) return;

            _dedupOrder.Enqueue(evt.DedupKey);
            while (_dedupOrder.Count > MaxDedupKeys)
            {
                _dedupKeys.Remove(_dedupOrder.Dequeue());
            }

            _recent.Add(evt);
            if (_recent.Count > MaxRecentEvents)
            {
                _recent.RemoveRange(0, _recent.Count - MaxRecentEvents);
            }

            if (_lastEventTime is null || evt.Time >= _lastEventTime.Value)
            {
                _lastEventTime = evt.Time;
            }

            stateChanged = _state != EventStreamState.Running;
            _state = EventStreamState.Running;
        }

        EventReceived?.Invoke(this, evt);

        if (stateChanged)
        {
            StatusChanged?.Invoke(this, new EventStreamStatus(EventStreamState.Running));
        }
    }

    /// <summary>
    /// 订阅已建立。只在"还没收到事件"时才落这个状态：
    /// 进程启动与事件到达之间存在竞态，先到的事件已经把状态推到 <see cref="EventStreamState.Running"/>，
    /// 这里再写回去就会把 Running 打回 Subscribed。
    /// </summary>
    private void MarkSubscribed()
    {
        bool changed;

        lock (_gate)
        {
            if (_state is EventStreamState.Running or EventStreamState.Stopped) return;

            changed = _state != EventStreamState.Subscribed;
            _state = EventStreamState.Subscribed;
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, new EventStreamStatus(EventStreamState.Subscribed));
        }
    }

    private void SetState(EventStreamState state, string? message = null)
    {
        bool changed;

        lock (_gate)
        {
            changed = _state != state;
            _state = state;
        }

        if (changed || message != null)
        {
            StatusChanged?.Invoke(this, new EventStreamStatus(state, message));
        }
    }
}
