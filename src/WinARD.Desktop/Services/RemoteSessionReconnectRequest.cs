using WinARD.Domain.Connections;

namespace WinARD.Desktop.Services;

public interface IReconnectProfileCapture
{
    void Capture(ConnectionProfile profile);
}

internal sealed class RemoteSessionReconnectRequest(
    ConnectionProfile initialProfile,
    Func<ConnectionProfile, CancellationToken, Task> reconnect,
    Func<ConnectionProfile, CancellationToken, Task>? reconnectWithoutReservation = null) : IReconnectProfileCapture
{
    private ConnectionProfile _profile = initialProfile ?? throw new ArgumentNullException(nameof(initialProfile));
    private readonly Func<ConnectionProfile, CancellationToken, Task> _reconnect = reconnect ??
        throw new ArgumentNullException(nameof(reconnect));
    private readonly Func<ConnectionProfile, CancellationToken, Task> _reconnectWithoutReservation =
        reconnectWithoutReservation ?? reconnect;

    public void Capture(ConnectionProfile profile) =>
        Volatile.Write(ref _profile, profile ?? throw new ArgumentNullException(nameof(profile)));

    public Task InvokeAsync(CancellationToken token) =>
        _reconnect(Volatile.Read(ref _profile), token);

    public Task InvokeWithoutReservationAsync(CancellationToken token) =>
        _reconnectWithoutReservation(Volatile.Read(ref _profile), token);
}
