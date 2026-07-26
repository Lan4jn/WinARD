using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Security.Secrets;

namespace WinARD.Security.Vault;

public interface IVaultStorage
{
    ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken);

    ValueTask WriteAtomicallyAsync(
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken);
}

public sealed class FileVaultStorage(string path) : IVaultStorage
{
    private readonly string _path = Path.GetFullPath(
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Vault path cannot be blank.", nameof(path))
            : path);

    public async ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var info = new FileInfo(_path);
        if (info.Length > VaultFileFormat.MaximumFileBytes)
        {
            throw new InvalidDataException("The vault file exceeds its size limit.");
        }

        return await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAtomicallyAsync(
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        if (contents.Length > VaultFileFormat.MaximumFileBytes)
        {
            throw new InvalidDataException("The vault file exceeds its size limit.");
        }

        var directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException("The vault path has no parent directory.");
        Directory.CreateDirectory(directory);
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
    private readonly IVaultStorage _storage;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _disposeSync = new();
    private readonly VaultKdfParameters _kdf;
    private readonly byte[] _salt;
    private readonly byte[] _verifierTag;
    private Dictionary<string, VaultEntry> _entries;
    private byte[]? _key;
    private ITimer? _idleTimer;
    private Task? _disposeTask;
    private bool _disposed;

    private EncryptedCredentialVault(
        IVaultStorage storage,
        TimeProvider timeProvider,
        TimeSpan idleTimeout,
        VaultDocument document,
        byte[] key)
    {
        _storage = storage;
        _timeProvider = timeProvider;
        _idleTimeout = idleTimeout;
        _kdf = document.Kdf;
        _salt = document.Salt;
        _verifierTag = document.VerifierTag;
        _entries = new Dictionary<string, VaultEntry>(document.Entries, StringComparer.Ordinal);
        _key = key;
        _idleTimer = timeProvider.CreateTimer(
            static state => ((EncryptedCredentialVault)state!).BeginAutoLock(),
            this,
            idleTimeout,
            Timeout.InfiniteTimeSpan);
    }

    public static async ValueTask<EncryptedCredentialVault> CreateAsync(
        IVaultStorage storage,
        ISecret masterPassword,
        TimeProvider timeProvider,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        ValidateArguments(storage, masterPassword, timeProvider, idleTimeout);
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
            var document = new VaultDocument(
                VaultKdfParameters.Default,
                salt,
                verifierTag,
                new Dictionary<string, VaultEntry>(StringComparer.Ordinal));
            var bytes = VaultFileFormat.Serialize(document);
            try
            {
                await storage.WriteAtomicallyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            var result = new EncryptedCredentialVault(
                storage,
                timeProvider,
                idleTimeout,
                document,
                key);
            key = null;
            return result;
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
        var bytes = await storage.ReadAsync(cancellationToken).ConfigureAwait(false) ??
            throw new FileNotFoundException("The credential vault does not exist.");
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
            ValidateAllEntries(document, key);
            var result = new EncryptedCredentialVault(
                storage,
                timeProvider,
                idleTimeout,
                document,
                key);
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
                var bytes = VaultFileFormat.Serialize(
                    new VaultDocument(_kdf, _salt, _verifierTag, next));
                try
                {
                    await _storage
                        .WriteAtomicallyAsync(bytes, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
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
            var bytes = VaultFileFormat.Serialize(
                new VaultDocument(_kdf, _salt, _verifierTag, next));
            try
            {
                await _storage.WriteAtomicallyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
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
        cancellationToken.ThrowIfCancellationRequested();
        var password = new byte[masterPassword.Length];
        try
        {
            masterPassword.CopyTo(password);
            using var argon = new Argon2id(password)
            {
                Salt = salt.ToArray(),
                DegreeOfParallelism = kdf.Parallelism,
                Iterations = kdf.Iterations,
                MemorySize = kdf.MemoryKiB,
            };
            var derivation = argon.GetBytesAsync(32);
            return await derivation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
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
        byte[] nonce;
        do
        {
            nonce = RandomNumberGenerator.GetBytes(VaultFileFormat.NonceSize);
        }
        while (!usedNonces.Add(Convert.ToHexString(nonce)));

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

    private void Touch()
    {
        _idleTimer?.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
    }

    private void BeginAutoLock()
    {
        _ = AutoLockAsync();
    }

    private async Task AutoLockAsync()
    {
        try
        {
            await LockAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
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
}
