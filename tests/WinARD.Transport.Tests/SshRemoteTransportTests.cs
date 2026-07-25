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
    public async Task Failed_SshNet_connection_disposes_authentication_material()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var unavailablePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var method = new TrackingAuthenticationMethod();
        var transport = new SshRemoteTransport(new FixedAuthenticationMethodProvider(method));
        var ssh = SshProfile.Create(
            "127.0.0.1",
            unavailablePort,
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

        Assert.Equal(1, method.DisposeCount);
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

        public Task<ISshTunnelSession> ConnectAsync(
            SshProfile profile,
            Func<SshPresentedHostKey, bool> hostKeyValidator,
            CancellationToken cancellationToken)
        {
            if (!hostKeyValidator(new SshPresentedHostKey(algorithm, hostKey)))
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

    private sealed class FixedAuthenticationMethodProvider(AuthenticationMethod method)
        : ISshAuthenticationMethodProvider
    {
        public ValueTask<IReadOnlyList<AuthenticationMethod>> GetAuthenticationMethodsAsync(
            SshProfile profile,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AuthenticationMethod>>([method]);
    }

    private sealed class TrackingAuthenticationMethod() : AuthenticationMethod("ssh-user"), IDisposable
    {
        public int DisposeCount { get; private set; }

        public override string Name => "tracking";

        public override AuthenticationResult Authenticate(Session session) =>
            AuthenticationResult.Failure;

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
