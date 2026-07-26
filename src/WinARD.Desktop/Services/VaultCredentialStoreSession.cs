using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Security.Vault;

namespace WinARD.Desktop.Services;

public sealed class VaultCredentialStoreSession : ICredentialStore, IAsyncDisposable
{
    private readonly IVaultStorage _storage;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EncryptedCredentialVault? _vault;
    private bool _disposed;

    public VaultCredentialStoreSession(
        IVaultStorage storage,
        TimeProvider timeProvider,
        TimeSpan idleTimeout)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
        _idleTimeout = idleTimeout;
    }

    public bool IsUnlocked => Volatile.Read(ref _vault) is not null;

    public async ValueTask UnlockAsync(ISecret masterPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var old = Interlocked.Exchange(ref _vault, null);
            if (old is not null)
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }

            var exists = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false) is not null;
            var opened = exists
                ? await EncryptedCredentialVault.OpenAsync(
                    _storage, masterPassword, _timeProvider, _idleTimeout, cancellationToken).ConfigureAwait(false)
                : await EncryptedCredentialVault.CreateAsync(
                    _storage, masterPassword, _timeProvider, _idleTimeout, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _vault, opened);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask LockAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var vault = Interlocked.Exchange(ref _vault, null);
            if (vault is not null)
            {
                await vault.LockAsync().ConfigureAwait(false);
                await vault.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(CredentialReference reference, ISecret secret, CancellationToken cancellationToken)
    {
        try
        {
            await Vault.SaveAsync(reference, secret, cancellationToken).ConfigureAwait(false);
        }
        catch (VaultLockedException)
        {
            Volatile.Write(ref _vault, null);
            throw;
        }
    }

    public async ValueTask<ISecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken)
    {
        try
        {
            return await Vault.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (VaultLockedException)
        {
            Volatile.Write(ref _vault, null);
            throw;
        }
    }

    public async ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Vault.ReadSnapshotAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (VaultLockedException)
        {
            Volatile.Write(ref _vault, null);
            throw;
        }
    }

    public async ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Vault.CompareExchangeAsync(reference, expectedVersion, replacement, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VaultLockedException)
        {
            Volatile.Write(ref _vault, null);
            throw;
        }
    }

    public async ValueTask DeleteAsync(CredentialReference reference, CancellationToken cancellationToken)
    {
        try
        {
            await Vault.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (VaultLockedException)
        {
            Volatile.Write(ref _vault, null);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var vault = Interlocked.Exchange(ref _vault, null);
            if (vault is not null)
            {
                await vault.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private EncryptedCredentialVault Vault => Volatile.Read(ref _vault) ?? throw new VaultLockedException();
}
