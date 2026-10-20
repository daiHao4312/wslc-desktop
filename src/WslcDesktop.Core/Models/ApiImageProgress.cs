namespace WslcDesktop.Core.Models;

/// <summary>
/// 官方 API 的镜像拉取进度快照（防腐层：不把 WinRT 的 <c>ImageProgress</c> 泄漏到服务层以上）。
///
/// 与 CLI 通道的关键差别：这里<b>有真实字节数</b>，所以能给出确定进度的百分比；
/// CLI 通道只回状态关键字，界面只能是不确定进度条 + 状态文本。
/// </summary>
public record ApiImageProgress
{
    /// <summary>层 ID；非分层事件（整体状态）可能为空。</summary>
    public string ImageId { get; init; } = string.Empty;

    /// <summary>引擎给的状态（<c>Pulling</c>/<c>Downloading</c>/<c>Verifying</c>/<c>Extracting</c>/<c>Complete</c>）。</summary>
    public string Status { get; init; } = string.Empty;

    public ulong DownloadedBytes { get; init; }

    public ulong TotalBytes { get; init; }

    /// <summary>已下载百分比（0-100）；总量未知时为 null。</summary>
    public double? Percent => TotalBytes > 0
        ? Math.Clamp(DownloadedBytes * 100.0 / TotalBytes, 0, 100)
        : null;

    public bool IsTerminal => string.Equals(Status, "Complete", StringComparison.OrdinalIgnoreCase);
}
