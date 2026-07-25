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
            throw new SshHostKeyUnknownException(verification, exception);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and not TransportTimeoutException &&
            verification?.Status == SshHostKeyStatus.Changed)
        {
            throw new SshHostKeyChangedException(sshEndpoint, exception);
        }

        if (verification?.Status != SshHostKeyStatus.Trusted)
        {
            Exception trustFailure = verification?.Status switch
            {
                SshHostKeyStatus.Unknown => new SshHostKeyUnknownException(verification),
                SshHostKeyStatus.Changed => new SshHostKeyChangedException(sshEndpoint),
                _ => new SshHostKeyNotVerifiedException(sshEndpoint),
            };

            var cleanup = new CleanupCollector(trustFailure);
            await cleanup.TryAsync(session.DisposeAsync).ConfigureAwait(false);
            cleanup.ThrowIfAny();
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
        catch (Exception exception)
        {
            var cleanup = new CleanupCollector(exception);
            await cleanup.TryAsync(session.DisposeAsync).ConfigureAwait(false);
            cleanup.ThrowIfAny();
            throw new InvalidOperationException("Unreachable.");
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
        AuthenticationMethod[] ownedAuthenticationMethods = [];
        SshClient? client = null;
        try
        {
            var authenticationMethods = await authenticationMethodProvider
                .GetAuthenticationMethodsAsync(profile, cancellationToken)
                .ConfigureAwait(false);
            if (authenticationMethods.Count == 0)
            {
                throw new InvalidOperationException("At least one SSH authentication method is required.");
            }

            ownedAuthenticationMethods = authenticationMethods.ToArray();
            var connectionInfo = new ConnectionInfo(
                profile.Host,
                profile.Port,
                profile.Username,
                ownedAuthenticationMethods);
            client = new SshClient(connectionInfo);
            client.HostKeyReceived += (_, eventArgs) =>
            {
                eventArgs.CanTrust = hostKeyValidator(
                    new SshPresentedHostKey(eventArgs.HostKeyName, eventArgs.HostKey));
            };

            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new SshNetTunnelSession(
                new SshClientResource(client),
                ownedAuthenticationMethods,
                new SshTunnelResourceFactory());
        }
        catch (Exception exception)
        {
            var cleanup = new CleanupCollector(exception);
            if (client is not null)
            {
                cleanup.Try(() =>
                {
                    if (client.IsConnected)
                    {
                        client.Disconnect();
                    }
                });
                cleanup.Try(client.Dispose);
            }

            AddAuthenticationMethodCleanup(cleanup, ownedAuthenticationMethods);
            cleanup.ThrowIfAny();
            throw new InvalidOperationException("Unreachable.");
        }
    }

    internal static void AddAuthenticationMethodCleanup(
        CleanupCollector cleanup,
        IEnumerable<AuthenticationMethod> authenticationMethods)
    {
        var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var method in authenticationMethods)
        {
            if (method is IDisposable disposable && disposed.Add(method))
            {
                cleanup.Try(disposable.Dispose);
            }
        }
    }
}

