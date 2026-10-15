using System.Text.Json;
using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Models;

namespace WSLC.Panel.Core.Services;

public class PreferencesService : IPrefsService
{
    private readonly string _prefsFilePath;
    private AppPreferences _current;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public PreferencesService(string? customPath = null)
    {
        if (customPath != null)
        {
            _prefsFilePath = customPath;
        }
        else
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(localAppData, "WSLCPanel");
            _prefsFilePath = Path.Combine(dir, "prefs.json");
        }

        // 构造即读盘：偏好文件仅数百字节，同步读取可避免"窗口先显示再回填主题"的闪烁
        _current = LoadFromDisk() ?? new AppPreferences();
    }

    public string PreferencesFilePath => _prefsFilePath;

    public AppPreferences GetPreferences() => _current;

    public async Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppPreferences? loaded = null;
        try
        {
            if (File.Exists(_prefsFilePath))
            {
                var json = await File.ReadAllTextAsync(_prefsFilePath, cancellationToken);
                loaded = JsonSerializer.Deserialize<AppPreferences>(json, JsonOptions);
            }
        }
        catch
        {
            // Fallback to current value
        }

        if (loaded != null)
        {
            _current = loaded;
        }

        return _current;
    }

    public async Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
    {
        _current = preferences;
        try
        {
            var dir = Path.GetDirectoryName(_prefsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_current, JsonOptions);
            await File.WriteAllTextAsync(_prefsFilePath, json, cancellationToken);
        }
        catch
        {
            // Ignore write errors or log
        }
    }

    private AppPreferences? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_prefsFilePath)) return null;
            var json = File.ReadAllText(_prefsFilePath);
            return JsonSerializer.Deserialize<AppPreferences>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
