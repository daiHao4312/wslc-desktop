namespace WslcDesktop.Core.Models;

public record SystemInfoModel
{
    public string WslVersion { get; init; } = string.Empty;
    public string KernelVersion { get; init; } = string.Empty;
    public string Direct3DVersion { get; init; } = string.Empty;
    public string DxCoreVersion { get; init; } = string.Empty;
    public string WindowsVersion { get; init; } = string.Empty;
    public string SettingsFilePath { get; init; } = string.Empty;
    public string SessionManagerVersion { get; init; } = string.Empty;
    public IReadOnlyList<SessionModel> ActiveSessions { get; init; } = Array.Empty<SessionModel>();
    public IReadOnlyList<string> MissingComponents { get; init; } = Array.Empty<string>();

    public bool IsWslcReady => MissingComponents.Count == 0 && !string.IsNullOrEmpty(WslVersion);
}
