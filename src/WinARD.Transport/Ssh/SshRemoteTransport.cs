using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using WinARD.Application.Ports;
using WinARD.Domain.Connections;

namespace WinARD.Transport.Ssh;

public sealed class SshRemoteTransport : IRemoteTransportFactory
{
    private readonly ISshSessionConnector _connector;
    private readonly TransportTimeouts _timeouts;
    private readonly TimeProvider _timeProvider;

    public SshRemoteTransport(
        ISshAuthenticationMethodProvider authenticationMethodProvider,
        TransportTimeouts? timeouts = null,
        TimeProvider? timeProvider = null)
        : this(
            new SshNetSessionConnector(authenticationMethodProvider),
            timeouts ?? TransportTimeouts.Default,
            timeProvider ?? TimeProvider.System)
    {
    }

    internal SshRemoteTransport(ISshSessionConnector connector)
        : this(connector, TransportTimeouts.Default, TimeProvider.System)
    {
    }

    internal SshRemoteTransport(
        ISshSessionConnector connector,
        TransportTimeouts timeouts,
        TimeProvider timeProvider)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _timeouts = timeouts ?? throw new ArgumentNullException(nameof(timeouts));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<TransportConnection> ConnectAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var sshProfile = profile.SshProfile ??
            throw new ArgumentException("An SSH transport requires an SSH profile.", nameof(profile));
        var sshEndpoint = new SshHostKeyEndpoint(sshProfile.Host, sshProfile.Port);
        var pin = CreatePin(sshProfile, sshEndpoint);
        SshHostKeyVerification? verification = null;

        bool ValidateHostKey(SshPresentedHostKey presented)
        {
            verification = SshHostKeyVerifier.Verify(
                sshEndpoint,
                presented.Algorithm,
                presented.KeyBytes.Span,
                pin);
            return verification.Status == SshHostKeyStatus.Trusted;
        }

