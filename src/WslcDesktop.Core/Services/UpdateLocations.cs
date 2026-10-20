namespace WslcDesktop.Core.Services;

/// <summary>
/// 在线更新涉及的本机落点（集中一处，避免"下载到 A、清理 B"这类漂移）。
///
/// 数据目录与面板偏好一致：<c>%LOCALAPPDATA%\WslcDesktop\</c>。
/// 不放进 exe 同目录，是因为 exe 可能位于只读位置（Program Files），
/// 而下更新包这件事本身不该因为"装在哪"而失败。
/// </summary>
public static class UpdateLocations
{
    /// <summary>应用数据目录。</summary>
    public static string DataDirectory(string? localAppDataOverride = null)
    {
        var localAppData = string.IsNullOrWhiteSpace(localAppDataOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataOverride;

        return Path.Combine(localAppData, "WslcDesktop");
    }

    /// <summary>更新包存放目录（下载落点，也是辅助进程的启动位置）。</summary>
    public static string UpdatesDirectory(string? localAppDataOverride = null)
        => Path.Combine(DataDirectory(localAppDataOverride), "update");

    /// <summary>更新过程日志（辅助进程无界面，出问题只能靠它回溯）。</summary>
    public static string LogPath(string? localAppDataOverride = null)
        => Path.Combine(DataDirectory(localAppDataOverride), "update.log");
}
