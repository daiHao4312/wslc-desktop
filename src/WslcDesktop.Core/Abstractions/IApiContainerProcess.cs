namespace WslcDesktop.Core.Abstractions;

/// <summary>
/// 容器内进程的句柄（M19）：能发信号、等退出、拿 PID。
///
/// 和 CLI <c>wslc exec</c> 的本质区别就是 <see cref="Terminate"/> / <see cref="Kill"/>：
/// 那边能杀的只是"客户端进程"，容器里的进程可能继续跑；这里的信号是发给**容器内进程**本身的。
/// </summary>
public interface IApiContainerProcess : IDisposable
{
    int Pid { get; }

    /// <summary>等进程结束并拿到退出码。取消令牌只影响等待，不会杀进程。</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>优雅结束（SIGTERM）。</summary>
    void Terminate();

    /// <summary>强制结束（SIGKILL）。</summary>
    void Kill();
}
