using WinARD.Domain.Connections;
using WinARD.Application.Sessions;
using WinARD.Domain.Errors;

namespace WinARD.Desktop.Services;

public interface IReconnectProfileCapture
{
    void Capture(ConnectionProfile profile);
}

internal sealed class RemoteSessionReconnectRequest(
    ConnectionProfile initialProfile,
    Func<Guid, CancellationToken, Task<ConnectionProfile?>> reloadProfile,
    Func<ConnectionProfile, CancellationToken, Task> reconnect,
    Func<ConnectionProfile, CancellationToken, Task>? reconnectWithoutReservation = null) : IReconnectProfileCapture
{
    private ConnectionProfile _profile = initialProfile ?? throw new ArgumentNullException(nameof(initialProfile));
    private readonly Func<Guid, CancellationToken, Task<ConnectionProfile?>> _reloadProfile = reloadProfile ??
        throw new ArgumentNullException(nameof(reloadProfile));
    private readonly Func<ConnectionProfile, CancellationToken, Task> _reconnect = reconnect ??
        throw new ArgumentNullException(nameof(reconnect));
    private readonly Func<ConnectionProfile, CancellationToken, Task> _reconnectWithoutReservation =
        reconnectWithoutReservation ?? reconnect;

    public void Capture(ConnectionProfile profile) =>
        Volatile.Write(ref _profile, profile ?? throw new ArgumentNullException(nameof(profile)));

    public Task InvokeAsync(CancellationToken token) => InvokeAsync(_reconnect, token);

    public Task InvokeWithoutReservationAsync(CancellationToken token) =>
        InvokeAsync(_reconnectWithoutReservation, token);

    private async Task InvokeAsync(
        Func<ConnectionProfile, CancellationToken, Task> reconnect,
        CancellationToken cancellationToken)
    {
        var desired = Volatile.Read(ref _profile);
        ConnectionProfile? persisted;
        try
        {
            persisted = await _reloadProfile(desired.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw ProfileUnavailable();
        }
        if (persisted is null)
        {
            throw ProfileUnavailable();
        }

        await reconnect(persisted.WithQualityProfile(desired.Quality), cancellationToken)
            .ConfigureAwait(false);
    }

    private static ReconnectFailureException ProfileUnavailable() => new(WinArdError.Create(
        ConnectionStage.Connecting,
        "RECONNECT_PROFILE_UNAVAILABLE",
        "设备配置已删除或无法读取，已停止重新连接。",
        Guid.NewGuid().ToString("N")));
}
