using WslcDesktop.Core.Abstractions;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 记录调用的 <see cref="IProcessRunner"/> 替身。
///
/// 沙箱与 CI 里 <c>wsl.exe</c> / <c>reg.exe</c> 都跑不了，所以"某个操作到底拼出了什么参数、
/// 有没有在参数不合法时仍然去调引擎"这类判断只能靠它来钉住。
/// </summary>
internal sealed class RecordingProcessRunner : IProcessRunner
{
    /// <summary>按调用顺序记录 <c>{fileName} {arguments}</c>。</summary>
    public List<string> Calls { get; } = new();

    /// <summary>按 (fileName, arguments) 返回结果；未设置时返回成功空输出。</summary>
    public Func<string, string, ProcessExecutionResult>? Handler { get; set; }

    public Task<ProcessExecutionResult> RunAsync(
        string fileName,
        string arguments,
        IDictionary<string, string>? customEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"{fileName} {arguments}");
        return Task.FromResult(Invoke(fileName, arguments));
    }

    public Task<ProcessExecutionResult> RunStreamingAsync(
        string fileName,
        string arguments,
        Action<string> onOutputLine,
        Action<string>? onErrorLine = null,
        IDictionary<string, string>? customEnvironment = null,
        CancellationToken cancellationToken = default,
        Action? onStarted = null)
    {
        Calls.Add($"{fileName} {arguments}");
        onStarted?.Invoke();

        var result = Invoke(fileName, arguments);

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            onOutputLine(line);
        }

        foreach (var line in result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            (onErrorLine ?? onOutputLine)(line);
        }

        return Task.FromResult(result);
    }

    private ProcessExecutionResult Invoke(string fileName, string arguments)
        => Handler?.Invoke(fileName, arguments)
           ?? new ProcessExecutionResult(0, string.Empty, string.Empty, true);
}
