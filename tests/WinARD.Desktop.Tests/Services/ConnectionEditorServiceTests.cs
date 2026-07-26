using System.Text;
using System.Security.Cryptography;
using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.Services;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Security.Secrets;
using WinARD.Transport.Ssh;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class ConnectionEditorServiceTests
{
    [Fact]
    public async Task AskEveryTimeSavesReferenceWithoutPersistingSecret()
    {
        var repository = new FakeRepository();
        using var store = new TransientCredentialStore();
        var sut = CreateService(repository, store);
        var profile = Profile();
        using var secret = Secret("must-not-be-persisted");

        var saved = await sut.SaveAsync(
            profile,
            CredentialSaveMode.AskEveryTime,
            secret,
            CancellationToken.None);

        Assert.Equal("ask", saved.CredentialReference!.Store);
        Assert.Equal(saved, repository.Saved);
        Assert.Null(await store.ReadAsync(saved.CredentialReference, CancellationToken.None));
    }

    [Fact]
    public async Task RepositoryFailureRollsBackNewCredential()
    {
        var repository = new FakeRepository { FailSave = true };
        using var store = new TransientCredentialStore();
        var sut = CreateService(repository, store);
        var profile = Profile();
        using var secret = Secret("temporary-password");

        await Assert.ThrowsAsync<IOException>(() => sut.SaveAsync(
            profile,
            CredentialSaveMode.WindowsCredentialManager,
            secret,
            CancellationToken.None));

        var reference = WinARD.Domain.Security.CredentialReference.Create(
            "windows",
            $"profile/{profile.Id:D}/mac");
        Assert.Null(await store.ReadAsync(reference, CancellationToken.None));
    }

    [Fact]
    public async Task EditingWithoutNewSecretPreservesOpaqueReferences()
    {
        var macReference = WinARD.Domain.Security.CredentialReference.Create("migrated", "shared/mac");
        var sshReference = WinARD.Domain.Security.CredentialReference.Create("legacy-vault", "shared/ssh-password");
        var passphraseReference = WinARD.Domain.Security.CredentialReference.Create("legacy-vault", "shared/ssh-passphrase");
        var pin = new SshHostKeyPin(
            new SshHostKeyEndpoint("jump.local", 22),
            "ssh-ed25519",
            "AAAAC3NzaC1lZDI1NTE5AAAAIFixture",
            "SHA256:fixture");
        var original = Profile()
            .WithCredential(macReference)
            .WithSsh(SshProfile.Create(
                    "jump.local", 22, "ssh-user", null, "studio.local", 5900,
                    credentialReference: null, null, null)
                .WithAuthenticationCredentials(sshReference, passphraseReference)
                .WithHostKeyPin(pin));
        var repository = new FakeRepository { Existing = original };
        var store = new FailOnCredentialAccessStore();
        var sut = CreateService(repository, store);
        var renamed = ConnectionProfile.Create(
                original.Id, "Renamed", original.Host, original.Port, original.MacUsername)
            .WithCredential(macReference)
            .WithSsh(original.SshProfile!);

        var saved = await sut.SaveAsync(
            renamed,
            CredentialSaveMode.WindowsCredentialManager,
            secret: null,
            CancellationToken.None);

        Assert.Equal("Renamed", saved.DisplayName);
        Assert.Equal(macReference, saved.CredentialReference);
        Assert.Equal(sshReference, saved.SshProfile!.PasswordCredentialReference);
        Assert.Equal(passphraseReference, saved.SshProfile.PrivateKeyPassphraseCredentialReference);
        Assert.Equal(pin, saved.SshProfile.HostKeyPin);
        Assert.Equal(saved, repository.Saved);
        Assert.Equal(0, store.AccessAttempts);
    }

    [Fact]
    public async Task OpaqueCredentialReferenceRejectsSecretWithoutExplicitModeMigration()
    {
        var original = Profile()
            .WithCredential(WinARD.Domain.Security.CredentialReference.Create(
                "legacy-plugin",
                "shared/mac"));
        var repository = new FakeRepository { Existing = original };
        using var store = new VersionedCredentialStore();
        var sut = CreateService(repository, store, store);
        using var secret = Secret("replacement");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SaveAsync(
                original,
                CredentialSaveMode.WindowsCredentialManager,
                secret,
                CancellationToken.None));

        Assert.Contains("先选择受支持的凭据保存方式", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, repository.SaveCalls);
        Assert.Empty(store.References);
    }

    [Fact]
    public async Task ExplicitModeMigrationDoesNotDeleteOpaqueCredentialReferences()
    {
        var oldMac = WinARD.Domain.Security.CredentialReference.Create(
            "legacy-plugin",
            "shared/mac");
        var oldSsh = WinARD.Domain.Security.CredentialReference.Create(
            "opaque-ssh",
            "shared/ssh");
        var original = Profile()
            .WithCredential(oldMac)
            .WithSsh(SshProfile.Create(
                    "jump.local", 22, "ssh-user", null, "studio.local", 5900,
                    credentialReference: null, null, null)
                .WithAuthenticationCredentials(oldSsh, null));
        var migratedDraft = ProfileWithId(original.Id)
            .WithSsh(SshProfile.Create(
                "jump.local", 22, "ssh-user", null, "studio.local", 5900,
                credentialReference: null, null, null));
        var repository = new FakeRepository { Existing = original };
        using var store = new VersionedCredentialStore();
        var sut = CreateService(repository, store, store);
        using var mac = Secret("new-mac");
        using var ssh = Secret("new-ssh");
        using var package = new ConnectionEditorSecretPackage(mac, ssh);

        var saved = await sut.SaveAsync(
            migratedDraft,
            CredentialSaveMode.WindowsCredentialManager,
            package,
            CancellationToken.None);

        Assert.Equal("windows", saved.CredentialReference!.Store);
        Assert.Equal("windows", saved.SshProfile!.PasswordCredentialReference!.Store);
        Assert.DoesNotContain(oldMac, store.DeleteAttempts);
        Assert.DoesNotContain(oldSsh, store.DeleteAttempts);
    }

    [Fact]
    public async Task RepositoryFailureDoesNotRollbackOverConcurrentCredentialUpdate()
    {
        var profile = Profile();
        var reference = WinARD.Domain.Security.CredentialReference.Create(
            "windows",
            $"profile/{profile.Id:D}/mac");
        using var store = new VersionedCredentialStore();
        var repository = new FakeRepository
        {
            SaveCallback = async cancellationToken =>
            {
                using var winner = Secret("concurrent-winner");
                await store.SaveAsync(reference, winner, cancellationToken);
                throw new IOException("repository failure");
            },
        };
        var sut = CreateService(repository, store, store);
        using var secret = Secret("this-save");

        await Assert.ThrowsAsync<AggregateException>(() => sut.SaveAsync(
            profile,
            CredentialSaveMode.WindowsCredentialManager,
            secret,
            CancellationToken.None));

        using var actual = await store.ReadAsync(reference, CancellationToken.None);
        Assert.Equal("concurrent-winner", ReadSecret(actual!));
    }

    [Fact]
    public async Task CleanupFailureAfterProfileCommitReturnsSuccessWithWarning()
    {
        var oldReference = WinARD.Domain.Security.CredentialReference.Create("windows", "old/mac");
        var original = Profile().WithCredential(oldReference);
        var repository = new FakeRepository { Existing = original };
        using var store = new VersionedCredentialStore { ThrowOnDelete = true };
        var sut = CreateService(repository, store, store);
        using var secret = Secret("new-password");

        var saved = await sut.SaveWithResultAsync(
            original,
            CredentialSaveMode.EncryptedVault,
            secret,
            CancellationToken.None);

        Assert.Equal("vault", saved.Profile.CredentialReference!.Store);
        Assert.Equal(saved.Profile, repository.Saved);
        Assert.Equal("连接已保存，但旧凭据未能清理。", saved.Warning);
    }

    [Fact]
    public async Task TestCleansFirstTransientSecretWhenSecondWriteFails()
    {
        using var persistent = new VersionedCredentialStore();
        using var transient = new VersionedCredentialStore { FailSaveNumber = 2 };
        var sut = CreateService(new FakeRepository(), persistent, transient);
        var profile = Profile().WithSsh(SshProfile.Create(
            "jump.local", 22, "ssh-user", null, "studio.local", 5900,
            credentialReference: null, null, null));
        using var mac = Secret("mac-password");
        using var ssh = Secret("ssh-password");
        using var package = new ConnectionEditorSecretPackage(mac, ssh);

        await Assert.ThrowsAsync<IOException>(() => sut.TestAsync(
            profile,
            CredentialSaveMode.WindowsCredentialManager,
            package,
            CancellationToken.None));

        Assert.Empty(transient.References);
    }

    [Theory]
    [InlineData(false, SshHostKeyPromptDecision.Trust)]
    [InlineData(true, SshHostKeyPromptDecision.Replace)]
    public async Task TestAcceptedHostKeyUpdatesDraftWithoutSavingAndDisposesSession(
        bool changed,
        SshHostKeyPromptDecision decision)
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(
            endpoint,
            "ssh-ed25519",
            "AQIDBA==");
        var profile = Profile().WithSsh(SshProfile.Create(
            endpoint.Host,
            endpoint.Port,
            "ssh-user",
            privateKeyPath: null,
            targetHost: "studio.local",
            targetPort: 5900,
            credentialReference: null,
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null));
        if (changed)
        {
            var old = SshHostKeyVerifier.CreateCandidate(
                endpoint,
                "ssh-ed25519",
                "CQkJCQ==").ToPin();
            profile = profile.WithSsh(profile.SshProfile!.WithHostKeyPin(old));
        }

        var repository = new FakeRepository();
        using var persistent = new VersionedCredentialStore();
        using var transient = new VersionedCredentialStore();
        var transport = new HostKeyThenSuccessTransport(candidate, changed);
        var client = new TrackingClient();
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(client),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(handler, new Prompt(decision));
        var sut = new ConnectionEditorService(
            repository,
            persistent,
            transient,
            workflow);
        using var mac = Secret("mac-password");

        var result = await sut.TestAsync(
            profile,
            CredentialSaveMode.WindowsCredentialManager,
            mac,
            CancellationToken.None);

        Assert.Equal(0, repository.SaveCalls);
        Assert.Equal(2, transport.Attempts);
        Assert.True(client.Disposed);
        Assert.Equal(candidate.ToPin(), result.Profile.SshProfile!.HostKeyPin);
        Assert.Empty(transient.References);
    }

    private static ConnectionEditorService CreateService(
        IDeviceRepository repository,
        ICredentialStore store,
        ITransientCredentialStore? transientStore = null) => new(
            repository,
            store,
            transientStore ?? (ITransientCredentialStore)store,
            new ConnectionAttemptWorkflow(
                new ConnectDeviceHandler(
                    new UnusedTransport(),
                    new UnusedSecretProvider(),
                    new RfbClientFactory(),
                    new ErrorMapper()),
                new Prompt(SshHostKeyPromptDecision.Cancel)));

    private static ConnectionProfile Profile() => ConnectionProfile.Create(
        Guid.NewGuid(),
        "Studio Mac",
        "studio.local",
        5900,
        "operator");

    private static ConnectionProfile ProfileWithId(Guid id) => ConnectionProfile.Create(
        id,
        "Studio Mac",
        "studio.local",
        5900,
        "operator");

    private static SecretBuffer Secret(string value) =>
        SecretBuffer.CopyFrom(Encoding.UTF8.GetBytes(value));

    private static string ReadSecret(ISecret secret)
    {
        var bytes = new byte[secret.Length];
        try
        {
            secret.CopyTo(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed class FakeRepository : IDeviceRepository
    {
        public bool FailSave { get; init; }

        public ConnectionProfile? Saved { get; private set; }

        public ConnectionProfile? Existing { get; init; }

        public Func<CancellationToken, Task>? SaveCallback { get; init; }

        public int SaveCalls { get; private set; }

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCalls++;
            if (FailSave)
            {
                throw new IOException("repository failure");
            }

            Saved = profile;
            return SaveCallback?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Existing?.Id == id ? Existing : null);

        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(Saved is null ? [] : [Saved]);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class VersionedCredentialStore : ITransientCredentialStore, IDisposable
    {
        private readonly Dictionary<WinARD.Domain.Security.CredentialReference, (byte[] Value, long Version)> _values = [];
        private long _nextVersion;
        private int _saveCount;

        public bool ThrowOnDelete { get; init; }

        public int? FailSaveNumber { get; init; }

        public IReadOnlyCollection<WinARD.Domain.Security.CredentialReference> References => _values.Keys;

        public List<WinARD.Domain.Security.CredentialReference> DeleteAttempts { get; } = [];

        public ValueTask SaveAsync(
            WinARD.Domain.Security.CredentialReference reference,
            ISecret secret,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _saveCount) == FailSaveNumber)
            {
                throw new IOException("credential write failure");
            }

            _values[reference] = (Copy(secret), Interlocked.Increment(ref _nextVersion));
            return ValueTask.CompletedTask;
        }

        public ValueTask<ISecret?> ReadAsync(
            WinARD.Domain.Security.CredentialReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISecret?>(_values.TryGetValue(reference, out var value)
                ? SecretBuffer.CopyFrom(value.Value)
                : null);
        }

        public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(
            WinARD.Domain.Security.CredentialReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_values.TryGetValue(reference, out var value)
                ? new CredentialStoreSnapshot(
                    SecretBuffer.CopyFrom(value.Value),
                    Version(value.Version))
                : null);
        }

        public async ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
            WinARD.Domain.Security.CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken)
        {
            using var write = await CompareExchangeWithVersionAsync(
                reference, expectedVersion, replacement, cancellationToken);
            return write.Result;
        }

        public ValueTask<CredentialStoreWriteResult> CompareExchangeWithVersionAsync(
            WinARD.Domain.Security.CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasCurrent = _values.TryGetValue(reference, out var current);
            var matches = hasCurrent
                ? expectedVersion is not null && expectedVersion.FixedTimeEquals(VersionBytes(current.Version))
                : expectedVersion is null;
            if (!matches)
            {
                return ValueTask.FromResult(new CredentialStoreWriteResult(
                    CredentialStoreCompareExchangeResult.Conflict,
                    null));
            }

            if (replacement is null)
            {
                if (ThrowOnDelete)
                {
                    throw new IOException("credential cleanup failure");
                }

                _values.Remove(reference);
                return ValueTask.FromResult(new CredentialStoreWriteResult(
                    CredentialStoreCompareExchangeResult.Succeeded,
                    null));
            }

            if (Interlocked.Increment(ref _saveCount) == FailSaveNumber)
            {
                throw new IOException("credential write failure");
            }

            var version = Interlocked.Increment(ref _nextVersion);
            _values[reference] = (Copy(replacement), version);
            return ValueTask.FromResult(new CredentialStoreWriteResult(
                CredentialStoreCompareExchangeResult.Succeeded,
                Version(version)));
        }

        public ValueTask DeleteAsync(
            WinARD.Domain.Security.CredentialReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteAttempts.Add(reference);
            if (ThrowOnDelete)
            {
                throw new IOException("credential cleanup failure");
            }

            if (_values.Remove(reference, out var removed))
            {
                CryptographicOperations.ZeroMemory(removed.Value);
            }

            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            foreach (var value in _values.Values)
            {
                CryptographicOperations.ZeroMemory(value.Value);
            }

            _values.Clear();
        }

        private static byte[] Copy(ISecret secret)
        {
            var value = new byte[secret.Length];
            secret.CopyTo(value);
            return value;
        }

        private static CredentialStoreVersion Version(long version) =>
            CredentialStoreVersion.CopyFrom(VersionBytes(version));

        private static byte[] VersionBytes(long version) => BitConverter.GetBytes(version);
    }

    private sealed class FailOnCredentialAccessStore : ITransientCredentialStore
    {
        public int AccessAttempts { get; private set; }

        public ValueTask SaveAsync(
            WinARD.Domain.Security.CredentialReference reference,
            ISecret secret,
            CancellationToken cancellationToken) => Fail();

        public ValueTask<ISecret?> ReadAsync(
            WinARD.Domain.Security.CredentialReference reference,
            CancellationToken cancellationToken) => Fail<ISecret?>();

        public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(
            WinARD.Domain.Security.CredentialReference reference,
            CancellationToken cancellationToken) => Fail<CredentialStoreSnapshot?>();

        public ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
            WinARD.Domain.Security.CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken) => Fail<CredentialStoreCompareExchangeResult>();

        public ValueTask DeleteAsync(
            WinARD.Domain.Security.CredentialReference reference,
            CancellationToken cancellationToken) => Fail();

        private ValueTask Fail()
        {
            AccessAttempts++;
            return ValueTask.FromException(new InvalidOperationException("Credential store must not be accessed."));
        }

        private ValueTask<T> Fail<T>()
        {
            AccessAttempts++;
            return ValueTask.FromException<T>(
                new InvalidOperationException("Credential store must not be accessed."));
        }
    }

    private sealed class UnusedTransport : IRemoteTransportFactory
    {
        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by save tests.");
    }

    private sealed class UnusedSecretProvider : IConnectionSecretProvider
    {
        public ValueTask<ISecret> GetSecretAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by save tests.");
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

    private sealed class Prompt(SshHostKeyPromptDecision decision) : ISshHostKeyPrompt
    {
        public ValueTask<SshHostKeyPromptDecision> PromptAsync(
            SshHostKeyPromptRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class FixedSecretProvider : IConnectionSecretProvider
    {
        public ValueTask<ISecret> GetSecretAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ISecret>(new FixedSecret());
    }

    private sealed class FixedClientFactory(TrackingClient client) : IRfbClientFactory
    {
        public IRfbClient Create(Stream stream) => client;
    }

    private sealed class TrackingClient : IRfbClient
    {
        public bool Disposed { get; private set; }

        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AuthenticateAsync(
            string username,
            ISecret secret,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedSecret : ISecret
    {
        public int Length => 1;

        public void CopyTo(Span<byte> destination) => destination[0] = 1;

        public ISecret Clone() => new FixedSecret();

        public void Dispose()
        {
        }
    }
}
