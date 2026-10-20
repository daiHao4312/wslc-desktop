namespace WslcDesktop.Core.Models;

/// <summary>发行版详情操作的统一结果（压缩 / 稀疏 / 调整大小 / 移动 / 导出 / 写 wsl.conf）。</summary>
public record DistroOperationResult
{
    public bool Success { get; init; }

    /// <summary>成功时的简短说明，失败时的可读原因（已剥掉 <c>wsl: </c> 配置告警行）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>等效命令（含程序名），便于用户复制到终端核对。</summary>
    public string CommandLine { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }
}

/// <summary>
/// 导出进度。
///
/// <c>wsl --export</c> <b>不打百分比</b>，只偶尔打几行状态，所以进度靠<b>轮询目标文件长到多大</b>
/// —— 那个数字是真实且连续的（参考项目实测同样的结论）。
/// </summary>
public record DistroExportProgress
{
    public long BytesWritten { get; init; }

    public string Display => FormatBytes(BytesWritten);

    private static string FormatBytes(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1024L => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };
}

/// <summary>读 <c>/etc/wsl.conf</c> 的结果。</summary>
public record WslConfSnapshot
{
    public bool Success { get; init; }

    /// <summary>文件内容。<b>文件不存在时是空串且 <see cref="Success"/> 为 true</b>——
    /// 没配过 wsl.conf 是完全正常的状态，不该当错误报给用户。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>读失败原因（发行版不存在、wsl.exe 不可用等）。</summary>
    public string Error { get; init; } = string.Empty;
}
