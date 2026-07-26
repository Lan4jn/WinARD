using System.Collections.Concurrent;
using WinARD.Application.Ports;
using WinARD.Domain.Security;

namespace WinARD.Desktop.Services;

public sealed class RoutedCredentialStore : ICredentialStore, IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, ICredentialStore> _stores;

    public RoutedCredentialStore(
        ICredentialStore windowsStore,
        ICredentialStore vaultStore,
        TransientCredentialStore transientStore)
    {
        _stores = new Dictionary<string, ICredentialStore>(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = windowsStore ?? throw new ArgumentNullException(nameof(windowsStore)),
            ["vault"] = vaultStore ?? throw new ArgumentNullException(nameof(vaultStore)),
            ["transient"] = transientStore ?? throw new ArgumentNullException(nameof(transientStore)),
        };
    }

    public ValueTask SaveAsync(CredentialReference reference, ISecret secret, CancellationToken cancellationToken) =>
        Store(reference).SaveAsync(reference, secret, cancellationToken);

    public ValueTask<ISecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken) =>
        Store(reference).ReadAsync(reference, cancellationToken);

    public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(CredentialReference reference, CancellationToken cancellationToken) =>
        Store(reference).ReadSnapshotAsync(reference, cancellationToken);

    public ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken) =>
        Store(reference).CompareExchangeAsync(reference, expectedVersion, replacement, cancellationToken);

    public ValueTask DeleteAsync(CredentialReference reference, CancellationToken cancellationToken) =>
        Store(reference).DeleteAsync(reference, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores.Values.Distinct())
        {
            if (store is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (store is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private ICredentialStore Store(CredentialReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (string.Equals(reference.Store, "ask", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("此连接配置为每次询问凭据。");
        }

        return _stores.TryGetValue(reference.Store, out var store)
            ? store
            : throw new InvalidOperationException("连接配置引用了不受支持的凭据存储。");
    }
}

public sealed class TransientCredentialStore : ICredentialStore, IDisposable
{
    private readonly ConcurrentDictionary<CredentialReference, ISecret> _secrets = new();

    public ValueTask SaveAsync(CredentialReference reference, ISecret secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(secret);
        var clone = secret.Clone();
        if (_secrets.TryGetValue(reference, out var old))
        {
            old.Dispose();
        }

        _secrets[reference] = clone;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ISecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_secrets.TryGetValue(reference, out var secret) ? secret.Clone() : null);
    }

    public async ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(CredentialReference reference, CancellationToken cancellationToken)
    {
        using var secret = await ReadAsync(reference, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            return null;
        }

        return new CredentialStoreSnapshot(secret.Clone(), CredentialStoreVersion.CopyFrom([1]));
    }

    public async ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken)
    {
        if (replacement is null)
        {
            await DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SaveAsync(reference, replacement, cancellationToken).ConfigureAwait(false);
        }

        return CredentialStoreCompareExchangeResult.Succeeded;
    }

    public ValueTask DeleteAsync(CredentialReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_secrets.TryRemove(reference, out var secret))
        {
            secret.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var secret in _secrets.Values)
        {
            secret.Dispose();
        }

        _secrets.Clear();
    }
}
