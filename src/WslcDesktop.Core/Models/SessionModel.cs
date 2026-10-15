namespace WslcDesktop.Core.Models;

public record SessionModel
{
    public int Id { get; init; }
    public int CreatorPid { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string StoragePath { get; init; } = string.Empty;
    public long? VhdSizeBytes { get; init; }
    public long? SwapSizeBytes { get; init; }

    public bool IsCliDefault => DisplayName.StartsWith("wslc-cli-", StringComparison.OrdinalIgnoreCase);

    public string VhdSizeDisplay => FormatSize(VhdSizeBytes);

    public string SwapSizeDisplay => FormatSize(SwapSizeBytes);

    public long TotalSizeBytes => (VhdSizeBytes ?? 0) + (SwapSizeBytes ?? 0);

    public string TotalSizeDisplay => FormatSize(TotalSizeBytes > 0 ? TotalSizeBytes : null);

    public string StoragePathDisplay => string.IsNullOrWhiteSpace(StoragePath)
        ? "未定位到存储文件"
        : StoragePath;

    /// <summary>会话 ID / 创建者进程 ID（列表里并排展示，省一列）。</summary>
    public string PidDisplay => $"#{Id} / PID {CreatorPid}";

    private static string FormatSize(long? bytes) => bytes switch
    {
        null or <= 0 => "未知",
        < 1024L * 1024 => $"{bytes.Value / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes.Value / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes.Value / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };
}
