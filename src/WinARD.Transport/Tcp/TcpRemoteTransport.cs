using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WinARD.Application.Ports;
using WinARD.Domain.Connections;

namespace WinARD.Transport.Tcp;

public sealed class TcpRemoteTransport : IRemoteTransportFactory
{
    private readonly TransportTimeouts _timeouts;
    private readonly TimeProvider _timeProvider;
    private readonly ITcpClientConnector _connector;

    public TcpRemoteTransport()
        : this(
            TransportTimeouts.Default,
            TimeProvider.System,
            new SystemTcpClientConnector())
    {
    }

    internal TcpRemoteTransport(
        TransportTimeouts timeouts,
        TimeProvider timeProvider,
        ITcpClientConnector connector)
    {
        _timeouts = timeouts ?? throw new ArgumentNullException(nameof(timeouts));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    public async Task<TransportConnection> ConnectAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var host = NormalizeHost(profile.Host);
        var client = await ConnectAsync(host, profile.Port, cancellationToken).ConfigureAwait(false);
        try
        {
            return new TransportConnection(
                client.GetStream(),
                new EndPointDescription(profile.Host, profile.Port),
                new TcpClientLifetime(client));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<TcpClient> ConnectAsync(
        string host,
        int port,
        CancellationToken callerToken)
    {
        using var deadline = new CancellationTokenSource(_timeouts.Connection, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        try
        {
            return await _connector.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerToken);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TransportTimeoutException(TransportTimeoutStage.Connection);
        }
    }

    private static string NormalizeHost(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1];
        }

        if (IPAddress.TryParse(trimmed, out var address))
        {
            return address.ToString();
        }

        var asciiHost = new IdnMapping().GetAscii(trimmed.TrimEnd('.'));
        if (Uri.CheckHostName(asciiHost) != UriHostNameType.Dns)
        {
            throw new ArgumentException("Connection host must be a valid DNS name or IP address.", nameof(host));
        }

        return asciiHost;
    }
}

internal interface ITcpClientConnector
{
    Task<TcpClient> ConnectAsync(string host, int port, CancellationToken cancellationToken);
}

internal sealed class SystemTcpClientConnector : ITcpClientConnector
{
    public async Task<TcpClient> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

internal sealed class TcpClientLifetime(TcpClient client) : IAsyncDisposable
{
    private int _disposed;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            client.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
