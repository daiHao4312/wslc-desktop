namespace WslcDesktop.Helpers;

/// <summary>
/// 单实例门闸：用命名互斥量判定主实例，用命名事件让后续实例"唤醒"主实例。
///
/// 为什么需要：面板是长驻工具（带托盘、事件流），允许开多份会带来
/// 多个 <c>wslc events</c> 子进程、多个托盘图标，以及关闭时互相打架。
///
/// 命名用 <c>Local\</c> 前缀：作用域限于当前登录会话，
/// 多用户/多会话（RDP）下互不干扰，各自都能开一份。
/// </summary>
public sealed class SingleInstanceGate : IDisposable
{
    private const string MutexName = @"Local\WslcDesktop.SingleInstance.v1";
    private const string ActivateEventName = @"Local\WslcDesktop.Activate.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _listenerCts;
    private Thread? _listenerThread;

    /// <summary>本进程是否是主实例。</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>
    /// 尝试成为主实例。
    /// 无论成功与否都会创建激活事件——这样后续实例可以直接 Signal，
    /// 不必处理"主实例还没把事件建出来"的竞态。
    /// </summary>
    public bool TryAcquire()
    {
        _activateEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ActivateEventName);

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimary = createdNew;

        return createdNew;
    }

    /// <summary>通知主实例把自己唤到前台（由后续实例调用，随后应自行退出）。</summary>
    public void SignalPrimary()
    {
        try
        {
            _activateEvent?.Set();
        }
        catch
        {
            // 主实例可能刚好在退出；静默即可，反正我们也要退了
        }
    }

    /// <summary>
    /// 主实例开始监听激活请求。<paramref name="onActivated"/> 在<b>后台线程</b>触发，
    /// 调用方需要自行切回 UI 线程。
    /// </summary>
    public void StartListening(Action onActivated)
    {
        if (!IsPrimary || _activateEvent is null) return;

        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;

        _listenerThread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                // 带超时地等：这样取消能及时生效，不必依赖 Abandon
                if (!_activateEvent.WaitOne(TimeSpan.FromMilliseconds(400)))
                {
                    continue;
                }

                if (token.IsCancellationRequested) break;

                try
                {
                    onActivated();
                }
                catch
                {
                    // 唤到前台失败不该拖垮监听线程
                }
            }
        })
        {
            IsBackground = true,
            Name = "WslcDesktop.SingleInstanceListener"
        };

        _listenerThread.Start();
    }

    public void Dispose()
    {
        try
        {
            _listenerCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _listenerThread?.Join(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // ignore
        }

        _listenerCts?.Dispose();

        if (IsPrimary && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // 已经释放过则忽略
            }
        }

        _mutex?.Dispose();
        _activateEvent?.Dispose();
    }
}
