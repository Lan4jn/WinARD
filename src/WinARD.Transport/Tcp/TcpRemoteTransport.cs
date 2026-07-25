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
    private readonly IHostAddressResolver _resolver;
    private readonly ITcpClientConnector _connector;

    public TcpRemoteTransport()
        : this(
            TransportTimeouts.Default,
            TimeProvider.System,
            new SystemHostAddressResolver(),
            new SystemTcpClientConnector())
    {
    }

    internal TcpRemoteTransport(
        TransportTimeouts timeouts,
        TimeProvider timeProvider,
        IHostAddressResolver resolver,
        ITcpClientConnector connector)
    {
        _timeouts = timeouts ?? throw new ArgumentNullException(nameof(timeouts));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    public async Task<TransportConnection> ConnectAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var host = NormalizeHost(profile.Host);
        var addresses = IPAddress.TryParse(host, out var literalAddress)
            ? [literalAddress]
            : await ResolveAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var client = await ConnectAnyAsync(addresses, profile.Port, cancellationToken).ConfigureAwait(false);
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

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken callerToken)
    {
        using var deadline = new CancellationTokenSource(_timeouts.DnsResolution, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        try
        {
            return await _resolver.ResolveAsync(host, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerToken);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TransportTimeoutException(TransportTimeoutStage.DnsResolution);
        }
    }

    private async Task<TcpClient> ConnectAnyAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken callerToken)
    {
        using var deadline = new CancellationTokenSource(_timeouts.Connection, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        Exception? lastFailure = null;

        foreach (var address in addresses)
        {
            try
            {
                return await _connector.ConnectAsync(address, port, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(callerToken);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new TransportTimeoutException(TransportTimeoutStage.Connection);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastFailure = exception;
            }
        }

        throw lastFailure ?? new SocketException((int)SocketError.NotConnected);
    }

    private static string NormalizeHost(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1];
        }

        if (IPAddress.TryParse(trimmed, out _))
        {
            return trimmed;
        }

        var asciiHost = new IdnMapping().GetAscii(trimmed.TrimEnd('.'));
        if (Uri.CheckHostName(asciiHost) != UriHostNameType.Dns)
        {
            throw new ArgumentException("Connection host must be a valid DNS name or IP address.", nameof(host));
        }

        return asciiHost;
    }
}

internal interface IHostAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class SystemHostAddressResolver : IHostAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

internal interface ITcpClientConnector
{
    Task<TcpClient> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken);
}

internal sealed class SystemTcpClientConnector : ITcpClientConnector
{
    public async Task<TcpClient> ConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient(address.AddressFamily);
        try
        {
            await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
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
