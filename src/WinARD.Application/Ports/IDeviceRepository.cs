using WinARD.Domain.Connections;

namespace WinARD.Application.Ports;

public interface IDeviceRepository : IAsyncDisposable
{
    Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken);

    Task<ConnectionProfile> UpdateHostKeyPinAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);

    Task<ConnectionProfile> UpdateQualityProfileAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);

    Task<ConnectionProfile> UpdateFrameRefreshPolicyAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);

    Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
