namespace WslcDesktop.Core.Models;

/// <summary>容器内一次性命令（<c>wslc exec</c>）的执行结果。</summary>
public record ExecResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}
