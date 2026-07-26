using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Security.Secrets;

namespace WinARD.Security.Vault;

public interface IVaultStorage
{
    ValueTask<VaultStorageSnapshot?> ReadAsync(CancellationToken cancellationToken);

    ValueTask<VaultStorageWriteResult> CompareExchangeAsync(
        ReadOnlyMemory<byte> contents,
        string? expectedVersion,
        CancellationToken cancellationToken);
}

public sealed record VaultStorageSnapshot(byte[] Contents, string Version);

public sealed record VaultStorageWriteResult(bool Written, string? Version);

public sealed class VaultConcurrencyException : IOException
{
    public VaultConcurrencyException()
        : base("The credential vault changed in another instance.")
    {
    }
}

public sealed class VaultFormatException : IOException
{
    public VaultFormatException(string safeMessage)
        : base(safeMessage)
    {
    }
}

internal interface IVaultNonceSource
{
    void Fill(Span<byte> destination);
}

internal sealed class CryptographicVaultNonceSource : IVaultNonceSource
{
    public static CryptographicVaultNonceSource Instance { get; } = new();

    public void Fill(Span<byte> destination) =>
        RandomNumberGenerator.Fill(destination);
}

public sealed class FileVaultStorage(string path) : IVaultStorage
{
    private readonly string _path = Path.GetFullPath(
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Vault path cannot be blank.", nameof(path))
            : path);

    public async ValueTask<VaultStorageSnapshot?> ReadAsync(
        CancellationToken cancellationToken)
    {
        var contents = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (contents is null)
        {
            return null;
        }

        return new VaultStorageSnapshot(contents, VersionOf(contents));
    }

    public async ValueTask<VaultStorageWriteResult> CompareExchangeAsync(
        ReadOnlyMemory<byte> contents,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (contents.Length > VaultFileFormat.MaximumFileBytes)
        {
            throw new VaultFormatException("The vault file exceeds its size limit.");
        }

        var directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException("The vault path has no parent directory.");
        Directory.CreateDirectory(directory);
        var lockPath = _path + ".lock";
        await using var exclusive = await OpenLockAsync(lockPath, cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentVersion = current is null ? null : VersionOf(current);
            if (!string.Equals(currentVersion, expectedVersion, StringComparison.Ordinal))
            {
                return new VaultStorageWriteResult(false, currentVersion);
            }
        }
        finally
        {
            if (current is not null)
            {
                CryptographicOperations.ZeroMemory(current);
            }
        }

        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temporary, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, _path);
            }

            var versionBytes = contents.ToArray();
            try
            {
                return new VaultStorageWriteResult(true, VersionOf(versionBytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(versionBytes);
            }
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static async ValueTask<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[64 * 1024];
        using var contents = new MemoryStream();
        var total = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = (VaultFileFormat.MaximumFileBytes + 1) - total;
                var read = await stream
                    .ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return contents.ToArray();
                }

                total = checked(total + read);
                if (total > VaultFileFormat.MaximumFileBytes)
                {
                    throw new VaultFormatException("The vault file exceeds its size limit.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (total > contents.Capacity)
                {
                    var doubled = contents.Capacity == 0
                        ? buffer.Length
                        : checked(contents.Capacity * 2);
                    contents.Capacity = Math.Min(
                        VaultFileFormat.MaximumFileBytes,
                        Math.Max(total, doubled));
                }

                contents.Write(buffer, 0, read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (contents.TryGetBuffer(out var bufferedContents))
            {
                CryptographicOperations.ZeroMemory(
                    bufferedContents.AsSpan(0, checked((int)contents.Length)));
            }
        }
    }

    private async ValueTask<byte[]?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream)
        {
            return await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<FileStream> OpenLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                if (attempt == 49)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new VaultConcurrencyException();
    }

    private static string VersionOf(ReadOnlySpan<byte> contents) =>
        Convert.ToHexString(SHA256.HashData(contents));
}

public sealed class VaultLockedException : InvalidOperationException
{
    public VaultLockedException()
        : base("The credential vault is locked.")
    {
    }
}

