using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.Services;
using WinARD.Domain.Connections;
using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

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
        var prompt = new Prompt(SshHostKeyPromptDecision.Cancel);
        await using var sut = new ConnectionSessionController(
            new ConnectionAttemptWorkflow(handler, prompt),
            coordinator,
            new Repository());

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

    [Fact]
    public async Task Transfer_moves_session_and_lease_to_window_ownership_until_closed()
    {
        var client = new TrackingClient();
        var coordinator = new ActiveSessionCoordinator();
        await using var sut = Controller(client, coordinator);
        await sut.ConnectAsync(Profile(), CancellationToken.None);

        var ownership = sut.TransferConnectedSession();

        Assert.True(sut.IsConnected);
        Assert.NotNull(ownership.Session);
        Assert.Throws<InvalidOperationException>(() => sut.TransferConnectedSession());
        await Assert.ThrowsAsync<SessionAlreadyActiveException>(async () =>
        {
            await using var lease = await coordinator.AcquireAsync();
        });

        await ownership.DisposeAsync();

        Assert.False(sut.IsConnected);
        Assert.True(client.Disposed);
        await using var released = await coordinator.AcquireAsync();
    }

    [Fact]
    public async Task Controller_and_window_concurrent_close_dispose_session_once()
    {
        var client = new TrackingClient();
        var coordinator = new ActiveSessionCoordinator();
        var sut = Controller(client, coordinator);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        var ownership = sut.TransferConnectedSession();

        await Task.WhenAll(
            sut.DisposeAsync().AsTask(),
            ownership.DisposeAsync().AsTask());

        Assert.Equal(1, client.DisposeCount);
        await using var released = await coordinator.AcquireAsync();
    }

    [Fact]
    public async Task Concurrent_controller_dispose_calls_wait_for_the_same_disposal()
    {
        var client = new BlockingDisposeClient();
        var sut = Controller(client, new ActiveSessionCoordinator());
        await sut.ConnectAsync(Profile(), CancellationToken.None);

        var first = sut.DisposeAsync().AsTask();
        await client.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = sut.DisposeAsync().AsTask();

        Assert.False(second.IsCompleted);
        client.AllowDispose.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, client.DisposeCount);
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
            new ConnectionAttemptWorkflow(handler, prompt),
            new ActiveSessionCoordinator(),
            repository);

        await sut.ConnectAsync(profile, CancellationToken.None);

        Assert.Equal(2, transport.Attempts);
        Assert.Equal(candidate.ToPin(), repository.Saved!.SshProfile!.HostKeyPin);
        Assert.Equal(endpoint, prompt.Request!.Endpoint);
        Assert.Equal(candidate.Algorithm, prompt.Request.Algorithm);
        Assert.Equal(candidate.Fingerprint, prompt.Request.NewFingerprint);
        Assert.Equal(changed, prompt.Request.IsChanged);
    }

    [Theory]
    [InlineData(false, SshHostKeyPromptDecision.Trust)]
    [InlineData(true, SshHostKeyPromptDecision.Replace)]
    public async Task SharedAttemptWorkflowReturnsAcceptedPinAndRetriesExactlyOnce(
        bool changed,
        SshHostKeyPromptDecision decision)
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(
            endpoint,
            "ssh-ed25519",
            "AQIDBA==");
        var profile = SshProfileFor(endpoint);
        if (changed)
        {
            var old = SshHostKeyVerifier.CreateCandidate(
                endpoint,
                "ssh-ed25519",
                "CQkJCQ==").ToPin();
            profile = profile.WithSsh(profile.SshProfile!.WithHostKeyPin(old));
        }

        var transport = new HostKeyThenSuccessTransport(candidate, changed);
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(new TrackingClient()),
            new ErrorMapper());
        var prompt = new Prompt(decision);
        var acceptedProfiles = new List<ConnectionProfile>();
        var sut = new ConnectionAttemptWorkflow(handler, prompt);

        var outcome = await sut.AttemptAsync(
            profile,
            stageChanged: null,
            (updated, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                acceptedProfiles.Add(updated);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.NotNull(outcome.Result.Session);
        Assert.Equal(2, transport.Attempts);
        Assert.Single(acceptedProfiles);
        Assert.Equal(candidate.ToPin(), outcome.Profile.SshProfile!.HostKeyPin);
        Assert.Equal(candidate.ToPin(), acceptedProfiles[0].SshProfile!.HostKeyPin);
        await outcome.Result.Session.DisposeAsync();
    }

    [Theory]
    [InlineData(false, SshHostKeyPromptDecision.Cancel)]
    [InlineData(false, SshHostKeyPromptDecision.Replace)]
    [InlineData(true, SshHostKeyPromptDecision.Cancel)]
    [InlineData(true, SshHostKeyPromptDecision.Trust)]
    public async Task SharedAttemptWorkflowDoesNotRetryUnacceptedHostKeyDecision(
        bool changed,
        SshHostKeyPromptDecision decision)
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(
            endpoint,
            "ssh-ed25519",
            "AQIDBA==");
        var profile = SshProfileFor(endpoint);
        if (changed)
        {
            var old = SshHostKeyVerifier.CreateCandidate(
                endpoint,
                "ssh-ed25519",
                "CQkJCQ==").ToPin();
            profile = profile.WithSsh(profile.SshProfile!.WithHostKeyPin(old));
        }

        var transport = new HostKeyThenSuccessTransport(candidate, changed);
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(new TrackingClient()),
            new ErrorMapper());
        var accepted = 0;
        var sut = new ConnectionAttemptWorkflow(handler, new Prompt(decision));

        var outcome = await sut.AttemptAsync(
            profile,
            stageChanged: null,
            (_, _) =>
            {
                accepted++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Null(outcome.Result.Session);
        Assert.Equal(1, transport.Attempts);
        Assert.Equal(0, accepted);
        Assert.Equal(profile, outcome.Profile);
    }

    [Fact]
    public async Task SharedAttemptWorkflowRetriesAcceptedHostKeyOnlyOnce()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(
            endpoint,
            "ssh-ed25519",
            "AQIDBA==");
        var profile = SshProfileFor(endpoint);
        var transport = new AlwaysUnknownHostKeyTransport(candidate);
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(new TrackingClient()),
            new ErrorMapper());
        var sut = new ConnectionAttemptWorkflow(
            handler,
            new Prompt(SshHostKeyPromptDecision.Trust));

        var outcome = await sut.AttemptAsync(
            profile,
            stageChanged: null,
            acceptedHostKey: null,
            CancellationToken.None);

        Assert.Null(outcome.Result.Session);
        Assert.Equal(2, transport.Attempts);
        Assert.Equal(candidate.ToPin(), outcome.Profile.SshProfile!.HostKeyPin);
    }

    [Fact]
    public async Task ChangedHostKeyCancelReturnsSanitizedContextAndPreauthorizedReplaceRetriesOnce()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "AQIDBA==");
        var old = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "CQkJCQ==").ToPin();
        var profile = SshProfileFor(endpoint).WithSsh(
            SshProfileFor(endpoint).SshProfile!.WithHostKeyPin(old));
        var transport = new HostKeyUntilPinnedTransport(candidate);
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(new TrackingClient()),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(
            handler,
            new Prompt(SshHostKeyPromptDecision.Cancel));

        var canceled = await workflow.AttemptAsync(
            profile,
            stageChanged: null,
            acceptedHostKey: null,
            CancellationToken.None);

        Assert.NotNull(canceled.HostKeyFailure);
        Assert.Equal(old.Fingerprint, canceled.HostKeyFailure.PreviousFingerprint);
        Assert.Equal(candidate.Fingerprint, canceled.HostKeyFailure.NewFingerprint);
        Assert.True(canceled.HostKeyFailure.IsChanged);
        Assert.Equal(1, transport.Attempts);

        var persisted = new List<ConnectionProfile>();
        var replaced = await workflow.AttemptAsync(
            profile,
            stageChanged: null,
            (updated, _) =>
            {
                persisted.Add(updated);
                return Task.CompletedTask;
            },
            new PreauthorizedHostKeyPrompt(canceled.HostKeyFailure),
            CancellationToken.None);

        Assert.NotNull(replaced.Result.Session);
        Assert.Equal(3, transport.Attempts);
        Assert.Single(persisted);
        Assert.Equal(candidate.ToPin(), persisted[0].SshProfile!.HostKeyPin);
        await replaced.Result.Session.DisposeAsync();
    }

    [Fact]
    public async Task PreauthorizedHostKeyDecisionCanOnlyBeConsumedOnce()
    {
        var request = new SshHostKeyPromptRequest(
            new SshHostKeyEndpoint("jump.local", 22),
            "ssh-ed25519",
            "SHA256:new",
            "SHA256:old",
            IsChanged: true);
        var prompt = new PreauthorizedHostKeyPrompt(request);

        Assert.Equal(
            SshHostKeyPromptDecision.Replace,
            await prompt.PromptAsync(request, CancellationToken.None));
        Assert.Equal(
            SshHostKeyPromptDecision.Cancel,
            await prompt.PromptAsync(request, CancellationToken.None));
    }

    private static ConnectionProfile Profile() => ConnectionProfile.Create(
        Guid.NewGuid(), "Studio", "studio.local", 5900, "operator");

    private static ConnectionSessionController Controller(
        IRfbClient client,
        ActiveSessionCoordinator coordinator) =>
        new(
            new ConnectionAttemptWorkflow(
                new ConnectDeviceHandler(
                    new TrackingTransport(),
                    new FixedSecretProvider(),
                    new FixedClientFactory(client),
                    new ErrorMapper()),
                new Prompt(SshHostKeyPromptDecision.Cancel)),
            coordinator,
            new Repository());

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

    private sealed class AlwaysUnknownHostKeyTransport(
        SshHostKeyCandidate candidate) : IRemoteTransportFactory
    {
        public int Attempts { get; private set; }

        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            var verification = SshHostKeyVerifier.Verify(candidate, null);
            throw new SshHostKeyUnknownException(verification);
        }
    }

    private sealed class HostKeyUntilPinnedTransport(
        SshHostKeyCandidate candidate) : IRemoteTransportFactory
    {
        public int Attempts { get; private set; }

        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            var verification = SshHostKeyVerifier.Verify(candidate, profile.SshProfile?.HostKeyPin);
            return verification.Status switch
            {
                SshHostKeyStatus.Trusted => Task.FromResult(new TransportConnection(
                    new MemoryStream(),
                    new EndPointDescription(profile.Host, profile.Port))),
                SshHostKeyStatus.Changed => Task.FromException<TransportConnection>(
                    new SshHostKeyChangedException(verification)),
                _ => Task.FromException<TransportConnection>(new SshHostKeyUnknownException(verification)),
            };
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

    private sealed class FixedClientFactory(IRfbClient client) : IRfbClientFactory
    {
        public IRfbClient Create(Stream stream) => client;
    }

    private sealed class TrackingClient : IRfbClient
    {
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class BlockingDisposeClient : IRfbClient
    {
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }

        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            await AllowDispose.Task;
        }
    }

    private sealed class Secret : ISecret
    {
        public int Length => 1;
        public void CopyTo(Span<byte> destination) => destination[0] = 1;
        public ISecret Clone() => new Secret();
        public void Dispose() { }
    }
}
