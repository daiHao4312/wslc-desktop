namespace WslcDesktop.Core.Models;

public enum UpdateCheckStatus
{
    /// <summary>没配置更新源（仓库为空）。</summary>
    NotConfigured,

    /// <summary>已是最新版本。</summary>
    UpToDate,

    /// <summary>有新版本可用。</summary>
    UpdateAvailable,

    /// <summary>仓库不存在或不可访问（404 / 私有仓库）。</summary>
    RepoNotFound,

    /// <summary>网络失败、超时、返回体无法解析等。</summary>
    Failed
}

/// <summary>一次「检查更新」的结果。</summary>
public record UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }

    /// <summary>可直接展示给用户的一句话结论。</summary>
    public string Message { get; init; } = string.Empty;

    public string CurrentVersion { get; init; } = string.Empty;

    public string? LatestVersion { get; init; }

    /// <summary>下载落点（资产直链优先，退回发布页）。</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>发布说明摘要。</summary>
    public string? Notes { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    public bool HasUpdate => Status == UpdateCheckStatus.UpdateAvailable;

    /// <summary>是否拿到了可点开的下载地址（有新版时才有意义）。</summary>
    public bool CanOpenDownload => HasUpdate && !string.IsNullOrWhiteSpace(DownloadUrl);

    public static UpdateCheckResult NotConfigured(string currentVersion) => new()
    {
        Status = UpdateCheckStatus.NotConfigured,
        CurrentVersion = currentVersion,
        Message = "未配置更新源。可在「设置 → 应用偏好」里填写 GitHub 仓库（owner/repo）。"
    };

    public static UpdateCheckResult Failure(string currentVersion, string message) => new()
    {
        Status = UpdateCheckStatus.Failed,
        CurrentVersion = currentVersion,
        Message = message
    };
}
