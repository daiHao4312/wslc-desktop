namespace WSLC.Panel.Core.Models;

public record ContainerStatsModel
{
    public string ContainerId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double CpuPercent { get; init; }
    public string CpuPercentRaw { get; init; } = "0.00%";
    public string MemoryUsage { get; init; } = string.Empty;
    public double MemoryPercent { get; init; }
    public string MemoryPercentRaw { get; init; } = "0.00%";
    public string NetIO { get; init; } = string.Empty;
    public string BlockIO { get; init; } = string.Empty;
    public int? Pids { get; init; }
}
