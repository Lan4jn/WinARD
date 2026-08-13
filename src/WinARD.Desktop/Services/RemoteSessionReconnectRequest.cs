using WinARD.Domain.Connections;

namespace WinARD.Desktop.Services;

internal interface IReconnectProfileCapture
{
    void Capture(ConnectionProfile profile);
}

internal sealed class RemoteSessionReconnectRequest(
    ConnectionProfile initialProfile,
    Func<ConnectionProfile, CancellationToken, Task> reconnect) : IReconnectProfileCapture
{
    private ConnectionProfile _profile = initialProfile ?? throw new ArgumentNullException(nameof(initialProfile));
    private readonly Func<ConnectionProfile, CancellationToken, Task> _reconnect = reconnect ??
        throw new ArgumentNullException(nameof(reconnect));

    public void Capture(ConnectionProfile profile) =>
        Volatile.Write(ref _profile, profile ?? throw new ArgumentNullException(nameof(profile)));

    public Task InvokeAsync(CancellationToken token) =>
        _reconnect(Volatile.Read(ref _profile), token);
}
