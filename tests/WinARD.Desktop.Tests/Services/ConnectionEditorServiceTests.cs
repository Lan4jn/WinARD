using System.Text;
using System.Security.Cryptography;
using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.Services;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Security.Secrets;
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
        var sshReference = WinARD.Domain.Security.CredentialReference.Create("legacy-vault", "shared/ssh");
        var original = Profile()
            .WithCredential(macReference)
            .WithSsh(SshProfile.Create(
                "jump.local", 22, "ssh-user", null, "studio.local", 5900,
                sshReference, null, null));
        var repository = new FakeRepository { Existing = original };
        using var store = new TransientCredentialStore();
        var sut = CreateService(repository, store);

        var saved = await sut.SaveAsync(
            original,
            CredentialSaveMode.WindowsCredentialManager,
            secret: null,
            CancellationToken.None);

        Assert.Equal(macReference, saved.CredentialReference);
        Assert.Equal(sshReference, saved.SshProfile!.PasswordCredentialReference);
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

    private static ConnectionEditorService CreateService(
        IDeviceRepository repository,
        ICredentialStore store,
        ITransientCredentialStore? transientStore = null) => new(
            repository,
            store,
            transientStore ?? (ITransientCredentialStore)store,
            new ConnectDeviceHandler(
                new UnusedTransport(),
                new UnusedSecretProvider(),
                new RfbClientFactory(),
                new ErrorMapper()));

    private static ConnectionProfile Profile() => ConnectionProfile.Create(
        Guid.NewGuid(),
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

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
}
