namespace WslcDesktop.Core.Models;

/// <summary>本机 WSL 发行版（wsl.exe 侧，与 wslc 容器会话是两套东西）。</summary>
public record DistroModel
{
    public string Name { get; init; } = string.Empty;

    /// <summary>原始状态：Running / Stopped / Installing / Converting / Uninstalling …</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>WSL 版本：1 或 2。</summary>
    public int Version { get; init; }

    public bool IsDefault { get; init; }

    /// <summary>安装目录（注册表 Lxss\BasePath）。</summary>
    public string BasePath { get; init; } = string.Empty;

    public string VhdFileName { get; init; } = "ext4.vhdx";

    /// <summary>VHDX 实际文件大小（读盘得到，读不到为 null）。</summary>
    public long? VhdSizeBytes { get; init; }

    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);

    public string StateDisplay => State.ToLowerInvariant() switch
    {
        "running" => "运行中",
        "stopped" => "已停止",
        "installing" => "安装中",
        "uninstalling" => "卸载中",
        "converting" => "转换中",
        "exporting" => "导出中",
        "needreboot" => "需重启",
        _ => string.IsNullOrEmpty(State) ? "未知" : State
    };

    public string VersionDisplay => Version <= 0 ? "未知" : $"WSL {Version}";

    public string VhdSizeDisplay => VhdSizeBytes switch
    {
        null or <= 0 => "未知",
        < 1024L * 1024 * 1024 => $"{VhdSizeBytes.Value / (1024.0 * 1024.0):F0} MB",
        _ => $"{VhdSizeBytes.Value / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };

    /// <summary>VHDX 完整路径（读大小与"清理磁盘"提示用）。</summary>
    public string VhdPath => string.IsNullOrEmpty(BasePath) ? string.Empty : Path.Combine(BasePath, VhdFileName);
}
