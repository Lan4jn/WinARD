using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Security.Vault;
using System.Runtime.ExceptionServices;

namespace WinARD.Desktop.Services;

internal interface IVaultSessionInstance : ICredentialStore, IAsyncDisposable;

internal interface IVaultSessionFactory
{
    ValueTask<IVaultSessionInstance> UnlockAsync(
        ISecret masterPassword,
        CancellationToken cancellationToken);
}

public sealed class VaultCredentialStoreSession : ICredentialStore, IAsyncDisposable
{
    private readonly IVaultSessionFactory _factory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _disposeSync = new();
    private readonly Dictionary<IVaultSessionInstance, int> _activeOperations =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IVaultSessionInstance, DisposalRequest> _disposals =
        new(ReferenceEqualityComparer.Instance);
    private IVaultSessionInstance? _vault;
    private ITimer? _idleTimer;
    private DateTimeOffset _lastActivity;
    private long _idleDeadlineUtcTicks;
    private long _activityGeneration;
    private Task? _disposeTask;
    private bool _disposed;

    public VaultCredentialStoreSession(
        IVaultStorage storage,
        TimeProvider timeProvider,
        TimeSpan idleTimeout)
        : this(
            new EncryptedVaultSessionFactory(
                storage ?? throw new ArgumentNullException(nameof(storage)),
                timeProvider ?? throw new ArgumentNullException(nameof(timeProvider))),
            timeProvider,
            idleTimeout)
    {
    }

