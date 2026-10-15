namespace WslcDesktop.Core.Models;

public record ContainerModel
{
    public string Id { get; init; } = string.Empty;
    public string? FullId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty; // e.g. "running", "exited", "created"
    public string Status { get; init; } = string.Empty; // e.g. "Up 3 seconds", "Exited (0)"
    public string PortsRaw { get; init; } = string.Empty;
    public IReadOnlyList<PortMappingModel> PortMappings { get; init; } = Array.Empty<PortMappingModel>();
    public string Networks { get; init; } = string.Empty;
    public string CreatedAtRaw { get; init; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; init; }
    public string? RunningFor { get; init; }
    public string? Size { get; init; }
    public ContainerStatsModel? Stats { get; init; }

    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);

    /// <summary>短 ID（列表显示用）。</summary>
    public string ShortId => Id.Length <= 12 ? Id : Id[..12];

    /// <summary>状态徽标文案（中文）。</summary>
    public string StateDisplay => State.ToLowerInvariant() switch
    {
        "running" => "运行中",
        "exited" => "已退出",
        "created" => "已创建",
        "paused" => "已暂停",
        "restarting" => "重启中",
        "dead" => "已终止",
        _ => string.IsNullOrEmpty(State) ? "未知" : State
    };

    /// <summary>CPU 占用（引擎未返回时显示占位符，避免界面出现空白列）。</summary>
    public string CpuDisplay => Stats is null ? "—" : Stats.CpuPercentRaw;

    /// <summary>内存占用（形如 "52.5MiB / 7.6GiB"）。</summary>
    public string MemoryDisplay
        => string.IsNullOrWhiteSpace(Stats?.MemoryUsage) ? "—" : Stats!.MemoryUsage!;

    public string PidsDisplay => Stats?.Pids is > 0 ? Stats.Pids.Value.ToString() : "—";
    public string NetIoDisplay => string.IsNullOrWhiteSpace(Stats?.NetIO) ? "—" : Stats!.NetIO!;
    public string BlockIoDisplay => string.IsNullOrWhiteSpace(Stats?.BlockIO) ? "—" : Stats!.BlockIO!;

    public string PortsDisplay => PortMappings.Count == 0
        ? "—"
        : string.Join(", ", PortMappings.Select(p => p.ShortDisplay));

    /// <summary>端口一键打开：取第一个可浏览的映射。</summary>
    public string? PrimaryBrowserUrl
        => PortMappings.FirstOrDefault(p => p.IsBrowsable)?.BrowserUrl;

    public bool HasBrowsablePort => PrimaryBrowserUrl != null;

    /// <summary>运行时长（引擎给的 RunningFor 优先）。</summary>
    public string UptimeDisplay => string.IsNullOrWhiteSpace(RunningFor) ? "—" : RunningFor!;
}
