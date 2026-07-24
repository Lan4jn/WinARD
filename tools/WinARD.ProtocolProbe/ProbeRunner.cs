using System.Net.Sockets;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Handshake;

namespace WinARD.ProtocolProbe;

public sealed class ProbeRunner
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _operationTimeout;

    public ProbeRunner()
        : this(DefaultOperationTimeout)
    {
    }

    public ProbeRunner(TimeSpan operationTimeout)
    {
        if (operationTimeout <= TimeSpan.Zero || operationTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        _operationTimeout = operationTimeout;
    }

    public async Task<ProbeResult> RunAsync(
        string host,
        int port,
        ISecretMaterial username,
        ISecretMaterial password,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(_operationTimeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, operationCancellation.Token);
            await using var stream = client.GetStream();
            var handshake = await RfbHandshake.NegotiateAsync(stream, operationCancellation.Token);
            if (handshake.SecurityType != RfbSecurityType.AppleRemoteDesktop)
            {
                throw new RfbProtocolException("The server did not negotiate Apple Remote Desktop security type 30.");
            }

            await new ArdAuthenticator().AuthenticateAsync(
                stream,
                handshake.Version,
                username,
                password,
                operationCancellation.Token);
            return new ProbeResult(handshake.Version, handshake.SecurityType);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && operationCancellation.IsCancellationRequested)
        {
            throw new ProbeTimeoutException(exception);
        }
    }
}
