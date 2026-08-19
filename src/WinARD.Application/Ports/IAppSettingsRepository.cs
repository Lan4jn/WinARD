using WinARD.Domain.Settings;

namespace WinARD.Application.Ports;

public sealed record AppSettingsSnapshot(AppSettings Settings, long Revision);

public interface IAppSettingsRepository : IAsyncDisposable
{
    Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken);

    Task<bool> TryUpdateAsync(
        long expectedRevision,
        AppSettings settings,
        CancellationToken cancellationToken);
}
