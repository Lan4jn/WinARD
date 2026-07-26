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
            .. original.AsSpan(original.Length - VaultFileFormat.TagSize),
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
            .. original.AsSpan(original.Length - VaultFileFormat.TagSize),
        ];
        await AssertTamperedAsync(storage);

        storage.Bytes =
        [
            .. original.AsSpan(0, ranges[0].Start),
            .. original.AsSpan(ranges[0].Start, ranges[0].Length),
            .. original.AsSpan(ranges[0].Start, ranges[0].Length),
            .. original.AsSpan(original.Length - VaultFileFormat.TagSize),
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

}
