using System.Collections.Concurrent;
using System.Security.Cryptography;
using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Security.Secrets;

namespace WinARD.Security.WindowsCredentials;

public sealed class WindowsCredentialStore : ICredentialStore
{
    internal const int MaximumBlobBytes = 2560;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TargetGates =
        new(StringComparer.Ordinal);

    private readonly IWindowsCredentialApi _native;

    public WindowsCredentialStore()
        : this(new WindowsCredentialApi())
    {
    }

    internal WindowsCredentialStore(IWindowsCredentialApi native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public async ValueTask SaveAsync(
        CredentialReference reference,
        ISecret secret,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(secret);
        cancellationToken.ThrowIfCancellationRequested();
        if (secret.Length > MaximumBlobBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                "Windows Credential Manager limits generic credential blobs to 2560 bytes.");
        }

        var target = TargetName(reference);
        var gate = TargetGates.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var value = new byte[secret.Length];
        try
        {
            secret.CopyTo(value);
            _native.Write(target, value);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
            gate.Release();
        }
    }

    public async ValueTask<ISecret?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var target = TargetName(reference);
        var gate = TargetGates.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? value = null;
        try
        {
            value = _native.Read(target);
            return value is null ? null : SecretBuffer.CopyFrom(value);
        }
        finally
        {
            if (value is not null)
            {
                CryptographicOperations.ZeroMemory(value);
            }

            gate.Release();
        }
    }

    public async ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var target = TargetName(reference);
        var gate = TargetGates.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? value = null;
        byte[]? version = null;
        SecretBuffer? secret = null;
        CredentialStoreVersion? snapshotVersion = null;
        try
        {
            value = _native.Read(target);
            if (value is null)
            {
                return null;
            }

            version = SHA256.HashData(value);
            secret = SecretBuffer.CopyFrom(value);
            snapshotVersion = CredentialStoreVersion.CopyFrom(version);
            var snapshot = new CredentialStoreSnapshot(secret, snapshotVersion);
            secret = null;
            snapshotVersion = null;
            return snapshot;
        }
        finally
        {
            secret?.Dispose();
            snapshotVersion?.Dispose();
            if (value is not null)
            {
                CryptographicOperations.ZeroMemory(value);
            }

            if (version is not null)
            {
                CryptographicOperations.ZeroMemory(version);
            }

            gate.Release();
        }
    }

    public async ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (replacement?.Length > MaximumBlobBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(replacement),
                "Windows Credential Manager limits generic credential blobs to 2560 bytes.");
        }

        var target = TargetName(reference);
        var gate = TargetGates.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? current = null;
        byte[]? currentVersion = null;
        byte[]? replacementBytes = null;
        try
        {
            current = _native.Read(target);
            var matches = current is null
                ? expectedVersion is null
                : expectedVersion is not null && VersionMatches(current, expectedVersion, out currentVersion);
            if (!matches)
            {
                return CredentialStoreCompareExchangeResult.Conflict;
            }

            if (replacement is null)
            {
                _native.Delete(target);
            }
            else
            {
                replacementBytes = new byte[replacement.Length];
                replacement.CopyTo(replacementBytes);
                _native.Write(target, replacementBytes);
            }

            return CredentialStoreCompareExchangeResult.Succeeded;
        }
        finally
        {
            if (current is not null)
            {
                CryptographicOperations.ZeroMemory(current);
            }

            if (currentVersion is not null)
            {
                CryptographicOperations.ZeroMemory(currentVersion);
            }

            if (replacementBytes is not null)
            {
                CryptographicOperations.ZeroMemory(replacementBytes);
            }

            gate.Release();
        }
    }

    public async ValueTask DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var target = TargetName(reference);
        var gate = TargetGates.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _native.Delete(target);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static string TargetName(CredentialReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return $"WinARD/{Uri.EscapeDataString(reference.Store)}/{Uri.EscapeDataString(reference.Key)}";
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows Credential Manager is only available on Windows.");
        }
    }

    private static bool VersionMatches(
        byte[] current,
        CredentialStoreVersion expectedVersion,
        out byte[] digest)
    {
        digest = SHA256.HashData(current);
        return expectedVersion.FixedTimeEquals(digest);
    }
}
