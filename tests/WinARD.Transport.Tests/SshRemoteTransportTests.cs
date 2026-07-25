using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using WinARD.Domain.Connections;
using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class SshRemoteTransportTests
{
    [Fact]
    public async Task Unknown_host_key_is_returned_for_confirmation_before_a_tunnel_is_opened()
    {
        var connector = new FakeSshSessionConnector("ssh-ed25519", [1, 2, 3]);
        var transport = new SshRemoteTransport(connector);
        var profile = CreateProfile(pinnedAlgorithm: null, pinnedFingerprint: null);

        var exception = await Assert.ThrowsAsync<SshHostKeyUnknownException>(
            () => transport.ConnectAsync(profile, CancellationToken.None));

        Assert.Equal(SshHostKeyStatus.Unknown, exception.Verification.Status);
        Assert.Equal(0, connector.OpenCount);
    }

    [Fact]
    public async Task Changed_host_key_is_blocked_before_a_tunnel_is_opened()
    {
        var connector = new FakeSshSessionConnector("ssh-ed25519", [9, 9, 9]);
        var transport = new SshRemoteTransport(connector);
        var profile = CreateProfile(
            "ssh-ed25519",
            SshHostKeyVerifier.ComputeFingerprint([1, 2, 3]));

        await Assert.ThrowsAsync<SshHostKeyChangedException>(
            () => transport.ConnectAsync(profile, CancellationToken.None));

        Assert.Equal(0, connector.OpenCount);
    }

    [Fact]
    public async Task Connector_that_skips_host_key_validation_is_disposed_before_tunnel_creation()
    {
        var connector = new FakeSshSessionConnector("ssh-ed25519", [1, 2, 3])
        {
            SkipHostKeyValidator = true,
        };
        var transport = new SshRemoteTransport(connector);
        var profile = CreateProfile(pinnedAlgorithm: null, pinnedFingerprint: null);

        await Assert.ThrowsAsync<SshHostKeyNotVerifiedException>(
            () => transport.ConnectAsync(profile, CancellationToken.None));

        Assert.Equal(0, connector.OpenCount);
        Assert.Equal(1, connector.SessionDisposeCount);
    }

    [Fact]
    public async Task Connector_that_ignores_unknown_key_rejection_is_disposed_before_tunnel_creation()
    {
        var connector = new FakeSshSessionConnector("ssh-ed25519", [1, 2, 3])
        {
            IgnoreHostKeyRejection = true,
        };
        var transport = new SshRemoteTransport(connector);
        var profile = CreateProfile(pinnedAlgorithm: null, pinnedFingerprint: null);

        var exception = await Assert.ThrowsAsync<SshHostKeyUnknownException>(
            () => transport.ConnectAsync(profile, CancellationToken.None));

        Assert.Equal(SshHostKeyStatus.Unknown, exception.Verification.Status);
        Assert.Equal(0, connector.OpenCount);
        Assert.Equal(1, connector.SessionDisposeCount);
    }

    [Fact]
    public async Task Connector_that_ignores_changed_key_rejection_is_disposed_before_tunnel_creation()
    {
        var connector = new FakeSshSessionConnector("ssh-ed25519", [9, 9, 9])
        {
            IgnoreHostKeyRejection = true,
        };
        var transport = new SshRemoteTransport(connector);
        var profile = CreateProfile(
            "ssh-ed25519",
            SshHostKeyVerifier.ComputeFingerprint([1, 2, 3]));

        await Assert.ThrowsAsync<SshHostKeyChangedException>(
            () => transport.ConnectAsync(profile, CancellationToken.None));

        Assert.Equal(0, connector.OpenCount);
        Assert.Equal(1, connector.SessionDisposeCount);
    }

    [Fact]
    public async Task Matching_pin_opens_the_remote_Rfb_target_and_disposes_all_session_resources_once()
    {
        byte[] hostKey = [4, 5, 6];
        var connector = new FakeSshSessionConnector("SSH-ED25519", hostKey);
        var transport = new SshRemoteTransport(connector);
        var profile = CreateProfile(
            "ssh-ed25519",
            SshHostKeyVerifier.ComputeFingerprint(hostKey));

        var connection = await transport.ConnectAsync(profile, CancellationToken.None);

        Assert.Equal("127.0.0.1", connector.TargetHost);
        Assert.Equal(5900, connector.TargetPort);
        Assert.Equal("127.0.0.1", connection.EndPoint.Host);
        Assert.Equal(5900, connection.EndPoint.Port);

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        Assert.Equal(1, connector.Stream.DisposeCount);
        Assert.Equal(1, connector.SessionDisposeCount);
    }

    [Fact]
    public async Task Tunnel_failure_disposes_the_connected_Ssh_session()
    {
        byte[] hostKey = [7, 8, 9];
        var connector = new FakeSshSessionConnector("ssh-ed25519", hostKey)
        {
            TunnelFailure = new IOException("Tunnel failed."),
        };
        var transport = new SshRemoteTransport(connector);
        var profile = CreateProfile(
            "ssh-ed25519",
            SshHostKeyVerifier.ComputeFingerprint(hostKey));

        await Assert.ThrowsAsync<IOException>(
            () => transport.ConnectAsync(profile, CancellationToken.None));

        Assert.Equal(1, connector.SessionDisposeCount);
    }

    [Fact]
    public async Task Tunnel_and_session_cleanup_failures_are_both_preserved()
    {
        byte[] hostKey = [7, 8, 9];
        var tunnelFailure = new IOException("Tunnel failed.");
        var cleanupFailure = new IOException("Session cleanup failed.");
        var connector = new FakeSshSessionConnector("ssh-ed25519", hostKey)
        {
            TunnelFailure = tunnelFailure,
            SessionDisposeFailure = cleanupFailure,
        };
        var transport = new SshRemoteTransport(connector);
        var profile = CreateProfile(
            "ssh-ed25519",
            SshHostKeyVerifier.ComputeFingerprint(hostKey));

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => transport.ConnectAsync(profile, CancellationToken.None));

        Assert.Collection(
            exception.InnerExceptions,
            item => Assert.Same(tunnelFailure, item),
            item => Assert.Same(cleanupFailure, item));
        Assert.Equal(1, connector.SessionDisposeCount);
    }

    [Fact]
    public async Task Failed_SshNet_connection_disposes_authentication_material()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            await accepted.GetStream().WriteAsync("NOT-SSH\r\n"u8.ToArray());
            accepted.Client.Shutdown(SocketShutdown.Both);
        });
        var method = new TrackingAuthenticationMethod();
        var transport = new SshRemoteTransport(new FixedAuthenticationMethodProvider(method));
        var ssh = SshProfile.Create(
            "127.0.0.1",
            port,
            "ssh-user",
            privateKeyPath: null,
            targetHost: "127.0.0.1",
            targetPort: 5900,
            credentialReference: null,
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null);
        var profile = ConnectionProfile
            .Create(Guid.NewGuid(), "Mac", "ignored.example", 5999, "operator")
            .WithSsh(ssh);

        await Assert.ThrowsAnyAsync<Exception>(
            () => transport.ConnectAsync(profile, CancellationToken.None));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, method.DisposeCount);
    }

    [Fact]
    public async Task ConnectionInfo_construction_failure_disposes_all_obtained_authentication_material()
    {
        var first = new TrackingAuthenticationMethod();
        var second = new TrackingAuthenticationMethod();
        var provider = new FixedAuthenticationMethodProvider([first, null!, second]);
        var transport = new SshRemoteTransport(provider);
        var profile = CreateProfile(pinnedAlgorithm: null, pinnedFingerprint: null);

        await Assert.ThrowsAnyAsync<Exception>(
            () => transport.ConnectAsync(profile, CancellationToken.None));

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task Session_disposal_attempts_every_resource_and_preserves_all_failures()
    {
        var localFailure = new IOException("Local client cleanup failed.");
        var stopFailure = new IOException("Forward stop failed.");
        var forwardDisposeFailure = new IOException("Forward dispose failed.");
        var disconnectFailure = new IOException("SSH disconnect failed.");
        var clientDisposeFailure = new IOException("SSH dispose failed.");
        var authenticationFailure = new IOException("Authentication cleanup failed.");
        var local = new ThrowingLocalClientResource(localFailure);
        var forward = new ThrowingForwardedPortResource(stopFailure, forwardDisposeFailure);
        var client = new ThrowingSshClientResource(disconnectFailure, clientDisposeFailure);
        var authentication = new TrackingAuthenticationMethod(authenticationFailure);
        var session = new SshNetTunnelSession(
            client,
            [authentication],
            new UnusedTunnelResourceFactory(),
            forward,
            local);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => session.DisposeAsync().AsTask());

        Assert.Collection(
            exception.InnerExceptions,
            item => Assert.Same(localFailure, item),
            item => Assert.Same(stopFailure, item),
            item => Assert.Same(forwardDisposeFailure, item),
            item => Assert.Same(disconnectFailure, item),
            item => Assert.Same(clientDisposeFailure, item),
            item => Assert.Same(authenticationFailure, item));
        Assert.Equal(1, local.DisposeCount);
        Assert.Equal(1, forward.StopCount);
        Assert.Equal(1, forward.DisposeCount);
        Assert.Equal(1, client.DisconnectCount);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, authentication.DisposeCount);
    }

    private static ConnectionProfile CreateProfile(
        string? pinnedAlgorithm,
        string? pinnedFingerprint)
    {
        var ssh = SshProfile.Create(
            "jump.example",
            2222,
            "ssh-user",
            privateKeyPath: null,
            targetHost: "127.0.0.1",
            targetPort: 5900,
            credentialReference: null,
            pinnedAlgorithm,
            pinnedFingerprint);

        return ConnectionProfile
            .Create(Guid.NewGuid(), "Mac", "ignored.example", 5999, "operator")
            .WithSsh(ssh);
    }

    private sealed class FakeSshSessionConnector(
        string algorithm,
        byte[] hostKey) : ISshSessionConnector
    {
        public int OpenCount { get; private set; }

        public int SessionDisposeCount { get; private set; }

        public string? TargetHost { get; private set; }

        public int TargetPort { get; private set; }

        public CountingStream Stream { get; } = new();

        public Exception? TunnelFailure { get; init; }

        public Exception? SessionDisposeFailure { get; init; }

        public bool SkipHostKeyValidator { get; init; }

        public bool IgnoreHostKeyRejection { get; init; }

        public Task<ISshTunnelSession> ConnectAsync(
            SshProfile profile,
            Func<SshPresentedHostKey, bool> hostKeyValidator,
            CancellationToken cancellationToken)
        {
            var trusted = SkipHostKeyValidator ||
                hostKeyValidator(new SshPresentedHostKey(algorithm, hostKey));
            if (!trusted && !IgnoreHostKeyRejection)
            {
                throw new IOException("SSH host key was rejected.");
            }

            return Task.FromResult<ISshTunnelSession>(new FakeSession(this));
        }

        private sealed class FakeSession(FakeSshSessionConnector owner) : ISshTunnelSession
        {
            private int _disposed;

            public Task<Stream> OpenDirectTcpipAsync(
                string targetHost,
                int targetPort,
                CancellationToken cancellationToken)
            {
                owner.OpenCount++;
                owner.TargetHost = targetHost;
                owner.TargetPort = targetPort;
                return owner.TunnelFailure is null
                    ? Task.FromResult<Stream>(owner.Stream)
                    : Task.FromException<Stream>(owner.TunnelFailure);
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.SessionDisposeCount++;
                    if (owner.SessionDisposeFailure is not null)
                    {
                        return ValueTask.FromException(owner.SessionDisposeFailure);
                    }
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class CountingStream : MemoryStream
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && DisposeCount == 0)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FixedAuthenticationMethodProvider : ISshAuthenticationMethodProvider
    {
        private readonly IReadOnlyList<AuthenticationMethod> _methods;

        public FixedAuthenticationMethodProvider(AuthenticationMethod method)
            : this([method])
        {
        }

        public FixedAuthenticationMethodProvider(IReadOnlyList<AuthenticationMethod> methods)
        {
            _methods = methods;
        }

        public ValueTask<IReadOnlyList<AuthenticationMethod>> GetAuthenticationMethodsAsync(
            SshProfile profile,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_methods);
    }

    private sealed class TrackingAuthenticationMethod(Exception? disposeFailure = null)
        : AuthenticationMethod("ssh-user"), IDisposable
    {
        public int DisposeCount { get; private set; }

        public override string Name => "tracking";

        public override AuthenticationResult Authenticate(Session session) =>
            AuthenticationResult.Failure;

        public void Dispose()
        {
            DisposeCount++;
            if (disposeFailure is not null)
            {
                throw disposeFailure;
            }
        }
    }

    private sealed class ThrowingLocalClientResource(Exception failure) : ILocalTcpClientResource
    {
        public int DisposeCount { get; private set; }

        public Task ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Stream GetStream() => Stream.Null;

        public void Dispose()
        {
            DisposeCount++;
            throw failure;
        }
    }

    private sealed class ThrowingForwardedPortResource(
        Exception stopFailure,
        Exception disposeFailure) : ISshForwardedPortResource
    {
        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool IsStarted => true;

        public int BoundPort => 12345;

        public void Start()
        {
        }

        public void Stop()
        {
            StopCount++;
            throw stopFailure;
        }

        public void Dispose()
        {
            DisposeCount++;
            throw disposeFailure;
        }
    }

    private sealed class ThrowingSshClientResource(
        Exception disconnectFailure,
        Exception disposeFailure) : ISshClientResource
    {
        public int DisconnectCount { get; private set; }

        public int DisposeCount { get; private set; }

        public void AddForwardedPort(ISshForwardedPortResource forwardedPort)
        {
        }

        public void Disconnect()
        {
            DisconnectCount++;
            throw disconnectFailure;
        }

        public void Dispose()
        {
            DisposeCount++;
            throw disposeFailure;
        }
    }

    private sealed class UnusedTunnelResourceFactory : ISshTunnelResourceFactory
    {
        public ISshForwardedPortResource CreateForwardedPort(string targetHost, int targetPort) =>
            throw new InvalidOperationException("Not used.");

        public ILocalTcpClientResource CreateLocalClient() =>
            throw new InvalidOperationException("Not used.");
    }

}