    internal VaultCredentialStoreSession(
        IVaultSessionFactory factory,
        TimeProvider timeProvider,
        TimeSpan idleTimeout)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
        _idleTimeout = idleTimeout;
    }

    public bool IsUnlocked
    {
        get
        {
            if (Volatile.Read(ref _vault) is null)
            {
                return false;
            }

            if (_timeProvider.GetUtcNow().UtcTicks < Volatile.Read(ref _idleDeadlineUtcTicks))
            {
                return true;
            }

            _ = ExpireAsync(Volatile.Read(ref _activityGeneration));
            return false;
        }
    }

    public async ValueTask UnlockAsync(ISecret masterPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);
        DisposalRequest? oldDisposal = null;
        ExceptionDispatchInfo? unlockFailure = null;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var old = Interlocked.Exchange(ref _vault, null);
                StopIdleTimerLocked();
                _activityGeneration++;
                if (old is not null)
                {
                    oldDisposal = RequestDisposalLocked(old);
                }

                var opened = await _factory.UnlockAsync(masterPassword, cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(ref _vault, opened);
                TouchLocked();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception exception)
        {
            unlockFailure = ExceptionDispatchInfo.Capture(exception);
        }

        Exception? disposalFailure = null;
        if (oldDisposal is not null)
        {
            try
            {
                await CompleteDisposalAsync(oldDisposal).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                disposalFailure = exception;
            }
        }

        if (unlockFailure is not null)
        {
            if (disposalFailure is not null)
            {
                throw new AggregateException(
                    "解锁失败，且旧凭据库未能完整释放。",
                    unlockFailure.SourceException,
                    disposalFailure);
            }

            unlockFailure.Throw();
        }

        if (disposalFailure is not null)
        {
            throw disposalFailure;
        }
    }

    public async ValueTask LockAsync()
    {
        DisposalRequest? disposal = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var vault = Interlocked.Exchange(ref _vault, null);
            StopIdleTimerLocked();
            _activityGeneration++;
            if (vault is not null)
            {
                disposal = RequestDisposalLocked(vault);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (disposal is not null)
        {
            await CompleteDisposalAsync(disposal).ConfigureAwait(false);
        }
    }

    public ValueTask SaveAsync(
        CredentialReference reference,
        ISecret secret,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vault => vault.SaveAsync(reference, secret, cancellationToken),
            cancellationToken);

    public ValueTask<ISecret?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vault => vault.ReadAsync(reference, cancellationToken),
            cancellationToken);

    public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(
        CredentialReference reference,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vault => vault.ReadSnapshotAsync(reference, cancellationToken),
            cancellationToken);

    public ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vault => vault.CompareExchangeAsync(
                reference,
                expectedVersion,
                replacement,
                cancellationToken),
            cancellationToken);

    public ValueTask<CredentialStoreWriteResult> CompareExchangeWithVersionAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vault => vault.CompareExchangeWithVersionAsync(
                reference,
                expectedVersion,
                replacement,
                cancellationToken),
            cancellationToken);

    public ValueTask DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vault => vault.DeleteAsync(reference, cancellationToken),
            cancellationToken);

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            task = _disposeTask;
        }

        return new ValueTask(task);
    }

    private async ValueTask ExecuteAsync(
        Func<IVaultSessionInstance, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        var vault = await BeginOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(vault).ConfigureAwait(false);
        }
        catch (VaultLockedException)
        {
            await CompleteLockedOperationAsync(vault).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await CompleteOperationAsync(vault).ConfigureAwait(false);
            throw;
        }

        await CompleteOperationAsync(vault).ConfigureAwait(false);
    }

    private async ValueTask<T> ExecuteAsync<T>(
        Func<IVaultSessionInstance, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        var vault = await BeginOperationAsync(cancellationToken).ConfigureAwait(false);
        T result;
        try
        {
            result = await operation(vault).ConfigureAwait(false);
        }
        catch (VaultLockedException)
        {
            await CompleteLockedOperationAsync(vault).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await CompleteOperationAsync(vault).ConfigureAwait(false);
            throw;
        }

        await CompleteOperationAsync(vault).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<IVaultSessionInstance> BeginOperationAsync(
        CancellationToken cancellationToken)
    {
        IVaultSessionInstance? vault = null;
        DisposalRequest? expiredDisposal = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            vault = Volatile.Read(ref _vault) ?? throw new VaultLockedException();
            var activeOperations = _activeOperations.TryGetValue(vault, out var active)
                ? active
                : 0;
            if (_timeProvider.GetUtcNow().UtcTicks >= Volatile.Read(ref _idleDeadlineUtcTicks))
            {
                Volatile.Write(ref _vault, null);
                StopIdleTimerLocked();
                _activityGeneration++;
                expiredDisposal = RequestDisposalLocked(vault);
                vault = null;
            }
            else
            {
                _activeOperations[vault] = checked(activeOperations + 1);
                TouchLocked();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (expiredDisposal is not null)
        {
            await CompleteDisposalAsync(expiredDisposal).ConfigureAwait(false);
            throw new VaultLockedException();
        }

        return vault!;
    }

    private async ValueTask CompleteOperationAsync(IVaultSessionInstance vault)
    {
        DisposalRequest? disposal;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            disposal = DecrementActiveOperationsLocked(vault);
            if (!_disposed && ReferenceEquals(Volatile.Read(ref _vault), vault))
            {
                TouchLocked();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (disposal is not null)
        {
            await CompleteDisposalAsync(disposal).ConfigureAwait(false);
        }
    }

    private async ValueTask CompleteLockedOperationAsync(IVaultSessionInstance vault)
    {
        DisposalRequest? disposal;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            disposal = DecrementActiveOperationsLocked(vault);
            if (ReferenceEquals(Volatile.Read(ref _vault), vault))
            {
                Volatile.Write(ref _vault, null);
                StopIdleTimerLocked();
                _activityGeneration++;
                disposal = RequestDisposalLocked(vault);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (disposal is not null)
        {
            await CompleteDisposalAsync(disposal).ConfigureAwait(false);
        }
    }

    private void TouchLocked()
    {
        _lastActivity = _timeProvider.GetUtcNow();
        Volatile.Write(ref _idleDeadlineUtcTicks, (_lastActivity + _idleTimeout).UtcTicks);
        var generation = checked(++_activityGeneration);
        ScheduleIdleTimerLocked(generation, _idleTimeout);
    }

    private void ScheduleIdleTimerLocked(long generation, TimeSpan dueTime)
    {
        var timer = _timeProvider.CreateTimer(
            static state =>
            {
                var timerState = (IdleTimerState)state!;
                _ = timerState.Owner.ExpireAsync(timerState.Generation);
            },
            new IdleTimerState(this, generation),
            dueTime,
            Timeout.InfiniteTimeSpan);
        var previous = Interlocked.Exchange(ref _idleTimer, timer);
        previous?.Dispose();
    }

    private async Task ExpireAsync(long generation)
    {
        DisposalRequest? disposal = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || generation != _activityGeneration || _vault is null)
            {
                return;
            }

            var remaining = (_lastActivity + _idleTimeout) - _timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                ScheduleIdleTimerLocked(
                    generation,
                    remaining);
                return;
            }

            var vault = Interlocked.Exchange(ref _vault, null);
            StopIdleTimerLocked();
            _activityGeneration++;
            if (vault is not null)
            {
                disposal = RequestDisposalLocked(vault);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (disposal is not null)
        {
            await CompleteDisposalAsync(disposal).ConfigureAwait(false);
        }
    }

    private void StopIdleTimerLocked()
    {
        var timer = Interlocked.Exchange(ref _idleTimer, null);
        timer?.Dispose();
    }

    private DisposalRequest? DecrementActiveOperationsLocked(IVaultSessionInstance vault)
    {
        if (!_activeOperations.TryGetValue(vault, out var activeOperations))
        {
            throw new InvalidOperationException("凭据库操作计数不一致。");
        }

        if (activeOperations == 1)
        {
            _activeOperations.Remove(vault);
            if (_disposals.Remove(vault, out var disposal))
            {
                Volatile.Write(ref disposal.Ready, 1);
                return disposal;
            }
        }
        else
        {
            _activeOperations[vault] = activeOperations - 1;
        }

        return null;
    }

    private DisposalRequest RequestDisposalLocked(IVaultSessionInstance vault)
    {
        if (_disposals.TryGetValue(vault, out var existing))
        {
            return existing;
        }

        var request = new DisposalRequest(vault);
        if (_activeOperations.ContainsKey(vault))
        {
            _disposals.Add(vault, request);
        }
        else
        {
            Volatile.Write(ref request.Ready, 1);
        }
        return request;
    }

    private static async Task CompleteDisposalAsync(DisposalRequest request)
    {
        if (Volatile.Read(ref request.Ready) == 0)
        {
            await request.Completion.Task.ConfigureAwait(false);
            return;
        }

        if (Interlocked.Exchange(ref request.Started, 1) == 0)
        {
            try
            {
                await request.Vault.DisposeAsync().ConfigureAwait(false);
                request.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                request.Completion.TrySetException(exception);
            }
        }

        await request.Completion.Task.ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        List<DisposalRequest> disposals;
        DisposalRequest? currentDisposal = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var vault = Interlocked.Exchange(ref _vault, null);
            StopIdleTimerLocked();
            _activityGeneration++;
            if (vault is not null)
            {
                currentDisposal = RequestDisposalLocked(vault);
            }

            disposals = _disposals.Values.Distinct().ToList();
            if (currentDisposal is not null && !disposals.Contains(currentDisposal))
            {
                disposals.Add(currentDisposal);
            }
        }
        finally
        {
            _gate.Release();
        }

        await Task.WhenAll(disposals.Select(CompleteDisposalAsync)).ConfigureAwait(false);
    }

    private sealed class DisposalRequest(IVaultSessionInstance vault)
    {
        public IVaultSessionInstance Vault { get; } = vault;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started;

        public int Ready;
    }

    private sealed record IdleTimerState(
        VaultCredentialStoreSession Owner,
        long Generation);

    private sealed class EncryptedVaultSessionFactory(
        IVaultStorage storage,
        TimeProvider timeProvider) : IVaultSessionFactory
    {
        public async ValueTask<IVaultSessionInstance> UnlockAsync(
            ISecret masterPassword,
            CancellationToken cancellationToken)
        {
            var exists = await storage.ReadAsync(cancellationToken).ConfigureAwait(false) is not null;
            var vault = exists
                ? await EncryptedCredentialVault.OpenAsync(
                    storage,
                    masterPassword,
                    timeProvider,
                    Timeout.InfiniteTimeSpan,
                    cancellationToken).ConfigureAwait(false)
                : await EncryptedCredentialVault.CreateAsync(
                    storage,
                    masterPassword,
                    timeProvider,
                    Timeout.InfiniteTimeSpan,
                    cancellationToken).ConfigureAwait(false);
            return new EncryptedVaultSessionInstance(vault);
        }
    }

    private sealed class EncryptedVaultSessionInstance(EncryptedCredentialVault vault)
        : IVaultSessionInstance
    {
        public ValueTask SaveAsync(
            CredentialReference reference,
            ISecret secret,
            CancellationToken cancellationToken) => vault.SaveAsync(reference, secret, cancellationToken);

        public ValueTask<ISecret?> ReadAsync(
            CredentialReference reference,
            CancellationToken cancellationToken) => vault.ReadAsync(reference, cancellationToken);

        public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(
            CredentialReference reference,
            CancellationToken cancellationToken) => vault.ReadSnapshotAsync(reference, cancellationToken);

        public ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
            CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken) =>
            vault.CompareExchangeAsync(reference, expectedVersion, replacement, cancellationToken);

        public ValueTask<CredentialStoreWriteResult> CompareExchangeWithVersionAsync(
            CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken) =>
            vault.CompareExchangeWithVersionAsync(reference, expectedVersion, replacement, cancellationToken);

        public ValueTask DeleteAsync(
            CredentialReference reference,
            CancellationToken cancellationToken) => vault.DeleteAsync(reference, cancellationToken);

        public ValueTask DisposeAsync() => vault.DisposeAsync();
    }
}
