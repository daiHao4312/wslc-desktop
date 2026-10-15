namespace WslcDesktop.Core.Abstractions;

public record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool IsSuccess);

public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        string fileName,
        string arguments,
        IDictionary<string, string>? customEnvironment = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式执行：每读到一行输出立即回调（用于镜像拉取这类需要实时反馈的长任务）。
    /// 与 <see cref="RunAsync"/> 不同，超时由调用方通过取消令牌控制。
    /// </summary>
    /// <param name="onStarted">
    /// 进程<b>成功启动</b>后立刻回调一次，且只回调一次。
    ///
    /// 存在的理由：像 <c>wslc events</c> 这样的长驻订阅，"起来了"和"有输出"之间可能隔很久
    /// （回补窗口内没有事件时甚至隔几小时），调用方没有这个信号就无法区分
    /// "订阅健康、只是没事件"和"根本连不上"。
    /// </param>
    Task<ProcessExecutionResult> RunStreamingAsync(
        string fileName,
        string arguments,
        Action<string> onOutputLine,
        Action<string>? onErrorLine = null,
        IDictionary<string, string>? customEnvironment = null,
        CancellationToken cancellationToken = default,
        Action? onStarted = null);
}
