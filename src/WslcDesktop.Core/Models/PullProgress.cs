namespace WslcDesktop.Core.Models;

/// <summary>镜像拉取过程中的一条进度快照。</summary>
public record PullProgress
{
    public string ImageReference { get; init; } = string.Empty;

    /// <summary>层 ID（如 "9a02b1d1b3f5"），非分层行（Digest/Status）为空。</summary>
    public string LayerId { get; init; } = string.Empty;

    /// <summary>状态关键字：Pulling / Downloading / Extracting / Pull complete / Already exists 等。</summary>
    public string Status { get; init; } = string.Empty;

    public ulong? DownloadedBytes { get; init; }
    public ulong? TotalBytes { get; init; }

    /// <summary>已下载百分比（0-100），无法计算时为 null。</summary>
    public double? Percent { get; init; }

    /// <summary>该行是否为终态提示（Pull complete / Digest / Status: 已下载）。</summary>
    public bool IsTerminal { get; init; }

    /// <summary>原始行文本，便于界面直接展示。</summary>
    public string RawLine { get; init; } = string.Empty;
}

/// <summary>一次拉取任务的最终结果。</summary>
public record PullResult
{
    public bool Success { get; init; }
    public string ImageReference { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    /// <summary>拉取耗时。</summary>
    public TimeSpan Duration { get; init; }
}
