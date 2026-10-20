namespace WslcDesktop.Core.Models;

/// <summary>
/// 一个可下载的更新包（对应 GitHub Release 里那个 <c>.exe</c> 资产）。
/// </summary>
public record UpdatePackage
{
    /// <summary>版本号（已去掉 v 前缀），形如 <c>1.2.0</c>。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>资产直链（GitHub 的 browser_download_url）。</summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>
    /// 落盘时使用的文件名。
    /// <b>视为不可信输入</b>：下载器会先做 <see cref="Path.GetFileName(string)"/> 净化，
    /// 防止上游给出带路径分隔符的名字写到目录外。
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>期望字节数；null 表示上游没给，跳过长度校验。</summary>
    public long? Size { get; init; }

    /// <summary>期望的 sha256（小写十六进制）；null 表示上游没给 digest，跳过哈希校验。</summary>
    public string? Sha256 { get; init; }

    /// <summary>是否有可强校验的摘要。</summary>
    public bool HasChecksum => !string.IsNullOrWhiteSpace(Sha256);
}

/// <summary>下载进度的快照。总长度未知时 <see cref="TotalBytes"/> 为 null。</summary>
public readonly record struct UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>完成百分比（0–100）；总长度未知时为 null，UI 应改用不确定进度条。</summary>
    public double? Percent => TotalBytes is > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes.Value * 100d, 0d, 100d)
        : null;

    public bool IsIndeterminate => TotalBytes is null or <= 0;

    /// <summary>形如 <c>“已下载 42.1 MB / 95.7 MB（44%）”</c>，用于界面文案。</summary>
    public string Describe()
    {
        var done = FormatSize(BytesReceived);

        if (TotalBytes is not > 0)
        {
            return $"已下载 {done}";
        }

        return $"已下载 {done} / {FormatSize(TotalBytes.Value)}（{Percent!.Value:0}%）";
    }

    /// <summary>把字节数格式化成易读的 MB / KB。</summary>
    public static string FormatSize(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        return bytes switch
        {
            >= (long)gb => $"{bytes / gb:0.00} GB",
            >= (long)mb => $"{bytes / mb:0.0} MB",
            >= (long)kb => $"{bytes / kb:0.0} KB",
            _ => $"{bytes} B"
        };
    }
}

public enum UpdateDownloadStatus
{
    Succeeded,

    /// <summary>网络失败、校验不过、磁盘写入失败等。</summary>
    Failed,

    /// <summary>用户主动取消（已清理半成品文件）。</summary>
    Cancelled
}

/// <summary>一次下载的结果。所有失败都降级成对象，不抛异常（与更新检查保持一致）。</summary>
public record UpdateDownloadResult
{
    public UpdateDownloadStatus Status { get; init; }

    /// <summary>可直接展示给用户的一句话结论。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>下载完成后的本地路径（成功时才有值）。</summary>
    public string? FilePath { get; init; }

    public long BytesWritten { get; init; }

    /// <summary>实际算出来的 sha256（成功时才有值）。</summary>
    public string? Sha256 { get; init; }

    /// <summary>命中"本地已有同版本且校验通过"的包，没有重新下载。</summary>
    public bool ReusedExisting { get; init; }

    public bool Succeeded => Status == UpdateDownloadStatus.Succeeded;

    public static UpdateDownloadResult Ok(string path, long bytes, string? sha256, bool reused = false) => new()
    {
        Status = UpdateDownloadStatus.Succeeded,
        Message = reused ? "已存在校验通过的更新包，直接复用。" : "下载完成并已通过完整性校验。",
        FilePath = path,
        BytesWritten = bytes,
        Sha256 = sha256,
        ReusedExisting = reused
    };

    public static UpdateDownloadResult Failure(string message) => new()
    {
        Status = UpdateDownloadStatus.Failed,
        Message = message
    };

    public static UpdateDownloadResult Cancelled() => new()
    {
        Status = UpdateDownloadStatus.Cancelled,
        Message = "已取消下载（未完成的临时文件已清理）。"
    };
}