public sealed class EncryptedCredentialVault :
    ICredentialStore,
    IAsyncDisposable
{
    private static readonly SemaphoreSlim KeyDerivationGate = new(1, 1);
    private readonly IVaultStorage _storage;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _disposeSync = new();
    private readonly VaultKdfParameters _kdf;
    private readonly byte[] _salt;
    private readonly byte[] _verifierTag;
    private readonly IVaultNonceSource _nonceSource;
    private readonly HashSet<string> _manifestNonceHistory = new(StringComparer.Ordinal);
    private string _storageVersion;
    private long _revision;
    private Dictionary<string, VaultEntry> _entries;
    private byte[]? _key;
    private ITimer? _idleTimer;
    private DateTimeOffset _lastActivity;
    private long _activityGeneration;
    private Task? _disposeTask;
    private bool _disposed;

    private EncryptedCredentialVault(
        IVaultStorage storage,
        TimeProvider timeProvider,
        TimeSpan idleTimeout,
        VaultDocument document,
        byte[] key,
        string storageVersion,
        IVaultNonceSource nonceSource)
    {
        _storage = storage;
        _timeProvider = timeProvider;
        _idleTimeout = idleTimeout;
        _kdf = document.Kdf;
        _salt = document.Salt;
        _verifierTag = document.VerifierTag;
        _revision = document.Revision;
        _storageVersion = storageVersion;
        _nonceSource = nonceSource;
        _manifestNonceHistory.Add(Convert.ToHexString(document.ManifestNonce));
        _entries = new Dictionary<string, VaultEntry>(document.Entries, StringComparer.Ordinal);
        _key = key;
        _lastActivity = timeProvider.GetUtcNow();
        ScheduleAutoLock(generation: 0, idleTimeout);
    }

    public static ValueTask<EncryptedCredentialVault> CreateAsync(
        IVaultStorage storage,
        ISecret masterPassword,
        TimeProvider timeProvider,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken) =>
        CreateAsync(
            storage,
            masterPassword,
            timeProvider,
            idleTimeout,
            CryptographicVaultNonceSource.Instance,
            cancellationToken);

    internal static async ValueTask<EncryptedCredentialVault> CreateAsync(
        IVaultStorage storage,
        ISecret masterPassword,
        TimeProvider timeProvider,
        TimeSpan idleTimeout,
        IVaultNonceSource nonceSource,
        CancellationToken cancellationToken)
    {
        ValidateArguments(storage, masterPassword, timeProvider, idleTimeout);
        ArgumentNullException.ThrowIfNull(nonceSource);
        if (await storage.ReadAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new IOException("A vault already exists in the selected storage.");
        }

        var salt = RandomNumberGenerator.GetBytes(VaultFileFormat.SaltSize);
        byte[]? key = null;
        try
        {
            key = await DeriveKeyAsync(
                masterPassword,
                salt,
                VaultKdfParameters.Default,
                cancellationToken).ConfigureAwait(false);
            var verifierTag = CreateVerifier(
                key,
                VaultKdfParameters.Default,
                salt);
            var unsignedDocument = new VaultDocument(
                VaultKdfParameters.Default,
                salt,
                verifierTag,
                Revision: 0,
                new Dictionary<string, VaultEntry>(StringComparer.Ordinal),
                new byte[VaultFileFormat.NonceSize],
                new byte[VaultFileFormat.TagSize]);
            var document = WithManifestTag(
                unsignedDocument,
                key,
                nonceSource,
                new HashSet<string>(StringComparer.Ordinal));
            byte[]? bytes = null;
            try
            {
                bytes = VaultFileFormat.Serialize(document);
                var write = await storage
                    .CompareExchangeAsync(bytes, expectedVersion: null, cancellationToken)
                    .ConfigureAwait(false);
                if (!write.Written || write.Version is null)
                {
                    throw new VaultConcurrencyException();
                }

                var result = new EncryptedCredentialVault(
                    storage,
                    timeProvider,
                    idleTimeout,
                    document,
                    key,
                    write.Version,
                    nonceSource);
                key = null;
                return result;
            }
            finally
            {
                if (bytes is not null)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }

                ClearManifestData(document);
            }
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    public static async ValueTask<EncryptedCredentialVault> OpenAsync(
        IVaultStorage storage,
        ISecret masterPassword,
        TimeProvider timeProvider,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        ValidateArguments(storage, masterPassword, timeProvider, idleTimeout);
        var snapshot = await storage.ReadAsync(cancellationToken).ConfigureAwait(false) ??
            throw new FileNotFoundException("The credential vault does not exist.");
        var bytes = snapshot.Contents;
        VaultDocument document;
        try
        {
            document = VaultFileFormat.Parse(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        byte[]? key = null;
        try
        {
            key = await DeriveKeyAsync(
                masterPassword,
                document.Salt,
                document.Kdf,
                cancellationToken).ConfigureAwait(false);
            ValidateVerifier(document, key);
            ValidateManifest(document, key);
            ValidateAllEntries(document, key);
            var result = new EncryptedCredentialVault(
                storage,
                timeProvider,
                idleTimeout,
                document,
                key,
                snapshot.Version,
                CryptographicVaultNonceSource.Instance);
            key = null;
            return result;
        }
        catch
        {
            ClearEntries(document.Entries.Values);
            CryptographicOperations.ZeroMemory(document.Salt);
            CryptographicOperations.ZeroMemory(document.VerifierTag);
            throw;
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            ClearManifestData(document);
        }
    }

    public async ValueTask SaveAsync(
        CredentialReference reference,
        ISecret secret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length > VaultFileFormat.MaximumSecretBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                "The secret exceeds the vault entry size limit.");
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var referenceText = reference.ToString();
            var plaintext = new byte[secret.Length];
            VaultEntry? replacement = null;
            try
            {
                secret.CopyTo(plaintext);
                replacement = Encrypt(referenceText, plaintext, _key!, _entries.Values);
                var next = new Dictionary<string, VaultEntry>(_entries, StringComparer.Ordinal)
                {
                    [referenceText] = replacement,
                };
                var document = BuildDocument(next, checked(_revision + 1));
                byte[]? bytes = null;
                try
                {
                    bytes = VaultFileFormat.Serialize(document);
                    var write = await _storage
                        .CompareExchangeAsync(bytes, _storageVersion, cancellationToken)
                        .ConfigureAwait(false);
                    if (!write.Written || write.Version is null)
                    {
                        throw new VaultConcurrencyException();
                    }

                    _storageVersion = write.Version;
                    _revision = document.Revision;
                }
                finally
                {
                    if (bytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }

                    ClearManifestData(document);
                }

                if (_entries.Remove(referenceText, out var previous))
                {
                    ClearEntry(previous);
                }

                _entries.Add(referenceText, replacement);
                replacement = null;
                Touch();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (replacement is not null)
                {
                    ClearEntry(replacement);
                }
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ISecret?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (!_entries.TryGetValue(reference.ToString(), out var entry))
            {
                Touch();
                return null;
            }

            var plaintext = Decrypt(entry, _key!, _kdf, _salt);
            try
            {
                Touch();
                return SecretBuffer.CopyFrom(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (!_entries.TryGetValue(reference.ToString(), out var entry))
            {
                Touch();
                return null;
            }

            var plaintext = Decrypt(entry, _key!, _kdf, _salt);
            SecretBuffer? secret = null;
            CredentialStoreVersion? version = null;
            try
            {
                Touch();
                secret = SecretBuffer.CopyFrom(plaintext);
                version = CreateEntryVersion(entry);
                var snapshot = new CredentialStoreSnapshot(secret, version);
                secret = null;
                version = null;
                return snapshot;
            }
            finally
            {
                secret?.Dispose();
                version?.Dispose();
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken)
    {
        using var write = await CompareExchangeWithVersionAsync(
            reference,
            expectedVersion,
            replacement,
            cancellationToken).ConfigureAwait(false);
        return write.Result;
    }

    public async ValueTask<CredentialStoreWriteResult> CompareExchangeWithVersionAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (replacement?.Length > VaultFileFormat.MaximumSecretBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(replacement),
                "The secret exceeds the vault entry size limit.");
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var referenceText = reference.ToString();
            var hasCurrent = _entries.TryGetValue(referenceText, out var current);
            if (hasCurrent != (expectedVersion is not null) ||
                (current is not null && !EntryVersionMatches(current, expectedVersion!)))
            {
                Touch();
                return new CredentialStoreWriteResult(
                    CredentialStoreCompareExchangeResult.Conflict,
                    writtenVersion: null);
            }

            var next = new Dictionary<string, VaultEntry>(_entries, StringComparer.Ordinal);
            byte[]? plaintext = null;
            VaultEntry? encryptedReplacement = null;
            try
            {
                if (replacement is null)
                {
                    next.Remove(referenceText);
                }
                else
                {
                    plaintext = new byte[replacement.Length];
                    replacement.CopyTo(plaintext);
                    encryptedReplacement = Encrypt(
                        referenceText,
                        plaintext,
                        _key!,
                        _entries.Values);
                    next[referenceText] = encryptedReplacement;
                }

                var document = BuildDocument(next, checked(_revision + 1));
                byte[]? bytes = null;
                try
                {
                    bytes = VaultFileFormat.Serialize(document);
                    var write = await _storage
                        .CompareExchangeAsync(bytes, _storageVersion, cancellationToken)
                        .ConfigureAwait(false);
                    if (!write.Written || write.Version is null)
                    {
                        Touch();
                        return new CredentialStoreWriteResult(
                            CredentialStoreCompareExchangeResult.Conflict,
                            writtenVersion: null);
                    }

                    _storageVersion = write.Version;
                    _revision = document.Revision;
                }
                finally
                {
                    if (bytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }

                    ClearManifestData(document);
                }

                if (_entries.Remove(referenceText, out var removed))
                {
                    ClearEntry(removed);
                }

                if (encryptedReplacement is not null)
                {
                    _entries.Add(referenceText, encryptedReplacement);
                    encryptedReplacement = null;
                }

                var writtenVersion = replacement is null
                    ? null
                    : CreateEntryVersion(_entries[referenceText]);
                Touch();
                return new CredentialStoreWriteResult(
                    CredentialStoreCompareExchangeResult.Succeeded,
                    writtenVersion);
            }
            finally
            {
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }

                if (encryptedReplacement is not null)
                {
                    ClearEntry(encryptedReplacement);
                }
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var referenceText = reference.ToString();
            if (!_entries.ContainsKey(referenceText))
            {
                Touch();
                return;
            }

            var next = new Dictionary<string, VaultEntry>(_entries, StringComparer.Ordinal);
            next.Remove(referenceText);
            var document = BuildDocument(next, checked(_revision + 1));
            byte[]? bytes = null;
            try
            {
                bytes = VaultFileFormat.Serialize(document);
                var write = await _storage
                    .CompareExchangeAsync(bytes, _storageVersion, cancellationToken)
                    .ConfigureAwait(false);
                if (!write.Written || write.Version is null)
                {
                    throw new VaultConcurrencyException();
                }

                _storageVersion = write.Version;
                _revision = document.Revision;
            }
            finally
            {
                if (bytes is not null)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }

                ClearManifestData(document);
            }

            if (_entries.Remove(referenceText, out var removed))
            {
                ClearEntry(removed);
            }

            Touch();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask LockAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            ClearKey();
            await DisposeTimerAsync().ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

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

    private async Task DisposeCoreAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClearKey();
            ClearEntries(_entries.Values);
            _entries.Clear();
            CryptographicOperations.ZeroMemory(_salt);
            CryptographicOperations.ZeroMemory(_verifierTag);
            await DisposeTimerAsync().ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
            _mutex.Dispose();
        }
    }

    private static void ValidateArguments(
        IVaultStorage storage,
        ISecret masterPassword,
        TimeProvider timeProvider,
        TimeSpan idleTimeout)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(masterPassword);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (masterPassword.Length is < 1 or > VaultFileFormat.MaximumSecretBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(masterPassword));
        }

        if (idleTimeout <= TimeSpan.Zero || idleTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        }
    }

    private static async ValueTask<byte[]> DeriveKeyAsync(
        ISecret masterPassword,
        byte[] salt,
        VaultKdfParameters kdf,
        CancellationToken cancellationToken)
    {
        await KeyDerivationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? password = null;
        try
        {
            password = new byte[masterPassword.Length];
            masterPassword.CopyTo(password);
            using var argon = new Argon2id(password)
            {
                Salt = salt.ToArray(),
                DegreeOfParallelism = kdf.Parallelism,
                Iterations = kdf.Iterations,
                MemorySize = kdf.MemoryKiB,
            };
            var derivation = argon.GetBytesAsync(32);
            try
            {
                return await derivation.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var abandonedResult = await derivation.ConfigureAwait(false);
                    CryptographicOperations.ZeroMemory(abandonedResult);
                }
                catch (Exception)
                {
                    // Observe the non-cancellable Argon2 operation before releasing
                    // the password buffer and concurrency gate.
                }

                throw;
            }
        }
        finally
        {
            if (password is not null)
            {
                CryptographicOperations.ZeroMemory(password);
            }

            KeyDerivationGate.Release();
        }
    }

    private VaultEntry Encrypt(
        string reference,
        byte[] plaintext,
        byte[] key,
        IEnumerable<VaultEntry> existing)
    {
        var usedNonces = new HashSet<string>(
            existing.Select(static entry => Convert.ToHexString(entry.Nonce)),
            StringComparer.Ordinal);
        usedNonces.Add(new string('0', VaultFileFormat.NonceSize * 2));
        usedNonces.UnionWith(_manifestNonceHistory);
        var nonce = GenerateUniqueNonce(_nonceSource, usedNonces);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[VaultFileFormat.TagSize];
        var associatedData = VaultFileFormat.AssociatedData(
            _kdf,
            _salt,
            reference,
            nonce,
            ciphertext.Length);
        try
        {
            using var aes = new AesGcm(key, VaultFileFormat.TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return new VaultEntry(reference, nonce, ciphertext, tag);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private static byte[] Decrypt(
        VaultEntry entry,
        byte[] key,
        VaultKdfParameters kdf,
        byte[] salt)
    {
        var plaintext = new byte[entry.Ciphertext.Length];
        var associatedData = VaultFileFormat.AssociatedData(
            kdf,
            salt,
            entry.Reference,
            entry.Nonce,
            entry.Ciphertext.Length);
        try
        {
            using var aes = new AesGcm(key, VaultFileFormat.TagSize);
            aes.Decrypt(
                entry.Nonce,
                entry.Ciphertext,
                entry.Tag,
                plaintext,
                associatedData);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private static CredentialStoreVersion CreateEntryVersion(VaultEntry entry)
    {
        var digest = CreateEntryVersionDigest(entry);
        try
        {
            return CredentialStoreVersion.CopyFrom(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool EntryVersionMatches(
        VaultEntry entry,
        CredentialStoreVersion expectedVersion)
    {
        var digest = CreateEntryVersionDigest(entry);
        try
        {
            return expectedVersion.FixedTimeEquals(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static byte[] CreateEntryVersionDigest(VaultEntry entry)
    {
        var material = new byte[
            entry.Nonce.Length + entry.Ciphertext.Length + entry.Tag.Length];
        try
        {
            entry.Nonce.CopyTo(material, 0);
            entry.Ciphertext.CopyTo(material, entry.Nonce.Length);
            entry.Tag.CopyTo(material, entry.Nonce.Length + entry.Ciphertext.Length);
            return SHA256.HashData(material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static void ValidateAllEntries(VaultDocument document, byte[] key)
    {
        foreach (var entry in document.Entries.Values)
        {
            var plaintext = Decrypt(entry, key, document.Kdf, document.Salt);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] CreateVerifier(
        byte[] key,
        VaultKdfParameters kdf,
        byte[] salt)
    {
        var tag = new byte[VaultFileFormat.TagSize];
        var associatedData = VaultFileFormat.HeaderAssociatedData(kdf, salt);
        try
        {
            using var aes = new AesGcm(key, VaultFileFormat.TagSize);
            aes.Encrypt(
                new byte[VaultFileFormat.NonceSize],
                ReadOnlySpan<byte>.Empty,
                Span<byte>.Empty,
                tag,
                associatedData);
            return tag;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(tag);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private static void ValidateVerifier(VaultDocument document, byte[] key)
    {
        var associatedData = VaultFileFormat.HeaderAssociatedData(
            document.Kdf,
            document.Salt);
        try
        {
            using var aes = new AesGcm(key, VaultFileFormat.TagSize);
            aes.Decrypt(
                new byte[VaultFileFormat.NonceSize],
                ReadOnlySpan<byte>.Empty,
                document.VerifierTag,
                Span<byte>.Empty,
                associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private VaultDocument BuildDocument(
        IReadOnlyDictionary<string, VaultEntry> entries,
        long revision) =>
        WithManifestTag(
            new VaultDocument(
                _kdf,
                _salt,
                _verifierTag,
                revision,
                entries,
                new byte[VaultFileFormat.NonceSize],
                new byte[VaultFileFormat.TagSize]),
            _key!,
            _nonceSource,
            _manifestNonceHistory);

    private static VaultDocument WithManifestTag(
        VaultDocument document,
        byte[] key,
        IVaultNonceSource nonceSource,
        HashSet<string> nonceHistory)
    {
        var manifest = VaultFileFormat.BuildManifest(document);
        var transferred = false;
        try
        {
            var forbidden = new HashSet<string>(nonceHistory, StringComparer.Ordinal)
            {
                new string('0', VaultFileFormat.NonceSize * 2),
            };
            forbidden.UnionWith(
                document.Entries.Values.Select(
                    static entry => Convert.ToHexString(entry.Nonce)));
            var nonce = GenerateUniqueNonce(nonceSource, forbidden);
            nonceHistory.Add(Convert.ToHexString(nonce));
            var tag = CreateManifestTag(key, manifest, nonce);
            var authenticated = document with
            {
                ManifestNonce = nonce,
                ManifestTag = tag,
                ManifestData = manifest,
            };
            transferred = true;
            return authenticated;
        }
        finally
        {
            if (!transferred)
            {
                CryptographicOperations.ZeroMemory(manifest);
            }
        }
    }

    private static void ClearManifestData(VaultDocument document)
    {
        if (document.ManifestData is not null)
        {
            CryptographicOperations.ZeroMemory(document.ManifestData);
        }
    }

    private static byte[] CreateManifestTag(
        byte[] key,
        byte[] manifest,
        byte[] nonce)
    {
        var tag = new byte[VaultFileFormat.TagSize];
        try
        {
            using var aes = new AesGcm(key, VaultFileFormat.TagSize);
            aes.Encrypt(
                nonce,
                ReadOnlySpan<byte>.Empty,
                Span<byte>.Empty,
                tag,
                manifest);
            return tag;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(tag);
            throw;
        }
    }

    private static void ValidateManifest(VaultDocument document, byte[] key)
    {
        var manifest = document.ManifestData ?? VaultFileFormat.BuildManifest(document);
        try
        {
            using var aes = new AesGcm(key, VaultFileFormat.TagSize);
            aes.Decrypt(
                document.ManifestNonce,
                ReadOnlySpan<byte>.Empty,
                document.ManifestTag,
                Span<byte>.Empty,
                manifest);
        }
        finally
        {
            if (document.ManifestData is null)
            {
                CryptographicOperations.ZeroMemory(manifest);
            }
        }
    }

    private static byte[] GenerateUniqueNonce(
        IVaultNonceSource source,
        HashSet<string> forbidden)
    {
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var nonce = new byte[VaultFileFormat.NonceSize];
            source.Fill(nonce);
            if (forbidden.Add(Convert.ToHexString(nonce)))
            {
                return nonce;
            }

            CryptographicOperations.ZeroMemory(nonce);
        }

        throw new CryptographicException(
            "Unable to generate a unique vault authentication nonce.");
    }

    private void Touch()
    {
        _lastActivity = _timeProvider.GetUtcNow();
        var generation = checked(++_activityGeneration);
        ScheduleAutoLock(generation, _idleTimeout);
    }

    private void BeginAutoLock(long generation)
    {
        _ = AutoLockAsync(generation);
    }

    private async Task AutoLockAsync(long generation)
    {
        try
        {
            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _key is null || generation != _activityGeneration)
                {
                    return;
                }

                var remaining = (_lastActivity + _idleTimeout) - _timeProvider.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                {
                    ScheduleAutoLock(generation, remaining);
                    return;
                }

                ClearKey();
                await DisposeTimerAsync().ConfigureAwait(false);
            }
            finally
            {
                _mutex.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ScheduleAutoLock(long generation, TimeSpan dueTime)
    {
        var timer = _timeProvider.CreateTimer(
            static state =>
            {
                var timerState = (AutoLockTimerState)state!;
                timerState.Owner.BeginAutoLock(timerState.Generation);
            },
            new AutoLockTimerState(this, generation),
            dueTime,
            Timeout.InfiniteTimeSpan);
        var previous = Interlocked.Exchange(ref _idleTimer, timer);
        previous?.Dispose();
    }

    private async ValueTask DisposeTimerAsync()
    {
        var timer = Interlocked.Exchange(ref _idleTimer, null);
        if (timer is not null)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_key is null)
        {
            throw new VaultLockedException();
        }
    }

    private void ClearKey()
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }

    private static void ClearEntries(IEnumerable<VaultEntry> entries)
    {
        foreach (var entry in entries)
        {
            ClearEntry(entry);
        }
    }

    private static void ClearEntry(VaultEntry entry)
    {
        CryptographicOperations.ZeroMemory(entry.Nonce);
        CryptographicOperations.ZeroMemory(entry.Ciphertext);
        CryptographicOperations.ZeroMemory(entry.Tag);
    }

    private sealed record AutoLockTimerState(
        EncryptedCredentialVault Owner,
        long Generation);
}
