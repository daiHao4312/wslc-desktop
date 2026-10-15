using WSLC.Panel.Core.Models;
using WSLC.Panel.Core.Services;

namespace WSLC.Panel.Core.Tests;

public class PreferencesServiceTests
{
    [Fact]
    public async Task TestLoadAndSavePreferences()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"prefs_test_{Guid.NewGuid():N}.json");
        try
        {
            var svc = new PreferencesService(tempFile);
            var initial = await svc.LoadAsync();
            Assert.Equal("Default", initial.Theme);
            Assert.Equal(3, initial.RefreshIntervalSeconds);

            var updated = initial with { Theme = "Dark", RefreshIntervalSeconds = 5 };
            await svc.SaveAsync(updated);

            var svc2 = new PreferencesService(tempFile);
            var loaded = await svc2.LoadAsync();
            Assert.Equal("Dark", loaded.Theme);
            Assert.Equal(5, loaded.RefreshIntervalSeconds);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
