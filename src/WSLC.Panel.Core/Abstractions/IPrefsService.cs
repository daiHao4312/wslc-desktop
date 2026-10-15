using WSLC.Panel.Core.Models;

namespace WSLC.Panel.Core.Abstractions;

public interface IPrefsService
{
    AppPreferences GetPreferences();
    Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default);
}
