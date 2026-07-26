using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.Services;
using WinARD.Domain.Connections;
using WinARD.Transport.Ssh;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class ConnectionSessionControllerTests
{
    [Fact]
    public async Task HostKeyPromptServiceForwardsOnlySanitizedRequest()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var request = new SshHostKeyPromptRequest(
            endpoint, "ssh-ed25519", "SHA256:new", "SHA256:old", IsChanged: true);
        var sut = new SshHostKeyPromptService();
        SshHostKeyPromptRequest? received = null;
        sut.SetHandler((value, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            received = value;
            return ValueTask.FromResult(SshHostKeyPromptDecision.Replace);
        });

        var result = await sut.PromptAsync(request, CancellationToken.None);

        Assert.Equal(SshHostKeyPromptDecision.Replace, result);
        Assert.Equal(request, received);
    }

    [Fact]
    public async Task SuccessfulConnectOwnsSessionUntilDisconnectAndEnforcesSingleSession()
    {
        var transport = new TrackingTransport();
        var client = new TrackingClient();
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(client),
            new ErrorMapper());
        var coordinator = new ActiveSessionCoordinator();
        await using var sut = new ConnectionSessionController(
            handler,
            coordinator,
            new Repository(),
            new Prompt(SshHostKeyPromptDecision.Cancel));

        await sut.ConnectAsync(Profile(), CancellationToken.None);

        Assert.True(sut.IsConnected);
        Assert.False(client.Disposed);
        await Assert.ThrowsAsync<SessionAlreadyActiveException>(async () =>
        {
            await using var lease = await coordinator.AcquireAsync();
        });

        await sut.DisconnectAsync(CancellationToken.None);

        Assert.False(sut.IsConnected);
        Assert.True(client.Disposed);
        await using var released = await coordinator.AcquireAsync();
    }

    [Theory]
    [InlineData(false, SshHostKeyPromptDecision.Trust)]
    [InlineData(true, SshHostKeyPromptDecision.Replace)]
    public async Task AcceptedHostKeyDecisionPersistsCandidateAndRetries(
        bool changed,
        SshHostKeyPromptDecision decision)
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "AQIDBA==");
        var profile = SshProfileFor(endpoint);
        if (changed)
        {
            var old = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "CQkJCQ==").ToPin();
            profile = profile.WithSsh(profile.SshProfile!.WithHostKeyPin(old));
        }

        var transport = new HostKeyThenSuccessTransport(candidate, changed);
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(new TrackingClient()),
            new ErrorMapper());
        var repository = new Repository();
        var prompt = new Prompt(decision);
        await using var sut = new ConnectionSessionController(
            handler,
            new ActiveSessionCoordinator(),
            repository,
            prompt);

        await sut.ConnectAsync(profile, CancellationToken.None);

        Assert.Equal(2, transport.Attempts);
        Assert.Equal(candidate.ToPin(), repository.Saved!.SshProfile!.HostKeyPin);
        Assert.Equal(endpoint, prompt.Request!.Endpoint);
        Assert.Equal(candidate.Algorithm, prompt.Request.Algorithm);
        Assert.Equal(candidate.Fingerprint, prompt.Request.NewFingerprint);
        Assert.Equal(changed, prompt.Request.IsChanged);
    }

    private static ConnectionProfile Profile() => ConnectionProfile.Create(
        Guid.NewGuid(), "Studio", "studio.local", 5900, "operator");

    private static ConnectionProfile SshProfileFor(SshHostKeyEndpoint endpoint) =>
        Profile().WithSsh(SshProfile.Create(
            endpoint.Host,
            endpoint.Port,
            "ssh-user",
            privateKeyPath: null,
            targetHost: "studio.local",
            targetPort: 5900,
            WinARD.Domain.Security.CredentialReference.Create("ask", "profile/id/ssh-password"),
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null));

    private sealed class TrackingTransport : IRemoteTransportFactory
    {
        public Task<TransportConnection> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult(new TransportConnection(new MemoryStream(), new EndPointDescription(profile.Host, profile.Port)));
    }

    private sealed class HostKeyThenSuccessTransport(
        SshHostKeyCandidate candidate,
        bool changed) : IRemoteTransportFactory
    {
        public int Attempts { get; private set; }

        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            if (Attempts == 1)
            {
                var verification = SshHostKeyVerifier.Verify(
                    candidate,
                    changed ? profile.SshProfile!.HostKeyPin : null);
                throw changed
                    ? new SshHostKeyChangedException(verification)
                    : new SshHostKeyUnknownException(verification);
            }

            return Task.FromResult(new TransportConnection(
                new MemoryStream(),
                new EndPointDescription(profile.Host, profile.Port)));
        }
    }

    private sealed class Repository : IDeviceRepository
    {
        public ConnectionProfile? Saved { get; private set; }

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved = profile;
            return Task.CompletedTask;
        }

        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Saved?.Id == id ? Saved : null);

        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(Saved is null ? [] : [Saved]);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Prompt(SshHostKeyPromptDecision decision) : ISshHostKeyPrompt
    {
        public SshHostKeyPromptRequest? Request { get; private set; }

        public ValueTask<SshHostKeyPromptDecision> PromptAsync(
            SshHostKeyPromptRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class FixedSecretProvider : IConnectionSecretProvider
    {
        public ValueTask<ISecret> GetSecretAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ISecret>(new Secret());
    }

    private sealed class FixedClientFactory(TrackingClient client) : IRfbClientFactory
    {
        public IRfbClient Create(Stream stream) => client;
    }

    private sealed class TrackingClient : IRfbClient
    {
        public bool Disposed { get; private set; }
        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Secret : ISecret
    {
        public int Length => 1;
        public void CopyTo(Span<byte> destination) => destination[0] = 1;
        public ISecret Clone() => new Secret();
        public void Dispose() { }
    }
}
