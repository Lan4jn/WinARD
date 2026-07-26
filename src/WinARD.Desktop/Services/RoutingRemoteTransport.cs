using WinARD.Application.Ports;
using WinARD.Domain.Connections;
using WinARD.Transport.Ssh;
using WinARD.Transport.Tcp;

namespace WinARD.Desktop.Services;

public sealed class RoutingRemoteTransport : IRemoteTransportFactory
{
    private readonly TcpRemoteTransport _tcp = new();
    private readonly SshRemoteTransport _ssh;
    private readonly InMemorySshHostKeyPinStore _pins;

    public RoutingRemoteTransport(ICredentialStore credentialStore)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        _pins = new InMemorySshHostKeyPinStore();
        _ssh = new SshRemoteTransport(_pins, credentialStore);
    }

    public async Task<TransportConnection> ConnectAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.TransportMode != TransportMode.Ssh)
        {
            return await _tcp.ConnectAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        return await _ssh.ConnectAsync(profile, cancellationToken).ConfigureAwait(false);
    }
}