        ISshTunnelSession session;
        try
        {
            session = await WithConnectionDeadlineAsync(
                token => _connector.ConnectAsync(sshProfile, ValidateHostKey, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and not TransportTimeoutException &&
            verification?.Status == SshHostKeyStatus.Unknown)
        {
            throw new SshHostKeyUnknownException(verification);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and not TransportTimeoutException &&
            verification?.Status == SshHostKeyStatus.Changed)
        {
            throw new SshHostKeyChangedException(sshEndpoint);
        }

        try
        {
            var stream = await WithConnectionDeadlineAsync(
                token => session.OpenDirectTcpipAsync(
                    sshProfile.TargetHost,
                    sshProfile.TargetPort,
                    token),
                cancellationToken).ConfigureAwait(false);
            return new TransportConnection(
                stream,
                new EndPointDescription(sshProfile.TargetHost, sshProfile.TargetPort),
                session);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<T> WithConnectionDeadlineAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken callerToken)
    {
        using var deadline = new CancellationTokenSource(_timeouts.Connection, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        try
        {
            return await operation(linked.Token).ConfigureAwait(false);
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

    private static SshHostKeyPin? CreatePin(
        SshProfile profile,
        SshHostKeyEndpoint endpoint) =>
        profile.PinnedHostKeyAlgorithm is null
            ? null
            : new SshHostKeyPin(
                endpoint,
                profile.PinnedHostKeyAlgorithm,
                profile.PinnedHostKeySha256!);
}

public interface ISshAuthenticationMethodProvider
{
    ValueTask<IReadOnlyList<AuthenticationMethod>> GetAuthenticationMethodsAsync(
        SshProfile profile,
        CancellationToken cancellationToken);
}

internal readonly record struct SshPresentedHostKey(
    string Algorithm,
    ReadOnlyMemory<byte> KeyBytes);

internal interface ISshSessionConnector
{
    Task<ISshTunnelSession> ConnectAsync(
        SshProfile profile,
        Func<SshPresentedHostKey, bool> hostKeyValidator,
        CancellationToken cancellationToken);
}

internal interface ISshTunnelSession : IAsyncDisposable
{
    Task<Stream> OpenDirectTcpipAsync(
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken);
}

internal sealed class SshNetSessionConnector(
    ISshAuthenticationMethodProvider authenticationMethodProvider) : ISshSessionConnector
{
    public async Task<ISshTunnelSession> ConnectAsync(
        SshProfile profile,
        Func<SshPresentedHostKey, bool> hostKeyValidator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationMethodProvider);
        var authenticationMethods = await authenticationMethodProvider
            .GetAuthenticationMethodsAsync(profile, cancellationToken)
            .ConfigureAwait(false);
        if (authenticationMethods.Count == 0)
        {
            throw new InvalidOperationException("At least one SSH authentication method is required.");
        }

        var ownedAuthenticationMethods = authenticationMethods.ToArray();
        var connectionInfo = new ConnectionInfo(
            profile.Host,
            profile.Port,
            profile.Username,
            ownedAuthenticationMethods);
        var client = new SshClient(connectionInfo);
        client.HostKeyReceived += (_, eventArgs) =>
        {
            eventArgs.CanTrust = hostKeyValidator(
                new SshPresentedHostKey(eventArgs.HostKeyName, eventArgs.HostKey));
        };

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new SshNetTunnelSession(client, ownedAuthenticationMethods);
        }
        catch
        {
            try
            {
                client.Dispose();
            }
            finally
            {
                DisposeAuthenticationMethods(ownedAuthenticationMethods);
            }

            throw;
        }
    }

    internal static void DisposeAuthenticationMethods(
        IEnumerable<AuthenticationMethod> authenticationMethods)
    {
        var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var method in authenticationMethods)
        {
            if (method is IDisposable disposable && disposed.Add(method))
            {
                disposable.Dispose();
            }
        }
    }
}

internal sealed class SshNetTunnelSession(
    SshClient client,
    IReadOnlyList<AuthenticationMethod> authenticationMethods) : ISshTunnelSession
{
    private readonly object _sync = new();
    private ForwardedPortLocal? _forwardedPort;
    private TcpClient? _localClient;
    private int _disposed;

    public async Task<Stream> OpenDirectTcpipAsync(
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        var forwardedPort = new ForwardedPortLocal(
            IPAddress.Loopback.ToString(),
            0,
            targetHost,
            checked((uint)targetPort));
        var localClient = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            client.AddForwardedPort(forwardedPort);
            forwardedPort.Start();
            await localClient
                .ConnectAsync(IPAddress.Loopback, checked((int)forwardedPort.BoundPort), cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _forwardedPort = forwardedPort;
                _localClient = localClient;
            }

            return localClient.GetStream();
        }
        catch
        {
            localClient.Dispose();
            if (forwardedPort.IsStarted)
            {
                forwardedPort.Stop();
            }

            forwardedPort.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        ForwardedPortLocal? forwardedPort;
        TcpClient? localClient;
        lock (_sync)
        {
            forwardedPort = _forwardedPort;
            localClient = _localClient;
            _forwardedPort = null;
            _localClient = null;
        }

        localClient?.Dispose();
        if (forwardedPort?.IsStarted == true)
        {
            forwardedPort.Stop();
        }

        forwardedPort?.Dispose();
        try
        {
            client.Dispose();
        }
        finally
        {
            SshNetSessionConnector.DisposeAuthenticationMethods(authenticationMethods);
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class SshHostKeyUnknownException : Exception
{
    public SshHostKeyUnknownException(SshHostKeyVerification verification)
        : base($"The SSH host key for {verification.Endpoint.Host}:{verification.Endpoint.Port} is not pinned.")
    {
        Verification = verification ?? throw new ArgumentNullException(nameof(verification));
    }

    public SshHostKeyVerification Verification { get; }
}
