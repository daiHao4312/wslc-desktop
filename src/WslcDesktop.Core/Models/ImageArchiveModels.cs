namespace WslcDesktop.Core.Models;

/// <summary>
/// 镜像导出/导入的进度。
///
/// ⚠️ 两侧的"连续量"完全不同，别指望同一个口径：
/// <list type="bullet">
/// <item><b>导出</b>（<c>wslc save</c>）引擎**一行输出都没有**，唯一连续的量是目标文件大小
/// （与 <c>wsl --export</c> 同一个毛病）；总大小事先不可知，所以进度条只能是不确定态。</item>
/// <item><b>导入</b>（<c>wslc load</c>）只在每个镜像加载完时打一行 <c>已加载映像: xxx</c>，
/// 没有字节数，能报的只有"已经加载了哪些"。</item>
/// </list>
/// </summary>
public record ImageArchiveProgress
{
    /// <summary>导出时目标文件当前字节数；导入时恒为 0（引擎不给）。</summary>
    public long BytesWritten { get; init; }

    /// <summary>状态文字（"正在导出…" / "已加载 my-app:1.0"）。</summary>
    public string Status { get; init; } = string.Empty;

    public string Display => FormatSize(BytesWritten);

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1024L => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };
}

/// <summary>镜像导出／导入的结果。</summary>
public class ImageArchiveResult
{
    public bool Success { get; init; }

    /// <summary>给用户看的一句话（成功时含文件大小/镜像数，失败时是引擎原因）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>tar 文件的路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>等效命令行（诊断用，不含任何凭据）。</summary>
    public string CommandLine { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }

    /// <summary>导入时引擎报告的镜像引用（<c>已加载映像:</c> 那几行）；导出时为参与导出的引用。</summary>
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 是"用户取消"而不是"出错"。
    /// 界面上两者要分开说：取消不该弹错误框，也不该说"失败"。
    /// </summary>
    public bool Cancelled { get; init; }
}
