using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Security.Secrets;

namespace WinARD.Security.WindowsCredentials;

public sealed class WindowsCredentialStore : ICredentialStore
{
    internal const int MaximumBlobBytes = 2560;
    internal const int RevisionOffset = 9;
    internal const int RevisionSize = 16;
    internal const int HeaderSize = 29;
    internal const int MaximumSecretBytes = MaximumBlobBytes - HeaderSize;
    private const int FormatVersionOffset = 8;
    private const int SecretLengthOffset = 25;
    private const byte CurrentFormatVersion = 1;
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
        var target = TargetName(reference);
        var gate = TargetGates.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? value = null;
        try
        {
            value = CreateBlob(secret, nameof(secret));
            _native.Write(target, value);
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
            if (value is null)
            {
                return null;
            }

            var secretLength = ValidateBlob(value);
            return SecretBuffer.CopyFrom(value.AsSpan(HeaderSize, secretLength));
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
        SecretBuffer? secret = null;
        CredentialStoreVersion? snapshotVersion = null;
        try
        {
            value = _native.Read(target);
            if (value is null)
            {
                return null;
            }

            var secretLength = ValidateBlob(value);
            secret = SecretBuffer.CopyFrom(value.AsSpan(HeaderSize, secretLength));
            snapshotVersion = CredentialStoreVersion.CopyFrom(
                value.AsSpan(RevisionOffset, RevisionSize));
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
        var target = TargetName(reference);
        var gate = TargetGates.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? current = null;
        byte[]? replacementBytes = null;
        try
        {
            current = _native.Read(target);
            var matches = current is null
                ? expectedVersion is null
                : expectedVersion is not null && VersionMatches(current, expectedVersion);
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
                replacementBytes = CreateBlob(replacement, nameof(replacement));
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
        CredentialStoreVersion expectedVersion)
    {
        ValidateBlob(current);
        return expectedVersion.FixedTimeEquals(
            current.AsSpan(RevisionOffset, RevisionSize));
    }

    private static byte[] CreateBlob(ISecret secret, string parameterName)
    {
        var secretLength = secret.Length;
        if (secretLength > MaximumSecretBytes)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Windows credentials are limited to {MaximumSecretBytes} secret bytes after the WinARD version header.");
        }

        var blob = new byte[HeaderSize + secretLength];
        try
        {
            BlobMagic.CopyTo(blob);
            blob[FormatVersionOffset] = CurrentFormatVersion;
            RandomNumberGenerator.Fill(blob.AsSpan(RevisionOffset, RevisionSize));
            BinaryPrimitives.WriteInt32LittleEndian(
                blob.AsSpan(SecretLengthOffset, sizeof(int)),
                secretLength);
            secret.CopyTo(blob.AsSpan(HeaderSize, secretLength));
            return blob;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(blob);
            throw;
        }
    }

    private static int ValidateBlob(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < HeaderSize ||
            !blob[..BlobMagic.Length].SequenceEqual(BlobMagic) ||
            blob[FormatVersionOffset] != CurrentFormatVersion)
        {
            throw MalformedBlob();
        }

        var secretLength = BinaryPrimitives.ReadInt32LittleEndian(
            blob.Slice(SecretLengthOffset, sizeof(int)));
        if (secretLength < 0 ||
            secretLength > MaximumSecretBytes ||
            blob.Length != HeaderSize + secretLength)
        {
            throw MalformedBlob();
        }

        return secretLength;
    }

    private static ReadOnlySpan<byte> BlobMagic => "WinARD-C"u8;

    private static InvalidDataException MalformedBlob() =>
        new(
            "The Windows credential is not a supported versioned WinARD credential blob. " +
            "Legacy raw credential blobs are intentionally rejected.");
}
