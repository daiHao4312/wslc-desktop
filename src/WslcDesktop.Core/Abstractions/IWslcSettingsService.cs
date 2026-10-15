using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

/// <summary>wslc 引擎配置文件（settings.yaml）的读写。与面板自身的偏好文件严格分开。</summary>
public interface IWslcSettingsService
{
    /// <summary>配置文件路径（通常 %LOCALAPPDATA%\wslc\settings.yaml）。</summary>
    string SettingsFilePath { get; }

    Task<string> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 覆盖写入。写入前会先备份为 <c>settings.yaml.bak-yyyyMMdd-HHmmss</c>，返回备份文件路径。
    /// </summary>
    Task<string> SaveAsync(string yaml, CancellationToken cancellationToken = default);

    /// <summary>列出已有备份（新→旧）。</summary>
    IReadOnlyList<string> GetBackups();
}
