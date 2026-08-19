using System.Security.Cryptography;
using WinARD.Application.Ports;
using WinARD.Desktop.Services;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Security;
using WinARD.Security.Secrets;
using WinARD.Security.Vault;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class VaultCredentialStoreSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"winard-vault-session-{Guid.NewGuid():N}");

    [Fact]
    public async Task FirstUnlockCreatesVaultWithoutPersistingMasterPassword()
    {
        Directory.CreateDirectory(_directory);
        await using var sut = CreateSession();
        using var master = Secret("correct horse battery staple");

        await sut.UnlockAsync(master, CancellationToken.None);

        Assert.True(sut.IsUnlocked);
        Assert.True(File.Exists(Path.Combine(_directory, "credentials.vault")));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task WrongPasswordLeavesExistingVaultLocked()
    {
        Directory.CreateDirectory(_directory);
        await using (var creator = CreateSession())
        {
            using var master = Secret("correct horse battery staple");
            await creator.UnlockAsync(master, CancellationToken.None);
        }

        await using var sut = CreateSession();
        using var wrong = Secret("wrong password");

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => sut.UnlockAsync(wrong, CancellationToken.None).AsTask());
        Assert.False(sut.IsUnlocked);
    }

    [Fact]
    public async Task FailedReunlockDisposesPreviouslyUnlockedInstanceExactlyOnce()
    {
        var old = new FakeVaultInstance();
        var factory = new QueueVaultFactory(old);
        await using var sut = new VaultCredentialStoreSession(
            factory,
            new ManualTimeProvider(),
            TimeSpan.FromMinutes(1));
        using var master = Secret("master");
        await sut.UnlockAsync(master, CancellationToken.None);
        factory.UnlockException = new CryptographicException("wrong password");

        await Assert.ThrowsAsync<CryptographicException>(
            () => sut.UnlockAsync(master, CancellationToken.None).AsTask());

        Assert.False(sut.IsUnlocked);
        Assert.Equal(1, old.DisposeCalls);
    }

    [Fact]
    public async Task CancelledPromptKeepsVaultLockedAndDisposesTemporarySecret()
    {
        await using var sut = CreateSession();
        using var prompt = new CredentialPromptViewModel();
        var secret = new TrackingSecret();
        prompt.Supply(secret);

        prompt.Dispose();

        Assert.False(sut.IsUnlocked);
        Assert.True(secret.WasDisposed);
    }

    [Fact]
    public async Task LockedVaultCanBeUnlockedAgain()
    {
        await using var sut = CreateSession();
        using var first = Secret("correct horse battery staple");
        await sut.UnlockAsync(first, CancellationToken.None);
        await sut.LockAsync();
        using var second = Secret("correct horse battery staple");

        await sut.UnlockAsync(second, CancellationToken.None);

        Assert.True(sut.IsUnlocked);
    }

    [Fact]
    public async Task IdleExpiryImmediatelyChangesSessionToLocked()
    {
        var time = new ManualTimeProvider();
        await using var sut = new VaultCredentialStoreSession(
            new FileVaultStorage(Path.Combine(_directory, "credentials.vault")),
            time,
            TimeSpan.FromMinutes(1));
        using var master = Secret("correct horse battery staple");
        await sut.UnlockAsync(master, CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.False(sut.IsUnlocked);
    }

    [Fact]
    public async Task UpdatingIdleTimeoutReplacesUnlockedSessionDeadline()
    {
        var time = new ManualTimeProvider();
        await using var sut = new VaultCredentialStoreSession(
            new QueueVaultFactory(new FakeVaultInstance()),
            time,
            TimeSpan.FromMinutes(15));
        using var master = Secret("master");
        await sut.UnlockAsync(master, CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(2));
        await sut.UpdateIdleTimeoutAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(sut.IsUnlocked);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(sut.IsUnlocked);
    }

    [Fact]
    public async Task LockedFailureFromOldOperationDoesNotClearNewlyUnlockedInstance()
    {
        var old = new FakeVaultInstance();
        var newer = new FakeVaultInstance();
        var factory = new QueueVaultFactory(old, newer);
        await using var sut = new VaultCredentialStoreSession(
            factory,
            new ManualTimeProvider(),
            TimeSpan.FromMinutes(1));
        using var master = Secret("master");
        await sut.UnlockAsync(master, CancellationToken.None);
        old.ReadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        old.ContinueRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        old.FailReadAsLocked = true;
        var read = sut.ReadAsync(
            CredentialReference.Create("vault", "old"),
            CancellationToken.None).AsTask();
        await old.ReadStarted.Task;

        factory.UnlockStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        factory.ContinueUnlock = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unlock = sut.UnlockAsync(master, CancellationToken.None).AsTask();
        await factory.UnlockStarted.Task;
        factory.ContinueUnlock.SetResult();
        while (!sut.IsUnlocked)
        {
            await Task.Yield();
        }

        old.ContinueRead.SetResult();
        await Assert.ThrowsAsync<VaultLockedException>(() => read);
        await unlock;

        Assert.True(sut.IsUnlocked);
        Assert.Equal(1, old.DisposeCalls);
        Assert.False(old.DisposedDuringRead);
        Assert.Equal(0, newer.DisposeCalls);
    }

    [Fact]
    public async Task OperationOnOldInstanceDoesNotExtendNewInstanceIdleDeadline()
    {
        var old = new FakeVaultInstance
        {
            ReadStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueRead = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var newer = new FakeVaultInstance();
        var time = new ManualTimeProvider();
        var factory = new QueueVaultFactory(old, newer);
        await using var sut = new VaultCredentialStoreSession(
            factory,
            time,
            TimeSpan.FromMinutes(1));
        using var master = Secret("master");
        await sut.UnlockAsync(master, CancellationToken.None);
        var read = sut.ReadAsync(
            CredentialReference.Create("vault", "old"),
            CancellationToken.None).AsTask();
        await old.ReadStarted.Task;
        factory.UnlockStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        factory.ContinueUnlock = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unlock = sut.UnlockAsync(master, CancellationToken.None).AsTask();
        await factory.UnlockStarted.Task;
        factory.ContinueUnlock.SetResult();
        while (!sut.IsUnlocked)
        {
            await Task.Yield();
        }

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.False(sut.IsUnlocked);
        old.ContinueRead.SetResult();
        _ = await read;
        await unlock;
        Assert.False(old.DisposedDuringRead);
    }

    [Fact]
    public async Task DelayedIdleTimerCannotRenewAnExpiredSession()
    {
        var vault = new FakeVaultInstance();
        var time = new ManualTimeProvider();
        await using var sut = new VaultCredentialStoreSession(
            new QueueVaultFactory(vault),
            time,
            TimeSpan.FromMinutes(1));
        using var master = Secret("master");
        await sut.UnlockAsync(master, CancellationToken.None);

        time.AdvanceWithoutFiring(TimeSpan.FromMinutes(1));

        Assert.False(sut.IsUnlocked);
        await Assert.ThrowsAsync<VaultLockedException>(() => sut.ReadAsync(
            CredentialReference.Create("vault", "expired"),
            CancellationToken.None).AsTask());
        Assert.Equal(1, vault.DisposeCalls);
    }

    [Fact]
    public async Task ConcurrentUnlockLockAndDisposeDisposeInstalledVaultExactlyOnce()
    {
        var vault = new FakeVaultInstance();
        var factory = new QueueVaultFactory(vault)
        {
            UnlockStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueUnlock = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var sut = new VaultCredentialStoreSession(
            factory,
            new ManualTimeProvider(),
            TimeSpan.FromMinutes(1));
        using var master = Secret("master");
        var unlock = sut.UnlockAsync(master, CancellationToken.None).AsTask();
        await factory.UnlockStarted.Task;
        var locking = sut.LockAsync().AsTask();
        var disposing = sut.DisposeAsync().AsTask();

        factory.ContinueUnlock.SetResult();
        await Task.WhenAll(unlock, locking, disposing);

        Assert.False(sut.IsUnlocked);
        Assert.Equal(1, vault.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.UnlockAsync(master, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DisposeWaitsForRetiredVaultWithActiveOperation()
    {
        var old = new FakeVaultInstance
        {
            ReadStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueRead = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var newer = new FakeVaultInstance();
        var factory = new QueueVaultFactory(old, newer);
        var sut = new VaultCredentialStoreSession(
            factory,
            new ManualTimeProvider(),
            TimeSpan.FromMinutes(1));
        using var master = Secret("master");
        await sut.UnlockAsync(master, CancellationToken.None);
        var read = sut.ReadAsync(
            CredentialReference.Create("vault", "old"),
            CancellationToken.None).AsTask();
        await old.ReadStarted.Task;
        factory.UnlockStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        factory.ContinueUnlock = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unlock = sut.UnlockAsync(master, CancellationToken.None).AsTask();
        await factory.UnlockStarted.Task;
        factory.ContinueUnlock.SetResult();
        while (!sut.IsUnlocked)
        {
            await Task.Yield();
        }

        var dispose = sut.DisposeAsync().AsTask();
        await Task.Yield();
        Assert.False(dispose.IsCompleted);
        Assert.False(old.DisposedDuringRead);

        old.ContinueRead.SetResult();
        _ = await read;
        await Task.WhenAll(unlock, dispose);
        Assert.Equal(1, old.DisposeCalls);
        Assert.Equal(1, newer.DisposeCalls);
        Assert.False(old.DisposedDuringRead);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private VaultCredentialStoreSession CreateSession() => new(
        new FileVaultStorage(Path.Combine(_directory, "credentials.vault")),
        TimeProvider.System,
        TimeSpan.FromMinutes(5));

    private static SecretBuffer Secret(string value) =>
        SecretBuffer.CopyFrom(System.Text.Encoding.UTF8.GetBytes(value));

    private sealed class TrackingSecret : WinARD.Application.Ports.ISecret
    {
        private readonly bool _isClone;

        public TrackingSecret(bool isClone = false) => _isClone = isClone;

        public int Length => 1;

        public bool WasDisposed { get; private set; }

        public void CopyTo(Span<byte> destination) => destination[0] = 1;

        public WinARD.Application.Ports.ISecret Clone() => new TrackingSecret(isClone: true)
        {
            _owner = this,
        };

        private TrackingSecret? _owner;

        public void Dispose()
        {
            if (_isClone && _owner is not null)
            {
                _owner.WasDisposed = true;
            }
            else
            {
                WasDisposed = true;
            }
        }
    }

    private sealed class QueueVaultFactory(params IVaultSessionInstance[] instances)
        : IVaultSessionFactory
    {
        private readonly Queue<IVaultSessionInstance> _instances = new(instances);

        public TaskCompletionSource? UnlockStarted { get; set; }

        public TaskCompletionSource? ContinueUnlock { get; set; }

        public Exception? UnlockException { get; set; }

        public async ValueTask<IVaultSessionInstance> UnlockAsync(
            ISecret masterPassword,
            CancellationToken cancellationToken)
        {
            UnlockStarted?.TrySetResult();
            if (ContinueUnlock is not null)
            {
                await ContinueUnlock.Task.WaitAsync(cancellationToken);
            }

            if (UnlockException is not null)
            {
                throw UnlockException;
            }

            return _instances.Dequeue();
        }
    }

    private sealed class FakeVaultInstance : IVaultSessionInstance
    {
        public TaskCompletionSource? ReadStarted { get; set; }

        public TaskCompletionSource? ContinueRead { get; set; }

        public bool FailReadAsLocked { get; set; }

        public int DisposeCalls { get; private set; }

        public bool DisposedDuringRead { get; private set; }

        private int _activeReads;

        public ValueTask SaveAsync(
            CredentialReference reference,
            ISecret secret,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<ISecret?> ReadAsync(
            CredentialReference reference,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _activeReads);
            ReadStarted?.TrySetResult();
            try
            {
                if (ContinueRead is not null)
                {
                    await ContinueRead.Task.WaitAsync(cancellationToken);
                }

                if (FailReadAsLocked)
                {
                    throw new VaultLockedException();
                }

                return null;
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(
            CredentialReference reference,
            CancellationToken cancellationToken) => ValueTask.FromResult<CredentialStoreSnapshot?>(null);

        public ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
            CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CredentialStoreCompareExchangeResult.Succeeded);

        public ValueTask<CredentialStoreWriteResult> CompareExchangeWithVersionAsync(
            CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CredentialStoreWriteResult(
                CredentialStoreCompareExchangeResult.Succeeded,
                writtenVersion: null));

        public ValueTask DeleteAsync(
            CredentialReference reference,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _activeReads) > 0)
            {
                DisposedDuringRead = true;
            }

            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            _now += amount;
            foreach (var timer in _timers.ToArray())
            {
                timer.FireIfDue(_now);
            }
        }

        public void AdvanceWithoutFiring(TimeSpan amount) => _now += amount;

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAt) : ITimer
        {
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void FireIfDue(DateTimeOffset now)
            {
                if (!_disposed && now >= dueAt)
                {
                    callback(state);
                    Dispose();
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
