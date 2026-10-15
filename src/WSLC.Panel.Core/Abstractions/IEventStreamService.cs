using WSLC.Panel.Core.Models;

namespace WSLC.Panel.Core.Abstractions;

/// <summary>事件流连接状态。</summary>
public enum EventStreamState
{
    /// <summary>未启动（用户关闭，或引擎不可用）。</summary>
    Stopped,

    /// <summary>首次建立订阅中。</summary>
    Connecting,

    /// <summary>
    /// 订阅已建立，但还没有收到任何事件。
    ///
    /// 和 <see cref="Connecting"/> 是两回事：首次订阅会回补 6 小时历史，
    /// 若这个窗口内一次容器操作都没有，引擎不会吐出任何东西，这个状态可以持续很久。
    /// 界面必须把它显示成"已订阅、等待事件"，而不是"连接中"。
    /// </summary>
    Subscribed,

    /// <summary>订阅正常，事件实时送达。</summary>
    Running,

    /// <summary>断开后自动重连中（附原因）。</summary>
    Reconnecting
}

public sealed record EventStreamStatus(EventStreamState State, string? Message = null);

/// <summary>
/// 引擎事件长驻订阅：内部负责保持 <c>wslc events</c> 子进程、断线重连、补拉去重，
/// 并把事件抛给订阅方。
///
/// 为什么要有这一层而不是直接用 <c>IWslcClient.StreamEventsAsync</c>：
/// ① 一次订阅会随引擎进程退出而结束（WSL 重启、引擎升级都会触发），必须有人重连；
/// ② 重连要按"上一条事件时间"续接，否则断线期间的事件会永久丢失；
/// ③ 续接会重复送达少量事件，必须去重。
/// </summary>
public interface IEventStreamService : IDisposable
{
    /// <summary>是否处于正常订阅状态（界面据此决定还要不要靠轮询兜底）。</summary>
    bool IsRunning { get; }

    EventStreamState State { get; }

    /// <summary>最近事件环形缓冲（最新在末尾），供活动记录页首次打开时回填。</summary>
    IReadOnlyList<WslcEvent> RecentEvents { get; }

    /// <summary>在<b>后台线程</b>上触发，订阅方需自行切到 UI 线程。</summary>
    event EventHandler<WslcEvent>? EventReceived;

    event EventHandler<EventStreamStatus>? StatusChanged;

    void Start();

    void Stop();

    /// <summary>清空最近事件缓冲（活动记录页的"清空"按钮）。不影响订阅，也不动去重表。</summary>
    void Clear();
}
