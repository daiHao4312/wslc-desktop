using WslcDesktop.Core.Abstractions;

namespace WslcDesktop.Core.Services;

/// <summary>
/// wslc settings.yaml 读写（带时间戳备份）。
/// 覆盖前先备份是硬要求：settings.yaml 被写坏会让整个容器引擎起不来。
/// </summary>
public class WslcSettingsService : IWslcSettingsService
{
    /// <summary>备份保留份数：超过的从旧到新删除，避免每次保存堆积一个文件。</summary>
    private const int MaxBackups = 10;

    private readonly string _settingsFilePath;

    public WslcSettingsService(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _settingsFilePath = customPath;
            return;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsFilePath = Path.Combine(localAppData, "wslc", "settings.yaml");
    }

    public string SettingsFilePath => _settingsFilePath;

    public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_settingsFilePath)) return string.Empty;
            return await File.ReadAllTextAsync(_settingsFilePath, cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<string> SaveAsync(string yaml, CancellationToken cancellationToken = default)
    {
        var backupPath = string.Empty;

        try
        {
            var dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(_settingsFilePath))
            {
                backupPath = $"{_settingsFilePath}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(_settingsFilePath, backupPath, overwrite: true);
            }

            // 原子写：先写临时文件再替换。直接写目标文件的话，写盘中途崩溃会留下
            // 损坏的 settings.yaml——引擎会因此起不来
            var tempPath = _settingsFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, yaml ?? string.Empty, cancellationToken);

            if (File.Exists(_settingsFilePath))
            {
                File.Replace(tempPath, _settingsFilePath, null);
            }
            else
            {
                File.Move(tempPath, _settingsFilePath, overwrite: true);
            }
        }
        catch
        {
            // 写入失败向上抛出由界面提示，这里只保证备份已尽力完成
            throw;
        }
        finally
        {
            CleanupOldBackups();
        }

        return backupPath;
    }

    /// <summary>只保留最近 <see cref="MaxBackups"/> 份备份，其余删除。</summary>
    private void CleanupOldBackups()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsFilePath);
            if (string.IsNullOrEmpty(dir)) return;

            var prefix = Path.GetFileName(_settingsFilePath) + ".bak-";
            var stale = Directory
                .EnumerateFiles(dir, prefix + "*")
                .OrderByDescending(f => f)
                .Skip(MaxBackups);

            foreach (var file in stale)
            {
                File.Delete(file);
            }
        }
        catch
        {
            // 备份清理失败不影响主流程
        }
    }

    public IReadOnlyList<string> GetBackups()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsFilePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return Array.Empty<string>();

            var prefix = Path.GetFileName(_settingsFilePath) + ".bak-";
            return Directory
                .EnumerateFiles(dir, Path.GetFileName(_settingsFilePath) + ".bak-*")
                .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
