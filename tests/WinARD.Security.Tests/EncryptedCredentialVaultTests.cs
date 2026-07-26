using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using WinARD.Domain.Security;
using WinARD.Security.Secrets;
using WinARD.Security.Vault;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Security.Tests;

public sealed class EncryptedCredentialVaultTests
{
    private static readonly CredentialReference Reference =
        CredentialReference.Create("vault", "device/alpha");

    [Fact]
    public async Task Saves_and_reads_without_persisting_plaintext()
    {
        var storage = new InMemoryVaultStorage();
        using var master = Utf8("master-52e6");
        await using var vault = await EncryptedCredentialVault.CreateAsync(
            storage,
            master,
            TimeProvider.System,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        using var secret = Utf8("credential-a1c3");

        await vault.SaveAsync(Reference, secret, CancellationToken.None);
        using var read = await vault.ReadAsync(Reference, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal("credential-a1c3", ReadUtf8(read!));
        Assert.False(
            storage.Bytes!.AsSpan().IndexOf(
                Encoding.UTF8.GetBytes("credential-a1c3")) >= 0);
    }

    [Fact]
    public async Task Wrong_master_password_cannot_open_an_empty_vault()
    {
        var storage = new InMemoryVaultStorage();
        using (var master = Utf8("master-52e6"))
        {
            await using var vault = await EncryptedCredentialVault.CreateAsync(
                storage,
                master,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
        }

        using var wrongMaster = Utf8("wrong-master-e8a2");
        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => EncryptedCredentialVault.OpenAsync(
                storage,
                wrongMaster,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Tamper_and_trailing_data_are_rejected_before_open_completes()
    {
        var storage = await CreateStoredVaultAsync();
        storage.Bytes![^1] ^= 0x40;
        using var master = Utf8("master-52e6");

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => EncryptedCredentialVault.OpenAsync(
                storage,
                master,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                CancellationToken.None).AsTask());

        storage = await CreateStoredVaultAsync();
        storage.Bytes = [.. storage.Bytes!, 0x7f];
        using var secondMaster = Utf8("master-52e6");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => EncryptedCredentialVault.OpenAsync(
                storage,
                secondMaster,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Manifest_rejects_zeroed_count_with_truncation_entry_deletion_and_reordering()
    {
        var storage = await CreateStoredVaultWithTwoEntriesAsync();
        var original = storage.Bytes!.ToArray();
        var countOffset = 64;
        storage.Bytes =
        [
            .. original.AsSpan(0, countOffset),
            0, 0, 0, 0,
            .. original.AsSpan(
                original.Length - VaultFileFormat.NonceSize - VaultFileFormat.TagSize),
        ];
        await AssertTamperedAsync(storage);

        storage.Bytes = original[..^20];
        await AssertTamperedAsync(storage);

        var ranges = EntryRanges(original);
        storage.Bytes =
        [
            .. original.AsSpan(0, ranges[0].Start),
            .. original.AsSpan(ranges[1].Start, ranges[1].Length),
            .. original.AsSpan(ranges[0].Start, ranges[0].Length),
            .. original.AsSpan(
                original.Length - VaultFileFormat.NonceSize - VaultFileFormat.TagSize),
        ];
        await AssertTamperedAsync(storage);

        storage.Bytes =
        [
            .. original.AsSpan(0, ranges[0].Start),
            .. original.AsSpan(ranges[0].Start, ranges[0].Length),
            .. original.AsSpan(ranges[0].Start, ranges[0].Length),
            .. original.AsSpan(
                original.Length - VaultFileFormat.NonceSize - VaultFileFormat.TagSize),
        ];
        await AssertTamperedAsync(storage);
    }

    [Fact]
    public async Task Concurrent_instances_use_compare_exchange_without_lost_updates()
    {
        var storage = await CreateStoredVaultAsync();
        using var masterA = Utf8("master-52e6");
        using var masterB = Utf8("master-52e6");
        await using var first = await EncryptedCredentialVault.OpenAsync(
            storage, masterA, TimeProvider.System, TimeSpan.FromMinutes(5), CancellationToken.None);
        await using var second = await EncryptedCredentialVault.OpenAsync(
            storage, masterB, TimeProvider.System, TimeSpan.FromMinutes(5), CancellationToken.None);
        var secondReference = CredentialReference.Create("vault", "device/bravo");
        using var value = Utf8("concurrent-value-315f");

        await first.DeleteAsync(Reference, CancellationToken.None);
        await Assert.ThrowsAsync<VaultConcurrencyException>(
            () => second.SaveAsync(secondReference, value, CancellationToken.None).AsTask());

        using var reopenMaster = Utf8("master-52e6");
        await using var reopened = await EncryptedCredentialVault.OpenAsync(
            storage,
            reopenMaster,
            TimeProvider.System,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        Assert.Null(await reopened.ReadAsync(Reference, CancellationToken.None));
        Assert.Null(await reopened.ReadAsync(secondReference, CancellationToken.None));
    }

    [Fact]
    public async Task Independent_file_storages_use_same_file_compare_exchange()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"winard-vault-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "credentials.vault");
        try
        {
            var firstStorage = new FileVaultStorage(path);
            var secondStorage = new FileVaultStorage(path);
            using (var createMaster = Utf8("master-52e6"))
            {
                await using var created = await EncryptedCredentialVault.CreateAsync(
                    firstStorage,
                    createMaster,
                    TimeProvider.System,
                    TimeSpan.FromMinutes(5),
                    CancellationToken.None);
                using var initial = Utf8("initial-value-7a8b");
                await created.SaveAsync(Reference, initial, CancellationToken.None);
            }

            using var masterA = Utf8("master-52e6");
            using var masterB = Utf8("master-52e6");
            await using var first = await EncryptedCredentialVault.OpenAsync(
                firstStorage,
                masterA,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            await using var second = await EncryptedCredentialVault.OpenAsync(
                secondStorage,
                masterB,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            var secondReference = CredentialReference.Create("vault", "device/bravo");
            using var value = Utf8("concurrent-value-315f");

            await first.DeleteAsync(Reference, CancellationToken.None);
            await Assert.ThrowsAsync<VaultConcurrencyException>(
                () => second.SaveAsync(secondReference, value, CancellationToken.None).AsTask());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Every_manifest_serialization_uses_a_distinct_nonce()
    {
        var storage = new InMemoryVaultStorage();
        using var master = Utf8("master-52e6");
        await using var vault = await EncryptedCredentialVault.CreateAsync(
            storage,
            master,
            TimeProvider.System,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        var initial = VaultFileFormat.Parse(storage.Bytes!).ManifestNonce;
        using var first = Utf8("nonce-value-1a2b");
        await vault.SaveAsync(Reference, first, CancellationToken.None);
        var afterFirst = VaultFileFormat.Parse(storage.Bytes!).ManifestNonce;
        using var second = Utf8("nonce-value-3c4d");
        await vault.SaveAsync(Reference, second, CancellationToken.None);
        var afterSecond = VaultFileFormat.Parse(storage.Bytes!).ManifestNonce;

        Assert.NotEqual(initial, afterFirst);
        Assert.NotEqual(afterFirst, afterSecond);
        Assert.NotEqual(initial, afterSecond);
    }

    [Fact]
    public async Task Forced_nonce_collisions_are_retried()
    {
        var nonceA = Enumerable.Repeat((byte)0x11, VaultFileFormat.NonceSize).ToArray();
        var nonceB = Enumerable.Repeat((byte)0x22, VaultFileFormat.NonceSize).ToArray();
        var nonceC = Enumerable.Repeat((byte)0x33, VaultFileFormat.NonceSize).ToArray();
        var nonceD = Enumerable.Repeat((byte)0x44, VaultFileFormat.NonceSize).ToArray();
        var nonceE = Enumerable.Repeat((byte)0x55, VaultFileFormat.NonceSize).ToArray();
        var random = new SequenceVaultNonceSource(
            nonceA,
            nonceA,
            nonceB,
            nonceB,
            nonceC,
            nonceA,
            nonceD,
            nonceC,
            nonceD,
            nonceE);
        var storage = new InMemoryVaultStorage();
        using var master = Utf8("master-52e6");
        await using var vault = await EncryptedCredentialVault.CreateAsync(
            storage,
            master,
            TimeProvider.System,
            TimeSpan.FromMinutes(5),
            random,
            CancellationToken.None);
        using var secret = Utf8("nonce-value-5e6f");

        await vault.SaveAsync(Reference, secret, CancellationToken.None);
        var firstDocument = VaultFileFormat.Parse(storage.Bytes!);
        await vault.SaveAsync(Reference, secret, CancellationToken.None);
        var secondDocument = VaultFileFormat.Parse(storage.Bytes!);

        Assert.Equal(nonceC, firstDocument.ManifestNonce);
        Assert.Equal(nonceB, firstDocument.Entries.Single().Value.Nonce);
        Assert.Equal(nonceE, secondDocument.ManifestNonce);
        Assert.Equal(nonceD, secondDocument.Entries.Single().Value.Nonce);
    }

    [Fact]
    public async Task Exhausted_nonce_collisions_are_rejected()
    {
        var storage = new InMemoryVaultStorage();
        using var master = Utf8("master-52e6");

        await Assert.ThrowsAsync<CryptographicException>(
            () => EncryptedCredentialVault.CreateAsync(
                storage,
                master,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                new ConstantVaultNonceSource(new byte[VaultFileFormat.NonceSize]),
                CancellationToken.None).AsTask());
        Assert.Null(storage.Bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Key_derivation_gate_releases_when_secret_access_throws(
        bool throwFromLength)
    {
        var failing = new ThrowingSecret(throwFromLength);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => EncryptedCredentialVault.CreateAsync(
                new InMemoryVaultStorage(),
                failing,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                CancellationToken.None).AsTask());

        using var valid = Utf8("master-52e6");
        await using var vault = await EncryptedCredentialVault.CreateAsync(
            new InMemoryVaultStorage(),
            valid,
            TimeProvider.System,
            TimeSpan.FromMinutes(5),
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Serialized_size_limit_includes_manifest_nonce_and_tag()
    {
        var entries = Enumerable.Range(0, 15).ToDictionary(
            static index => $"credential://vault/{index}",
            static index => new VaultEntry(
                $"credential://vault/{index}",
                Enumerable.Repeat((byte)(index + 1), VaultFileFormat.NonceSize).ToArray(),
                new byte[VaultFileFormat.MaximumSecretBytes],
                new byte[VaultFileFormat.TagSize]),
            StringComparer.Ordinal);
        var finalReference = "credential://vault/final";
        entries.Add(
            finalReference,
            new VaultEntry(
                finalReference,
                Enumerable.Repeat((byte)0x40, VaultFileFormat.NonceSize).ToArray(),
                [],
                new byte[VaultFileFormat.TagSize]));
        var document = new VaultDocument(
            VaultKdfParameters.Default,
            new byte[VaultFileFormat.SaltSize],
            new byte[VaultFileFormat.TagSize],
            Revision: 0,
            entries,
            Enumerable.Repeat((byte)0x41, VaultFileFormat.NonceSize).ToArray(),
            new byte[VaultFileFormat.TagSize]);
        var emptyFinalManifestLength = VaultFileFormat.BuildManifest(document).Length;
        var finalCiphertextLength =
            VaultFileFormat.MaximumFileBytes -
            VaultFileFormat.NonceSize -
            VaultFileFormat.TagSize +
            1 -
            emptyFinalManifestLength;
        entries[finalReference] = entries[finalReference] with
        {
            Ciphertext = new byte[finalCiphertextLength],
        };

        Assert.InRange(finalCiphertextLength, 1, VaultFileFormat.MaximumSecretBytes);
        Assert.Throws<InvalidDataException>(() => VaultFileFormat.Serialize(document));
    }

    [Fact]
    public async Task Failed_atomic_write_keeps_previous_vault_readable()
    {
        var storage = await CreateStoredVaultAsync();
        var original = storage.Bytes!.ToArray();
        storage.WriteException = new IOException("simulated atomic failure");
        using var master = Utf8("master-52e6");
        await using var vault = await EncryptedCredentialVault.OpenAsync(
            storage,
            master,
            TimeProvider.System,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        using var replacement = Utf8("replacement-441f");

        await Assert.ThrowsAsync<IOException>(
            () => vault.SaveAsync(Reference, replacement, CancellationToken.None).AsTask());

        Assert.Equal(original, storage.Bytes);
    }

    [Fact]
    public async Task Lock_and_dispose_are_shared_idempotent_operations()
    {
        var storage = await CreateStoredVaultAsync();
        using var master = Utf8("master-52e6");
        var vault = await EncryptedCredentialVault.OpenAsync(
            storage,
            master,
            TimeProvider.System,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        await Task.WhenAll(vault.LockAsync().AsTask(), vault.LockAsync().AsTask());
        await Assert.ThrowsAsync<VaultLockedException>(
            () => vault.ReadAsync(Reference, CancellationToken.None).AsTask());
        await Task.WhenAll(vault.DisposeAsync().AsTask(), vault.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Auto_lock_uses_the_supplied_time_provider()
    {
        var storage = await CreateStoredVaultAsync();
        var time = new ManualTimeProvider();
        using var master = Utf8("master-52e6");
        await using var vault = await EncryptedCredentialVault.OpenAsync(
            storage,
            master,
            time,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<VaultLockedException>(
            () => vault.ReadAsync(Reference, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Expired_timer_waiting_behind_activity_does_not_lock_the_fresh_session()
    {
        var storage = await CreateStoredVaultAsync();
        var time = new ManualTimeProvider();
        using var master = Utf8("master-52e6");
        await using var vault = await EncryptedCredentialVault.OpenAsync(
            storage,
            master,
            time,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        storage.WriteStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        storage.ContinueWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var replacement = Utf8("fresh-activity-2a19");
        var save = vault.SaveAsync(Reference, replacement, CancellationToken.None).AsTask();
        await storage.WriteStarted.Task;

        time.Advance(TimeSpan.FromMinutes(1));
        storage.ContinueWrite.SetResult();
        await save;

        using var read = await vault.ReadAsync(Reference, CancellationToken.None);
        Assert.NotNull(read);
        time.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<VaultLockedException>(
            () => vault.ReadAsync(Reference, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Rejects_KDF_DOS_values_truncation_and_duplicate_nonces()
    {
        var storage = await CreateStoredVaultAsync();
        BinaryPrimitives.WriteInt32LittleEndian(storage.Bytes!.AsSpan(12), int.MaxValue);
        using var master = Utf8("master-52e6");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => EncryptedCredentialVault.OpenAsync(
                storage,
                master,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                CancellationToken.None).AsTask());

        storage = await CreateStoredVaultAsync();
        storage.Bytes = storage.Bytes![..^5];
        using var truncatedMaster = Utf8("master-52e6");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => EncryptedCredentialVault.OpenAsync(
                storage,
                truncatedMaster,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                CancellationToken.None).AsTask());

        var nonce = Enumerable.Repeat((byte)0x42, VaultFileFormat.NonceSize).ToArray();
        var entries = new Dictionary<string, VaultEntry>(StringComparer.Ordinal)
        {
            ["credential://vault/one"] = new(
                "credential://vault/one",
                nonce.ToArray(),
                [1],
                new byte[VaultFileFormat.TagSize]),
            ["credential://vault/two"] = new(
                "credential://vault/two",
                nonce.ToArray(),
                [2],
                new byte[VaultFileFormat.TagSize]),
        };
        storage = new InMemoryVaultStorage
        {
            Bytes = VaultFileFormat.Serialize(
                new VaultDocument(
                    VaultKdfParameters.Default,
                    new byte[VaultFileFormat.SaltSize],
                    new byte[VaultFileFormat.TagSize],
                    Revision: 0,
                    entries,
                    new byte[VaultFileFormat.NonceSize],
                    new byte[VaultFileFormat.TagSize])),
        };
        using var duplicateMaster = Utf8("master-52e6");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => EncryptedCredentialVault.OpenAsync(
                storage,
                duplicateMaster,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                CancellationToken.None).AsTask());
    }

    private static async Task<InMemoryVaultStorage> CreateStoredVaultAsync()
    {
        var storage = new InMemoryVaultStorage();
        using var master = Utf8("master-52e6");
        await using var vault = await EncryptedCredentialVault.CreateAsync(
            storage,
            master,
            TimeProvider.System,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        using var secret = Utf8("credential-a1c3");
        await vault.SaveAsync(Reference, secret, CancellationToken.None);
        return storage;
    }

    private static async Task<InMemoryVaultStorage> CreateStoredVaultWithTwoEntriesAsync()
    {
        var storage = await CreateStoredVaultAsync();
        using var master = Utf8("master-52e6");
        await using var vault = await EncryptedCredentialVault.OpenAsync(
            storage,
            master,
            TimeProvider.System,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        using var secret = Utf8("credential-b7e2");
        await vault.SaveAsync(
            CredentialReference.Create("vault", "device/bravo"),
            secret,
            CancellationToken.None);
        return storage;
    }

    private static async Task AssertTamperedAsync(InMemoryVaultStorage storage)
    {
        using var master = Utf8("master-52e6");
        await Assert.ThrowsAnyAsync<Exception>(
            () => EncryptedCredentialVault.OpenAsync(
                storage,
                master,
                TimeProvider.System,
                TimeSpan.FromMinutes(5),
                CancellationToken.None).AsTask());
    }

    private static List<(int Start, int Length)> EntryRanges(byte[] file)
    {
        var countOffset = 64;
        var count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(countOffset));
        var offset = countOffset + sizeof(int);
        var result = new List<(int Start, int Length)>();
        for (var index = 0; index < count; index++)
        {
            var start = offset;
            var referenceLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset));
            offset += sizeof(int) + referenceLength + VaultFileFormat.NonceSize;
            var ciphertextLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset));
            offset += sizeof(int) + ciphertextLength + VaultFileFormat.TagSize;
            result.Add((start, offset - start));
        }

        return result;
    }

    private static SecretBuffer Utf8(string value) =>
        SecretBuffer.CopyFrom(Encoding.UTF8.GetBytes(value));

    private static string ReadUtf8(WinARD.Application.Ports.ISecret secret)
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

    private sealed class InMemoryVaultStorage : IVaultStorage
    {
        private byte[]? _bytes;
        private int _version;

        public byte[]? Bytes
        {
            get => _bytes;
            set
            {
                _bytes = value;
                _version++;
            }
        }

        public Exception? WriteException { get; set; }

        public TaskCompletionSource? WriteStarted { get; set; }

        public TaskCompletionSource? ContinueWrite { get; set; }

        public ValueTask<VaultStorageSnapshot?> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _bytes is null
                    ? null
                    : new VaultStorageSnapshot(
                        _bytes.ToArray(),
                        _version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        public async ValueTask<VaultStorageWriteResult> CompareExchangeAsync(
            ReadOnlyMemory<byte> contents,
            string? expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteException is not null)
            {
                throw WriteException;
            }

            WriteStarted?.TrySetResult();
            if (ContinueWrite is not null)
            {
                await ContinueWrite.Task.WaitAsync(cancellationToken);
            }

            var currentVersion = _bytes is null
                ? null
                : _version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(currentVersion, expectedVersion, StringComparison.Ordinal))
            {
                return new VaultStorageWriteResult(false, currentVersion);
            }

            _bytes = contents.ToArray();
            _version++;
            return new VaultStorageWriteResult(
                true,
                _version.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
            var timer = new ManualTimer(this, callback, state, _now + dueTime, period);
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

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAt,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset _dueAt = dueAt;
            private TimeSpan _period = period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _dueAt = owner._now + dueTime;
                _period = period;
                return !_disposed;
            }

            public void FireIfDue(DateTimeOffset now)
            {
                if (_disposed || now < _dueAt)
                {
                    return;
                }

                callback(state);
                if (_period == Timeout.InfiniteTimeSpan)
                {
                    Dispose();
                }
                else
                {
                    _dueAt = now + _period;
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

    private sealed class SequenceVaultNonceSource(params byte[][] values)
        : IVaultNonceSource
    {
        private readonly Queue<byte[]> _values = new(values);

        public void Fill(Span<byte> destination)
        {
            var value = _values.Dequeue();
            value.CopyTo(destination);
        }
    }

    private sealed class ConstantVaultNonceSource(byte[] value) : IVaultNonceSource
    {
        public void Fill(Span<byte> destination) => value.CopyTo(destination);
    }

    private sealed class ThrowingSecret(bool throwFromLength)
        : WinARD.Application.Ports.ISecret
    {
        public int Length => throwFromLength
            ? throw new InvalidOperationException("length failed")
            : 8;

        public void CopyTo(Span<byte> destination) =>
            throw new InvalidOperationException("copy failed");

        public WinARD.Application.Ports.ISecret Clone() =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

}