internal sealed class SshNetTunnelSession(
    ISshClientResource client,
    IReadOnlyList<AuthenticationMethod> authenticationMethods,
    ISshTunnelResourceFactory resourceFactory,
    ISshForwardedPortResource? forwardedPort = null,
    ILocalTcpClientResource? localClient = null) : ISshTunnelSession
{
    private readonly object _sync = new();
    private ISshForwardedPortResource? _forwardedPort = forwardedPort;
    private ILocalTcpClientResource? _localClient = localClient;
    private int _disposed;

    public async Task<Stream> OpenDirectTcpipAsync(
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        ISshForwardedPortResource? newForwardedPort = null;
        ILocalTcpClientResource? newLocalClient = null;
        try
        {
            newForwardedPort = resourceFactory.CreateForwardedPort(targetHost, targetPort);
            newLocalClient = resourceFactory.CreateLocalClient();
            client.AddForwardedPort(newForwardedPort);
            newForwardedPort.Start();
            await newLocalClient
                .ConnectAsync(
                    IPAddress.Loopback.ToString(),
                    newForwardedPort.BoundPort,
                    cancellationToken)
                .ConfigureAwait(false);
            var stream = newLocalClient.GetStream();
            lock (_sync)
            {
                _forwardedPort = newForwardedPort;
                _localClient = newLocalClient;
            }

            return stream;
        }
        catch (Exception exception)
        {
            var cleanup = new CleanupCollector(exception);
            if (newLocalClient is not null)
            {
                cleanup.Try(newLocalClient.Dispose);
            }

            if (newForwardedPort is not null)
            {
                cleanup.Try(() =>
                {
                    if (newForwardedPort.IsStarted)
                    {
                        newForwardedPort.Stop();
                    }
                });
                cleanup.Try(newForwardedPort.Dispose);
            }

            cleanup.ThrowIfAny();
            throw new InvalidOperationException("Unreachable.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        ISshForwardedPortResource? ownedForwardedPort;
        ILocalTcpClientResource? ownedLocalClient;
        lock (_sync)
        {
            ownedForwardedPort = _forwardedPort;
            ownedLocalClient = _localClient;
            _forwardedPort = null;
            _localClient = null;
        }

        var cleanup = new CleanupCollector();
        if (ownedLocalClient is not null)
        {
            cleanup.Try(ownedLocalClient.Dispose);
        }

        if (ownedForwardedPort is not null)
        {
            cleanup.Try(() =>
            {
                if (ownedForwardedPort.IsStarted)
                {
                    ownedForwardedPort.Stop();
                }
            });
            cleanup.Try(ownedForwardedPort.Dispose);
        }

        cleanup.Try(client.Disconnect);
        cleanup.Try(client.Dispose);
        SshNetSessionConnector.AddAuthenticationMethodCleanup(cleanup, authenticationMethods);
        cleanup.ThrowIfAny();
        return ValueTask.CompletedTask;
    }
}

internal interface ISshClientResource : IDisposable
{
    void AddForwardedPort(ISshForwardedPortResource forwardedPort);

    void Disconnect();
}

internal interface ISshForwardedPortResource : IDisposable
{
    bool IsStarted { get; }

    int BoundPort { get; }

    void Start();

    void Stop();
}

internal interface ILocalTcpClientResource : IDisposable
{
    Task ConnectAsync(string host, int port, CancellationToken cancellationToken);

    Stream GetStream();
}

internal interface ISshTunnelResourceFactory
{
    ISshForwardedPortResource CreateForwardedPort(string targetHost, int targetPort);

    ILocalTcpClientResource CreateLocalClient();
}

internal sealed class SshClientResource(SshClient client) : ISshClientResource
{
    public void AddForwardedPort(ISshForwardedPortResource forwardedPort)
    {
        if (forwardedPort is not SshForwardedPortResource sshForwardedPort)
        {
            throw new ArgumentException("Unsupported SSH forwarded port resource.", nameof(forwardedPort));
        }

        client.AddForwardedPort(sshForwardedPort.ForwardedPort);
    }

    public void Disconnect()
    {
        if (client.IsConnected)
        {
            client.Disconnect();
        }
    }

    public void Dispose() => client.Dispose();
}

internal sealed class SshForwardedPortResource(ForwardedPortLocal forwardedPort)
    : ISshForwardedPortResource
{
    internal ForwardedPortLocal ForwardedPort => forwardedPort;

    public bool IsStarted => forwardedPort.IsStarted;

    public int BoundPort => checked((int)forwardedPort.BoundPort);

    public void Start() => forwardedPort.Start();

    public void Stop() => forwardedPort.Stop();

    public void Dispose() => forwardedPort.Dispose();
}

internal sealed class LocalTcpClientResource(TcpClient client) : ILocalTcpClientResource
{
    public async Task ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken) =>
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);

    public Stream GetStream() => client.GetStream();

    public void Dispose() => client.Dispose();
}

internal sealed class SshTunnelResourceFactory : ISshTunnelResourceFactory
{
    public ISshForwardedPortResource CreateForwardedPort(string targetHost, int targetPort) =>
        new SshForwardedPortResource(
            new ForwardedPortLocal(
                IPAddress.Loopback.ToString(),
                0,
                targetHost,
                checked((uint)targetPort)));

    public ILocalTcpClientResource CreateLocalClient() =>
        new LocalTcpClientResource(new TcpClient(AddressFamily.InterNetwork));
}

internal sealed class CleanupCollector
{
    private readonly List<Exception> _exceptions = [];

    public CleanupCollector(Exception? primaryException = null)
    {
        if (primaryException is not null)
        {
            Add(primaryException);
        }
    }

    public void Try(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            Add(exception);
        }
    }

    public async ValueTask TryAsync(Func<ValueTask> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Add(exception);
        }
    }

    private void Add(Exception exception)
    {
        if (exception is AggregateException aggregateException)
        {
            _exceptions.AddRange(aggregateException.Flatten().InnerExceptions);
        }
        else
        {
            _exceptions.Add(exception);
        }
    }

    public void ThrowIfAny()
    {
        if (_exceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(_exceptions[0])
                .Throw();
        }

        if (_exceptions.Count > 1)
        {
            throw new AggregateException(_exceptions);
        }
    }
}

public sealed class SshHostKeyUnknownException : Exception
{
    public SshHostKeyUnknownException(SshHostKeyVerification verification)
        : this(verification, innerException: null)
    {
    }

    public SshHostKeyUnknownException(
        SshHostKeyVerification verification,
        Exception? innerException)
        : base(
            $"The SSH host key for {verification.Endpoint.Host}:{verification.Endpoint.Port} is not pinned.",
            innerException)
    {
        Verification = verification ?? throw new ArgumentNullException(nameof(verification));
    }

    public SshHostKeyVerification Verification { get; }
}

public sealed class SshHostKeyNotVerifiedException : Exception
{
    public SshHostKeyNotVerifiedException(SshHostKeyEndpoint endpoint)
        : base($"The SSH host key for {endpoint.Host}:{endpoint.Port} was not verified.")
    {
        Endpoint = endpoint;
    }

    public SshHostKeyEndpoint Endpoint { get; }
}
