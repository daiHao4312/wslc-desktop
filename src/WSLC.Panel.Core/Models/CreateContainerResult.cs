namespace WSLC.Panel.Core.Models;

/// <summary>创建容器的执行结果。</summary>
public record CreateContainerResult
{
    public bool Success { get; init; }

    /// <summary>引擎返回的容器 ID（<c>run -d</c> 时首行即为 ID）。</summary>
    public string ContainerId { get; init; } = string.Empty;

    /// <summary>实际执行的等效命令行（用于界面回显与排错）。</summary>
    public string CommandLine { get; init; } = string.Empty;

    /// <summary>失败原因或成功提示。</summary>
    public string Message { get; init; } = string.Empty;
}
