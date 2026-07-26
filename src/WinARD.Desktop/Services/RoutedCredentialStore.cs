using WinARD.Application.Ports;
using WinARD.Domain.Security;

namespace WinARD.Desktop.Services;

public sealed class RoutedCredentialStore : IPurposeAwareCredentialStore, IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, ICredentialStore> _stores;
    private readonly CredentialPromptService _promptService;

    public RoutedCredentialStore(
        ICredentialStore windowsStore,
        ICredentialStore vaultStore,
        TransientCredentialStore transientStore,
        CredentialPromptService promptService)
    {
        _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
        _stores = new Dictionary<string, ICredentialStore>(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = windowsStore ?? throw new ArgumentNullException(nameof(windowsStore)),
            ["vault"] = vaultStore ?? throw new ArgumentNullException(nameof(vaultStore)),
            ["transient"] = transientStore ?? throw new ArgumentNullException(nameof(transientStore)),
        };
    }

    public ValueTask SaveAsync(CredentialReference reference, ISecret secret, CancellationToken cancellationToken) =>
        Store(reference).SaveAsync(reference, secret, cancellationToken);

    public async ValueTask<ISecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (string.Equals(reference.Store, "ask", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "每次询问的凭据读取必须指定提示用途。");
        }

        return await Store(reference).ReadAsync(reference, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ISecret?> ReadForPromptAsync(
        CredentialPromptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.Equals(request.Reference.Store, "ask", StringComparison.OrdinalIgnoreCase))
        {
            return await _promptService.PromptReferenceAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        return await Store(request.Reference)
            .ReadAsync(request.Reference, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(CredentialReference reference, CancellationToken cancellationToken) =>
        Store(reference).ReadSnapshotAsync(reference, cancellationToken);

    public ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken) =>
        Store(reference).CompareExchangeAsync(reference, expectedVersion, replacement, cancellationToken);

    public ValueTask<CredentialStoreWriteResult> CompareExchangeWithVersionAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken) =>
        Store(reference).CompareExchangeWithVersionAsync(
            reference,
            expectedVersion,
            replacement,
            cancellationToken);

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

public interface ITransientCredentialStore : ICredentialStore
{
}

public sealed class TransientCredentialStore : ITransientCredentialStore, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<CredentialReference, Entry> _secrets = [];
    private long _nextVersion;

    public ValueTask SaveAsync(CredentialReference reference, ISecret secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(secret);
        lock (_gate)
        {
            var clone = secret.Clone();
            if (_secrets.Remove(reference, out var old))
            {
                old.Secret.Dispose();
            }

            _secrets[reference] = new Entry(clone, ++_nextVersion);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ISecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _secrets.TryGetValue(reference, out var entry)
                    ? entry.Secret.Clone()
                    : null);
        }
    }

    public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(CredentialReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_secrets.TryGetValue(reference, out var entry))
            {
                return ValueTask.FromResult<CredentialStoreSnapshot?>(null);
            }

            return ValueTask.FromResult<CredentialStoreSnapshot?>(new CredentialStoreSnapshot(
                entry.Secret.Clone(),
                Version(entry.Version)));
        }
    }

    public async ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken)
    {
        using var write = await CompareExchangeWithVersionAsync(
            reference, expectedVersion, replacement, cancellationToken).ConfigureAwait(false);
        return write.Result;
    }

    public ValueTask<CredentialStoreWriteResult> CompareExchangeWithVersionAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        lock (_gate)
        {
            var hasCurrent = _secrets.TryGetValue(reference, out var current);
            using var currentVersion = hasCurrent ? Version(current!.Version) : null;
            var matches = hasCurrent
                ? expectedVersion is not null && expectedVersion.FixedTimeEquals(currentVersion!)
                : expectedVersion is null;
            if (!matches)
            {
                return ValueTask.FromResult(new CredentialStoreWriteResult(
                    CredentialStoreCompareExchangeResult.Conflict,
                    writtenVersion: null));
            }

            if (replacement is null)
            {
                if (hasCurrent)
                {
                    _secrets.Remove(reference);
                    current!.Secret.Dispose();
                }

                return ValueTask.FromResult(new CredentialStoreWriteResult(
                    CredentialStoreCompareExchangeResult.Succeeded,
                    writtenVersion: null));
            }

            var clone = replacement.Clone();
            current?.Secret.Dispose();
            var version = ++_nextVersion;
            _secrets[reference] = new Entry(clone, version);
            return ValueTask.FromResult(new CredentialStoreWriteResult(
                CredentialStoreCompareExchangeResult.Succeeded,
                Version(version)));
        }
    }

    public ValueTask DeleteAsync(CredentialReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_secrets.Remove(reference, out var entry))
            {
                entry.Secret.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _secrets.Values)
            {
                entry.Secret.Dispose();
            }

            _secrets.Clear();
        }
    }

    private static CredentialStoreVersion Version(long value) =>
        CredentialStoreVersion.CopyFrom(BitConverter.GetBytes(value));

    private sealed record Entry(ISecret Secret, long Version);
}
