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

    /// <summary>
    /// 发布资产（<c>.exe</c>）的直链，**没有回落到发布页**。
    /// 在线更新只能吃直链，所以这里与 <see cref="DownloadUrl"/> 分开表达：
    /// 前者是"能点开看看的落点"，后者是"能拿来自动替换的文件"。
    /// </summary>
    public string? AssetUrl { get; init; }

    /// <summary>资产文件名（用于落盘命名）。</summary>
    public string? AssetName { get; init; }

    /// <summary>资产字节数；GitHub 未提供时为 null。</summary>
    public long? AssetSize { get; init; }

    /// <summary>资产的 sha256（小写十六进制）；GitHub 未提供 digest 时为 null。</summary>
    public string? AssetSha256 { get; init; }

    /// <summary>发布说明摘要。</summary>
    public string? Notes { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    public bool HasUpdate => Status == UpdateCheckStatus.UpdateAvailable;

    /// <summary>是否拿到了可点开的下载地址（有新版时才有意义）。</summary>
    public bool CanOpenDownload => HasUpdate && !string.IsNullOrWhiteSpace(DownloadUrl);

    /// <summary>
    /// 能否走「下载并原地替换」这条路：有新版本 **且** 拿到了 exe 资产直链。
    /// 只有发布页地址（没传 exe 资产）时只能退回"打开下载页"，不硬猜链接。
    /// </summary>
    public bool CanAutoUpdate => HasUpdate && !string.IsNullOrWhiteSpace(AssetUrl);

    /// <summary>
    /// 把本次检查结果转成下载用的更新包描述。没有资产直链时返回 null。
    /// </summary>
    public UpdatePackage? ToPackage()
    {
        if (!CanAutoUpdate) return null;

        return new UpdatePackage
        {
            Version = LatestVersion ?? string.Empty,
            DownloadUrl = AssetUrl!,
            FileName = string.IsNullOrWhiteSpace(AssetName)
                ? $"wslc-desktop-{LatestVersion}.exe"
                : AssetName!,
            Size = AssetSize,
            Sha256 = AssetSha256
        };
    }

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
