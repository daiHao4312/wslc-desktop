using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

public interface IPrefsService
{
    AppPreferences GetPreferences();
    Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default);
}
