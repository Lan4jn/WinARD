using System.ComponentModel;
using System.Security.Cryptography;
using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Security.Migration;
using WinARD.Security.Secrets;
using WinARD.Security.WindowsCredentials;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Security.Tests;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public async Task Canonical_target_escapes_reference_components_without_collisions()
    {
        using var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);
        using var secret = SecretBuffer.CopyFrom([4, 5, 6]);

        await store.SaveAsync(
            CredentialReference.Create("windows", "folder/item%2Fone"),
            secret,
            CancellationToken.None);

        Assert.Equal(
            "WinARD/windows/folder%2Fitem%252Fone",
            native.LastTarget);
    }

    [Fact]
    public async Task Missing_credential_maps_to_null_and_delete_is_idempotent()
    {
        using var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);
        var reference = CredentialReference.Create("windows", "missing");

        Assert.Null(await store.ReadAsync(reference, CancellationToken.None));
        await store.DeleteAsync(reference, CancellationToken.None);
    }

    [Fact]
    public async Task Rejects_credential_blobs_over_the_Windows_limit_before_native_call()
    {
        using var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);
        using var oversized = SecretBuffer.CopyFrom(
            new byte[WindowsCredentialStore.MaximumSecretBytes + 1]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.SaveAsync(
                CredentialReference.Create("windows", "large"),
                oversized,
                CancellationToken.None).AsTask());

        Assert.Equal(0, native.WriteCount);
    }

    [Fact]
    public async Task Maximum_secret_size_accounts_for_the_versioned_blob_header()
    {
        using var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);
        using var maximum = SecretBuffer.CopyFrom(
            new byte[WindowsCredentialStore.MaximumSecretBytes]);

        await store.SaveAsync(
            CredentialReference.Create("windows", "maximum"),
            maximum,
            CancellationToken.None);

        Assert.Equal(WindowsCredentialStore.MaximumBlobBytes, native.RawLength);
    }

    [Fact]
    public async Task Consecutive_same_content_saves_receive_different_versions()
    {
        using var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);
        var reference = CredentialReference.Create("windows", "aba-version");
        using var secret = SecretBuffer.CopyFrom([1, 2, 3]);

        await store.SaveAsync(reference, secret, CancellationToken.None);
        using var first = await store.ReadSnapshotAsync(reference, CancellationToken.None);
        await store.SaveAsync(reference, secret, CancellationToken.None);
        using var second = await store.ReadSnapshotAsync(reference, CancellationToken.None);

        Assert.False(first!.Version.FixedTimeEquals(second!.Version));
    }

    [Fact]
    public async Task Malformed_or_legacy_native_blob_is_rejected()
    {
        using var native = new FakeWindowsCredentialApi();
        native.SetRawValue([1, 2, 3]);
        var store = new WindowsCredentialStore(native);
        var reference = CredentialReference.Create("windows", "malformed");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ReadAsync(reference, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Migration_recovery_does_not_overwrite_same_content_rewrite_with_new_revision()
    {
        using var native = new FakeWindowsCredentialApi();
        var target = new WindowsCredentialStore(native);
        var reference = CredentialReference.Create("windows", "migration-aba");
        using (var oldValue = SecretBuffer.CopyFrom([1, 2, 3]))
        {
            await target.SaveAsync(reference, oldValue, CancellationToken.None);
        }

        using var source = new MemorySourceCredentialStore([4, 5, 6]);
        native.ThrowAfterNextWriteAndRewriteRevisionAfterNextRead();

        var exception = await Assert.ThrowsAsync<MigrationTargetWriteUncertainException>(
            () => new CredentialStoreMigrator().MoveAsync(
                source,
                target,
                reference,
                CancellationToken.None).AsTask());

        Assert.Equal("MIGRATION_TARGET_CONCURRENT_CHANGE", exception.SafeCode);
        Assert.False(source.Deleted);
        Assert.True(native.ConcurrentWinnerRetained);
        using var winner = await target.ReadAsync(reference, CancellationToken.None);
        AssertSecret([4, 5, 6], winner!);
    }

    [Fact]
    public async Task Disposed_queued_secret_does_not_leave_target_gate_locked()
    {
        using var native = new FakeWindowsCredentialApi();
        native.BlockNextWrite();
        var store = new WindowsCredentialStore(native);
        var reference = CredentialReference.Create("windows", "gate-release");
        using var blocker = SecretBuffer.CopyFrom([1]);
        var blockedWrite = Task.Run(
            () => store.SaveAsync(reference, blocker, CancellationToken.None).AsTask());
        await native.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var queued = new DisposableTestSecret([2]);
        var queuedWrite = store.SaveAsync(reference, queued, CancellationToken.None).AsTask();
        queued.Dispose();
        native.ReleaseBlockedWrite();
        await blockedWrite;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queuedWrite);

        using var final = SecretBuffer.CopyFrom([3]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await store.SaveAsync(reference, final, timeout.Token);
    }

    [Fact]
    public async Task Snapshot_compare_exchange_replaces_and_deletes_atomically()
    {
        using var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);
        var reference = CredentialReference.Create("windows", "cas-success");
        using var original = SecretBuffer.CopyFrom([1, 2, 3]);
        using var replacement = SecretBuffer.CopyFrom([7, 8, 9]);
        await store.SaveAsync(reference, original, CancellationToken.None);

        using var snapshot = await store.ReadSnapshotAsync(reference, CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(
            CredentialStoreCompareExchangeResult.Succeeded,
            await store.CompareExchangeAsync(
                reference,
                snapshot!.Version,
                replacement,
                CancellationToken.None));

        using var replacementSnapshot = await store.ReadSnapshotAsync(
            reference,
            CancellationToken.None);
        Assert.NotNull(replacementSnapshot);
        Assert.Equal(
            CredentialStoreCompareExchangeResult.Succeeded,
            await store.CompareExchangeAsync(
                reference,
                replacementSnapshot!.Version,
                replacement: null,
                CancellationToken.None));
        Assert.Null(await store.ReadAsync(reference, CancellationToken.None));
    }

    [Fact]
    public async Task Versioned_compare_exchange_returns_exact_written_revision()
    {
        using var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);
        var reference = CredentialReference.Create("windows", "cas-written-version");
        using var replacement = SecretBuffer.CopyFrom([7, 8, 9]);

        using var write = await store.CompareExchangeWithVersionAsync(
            reference,
            expectedVersion: null,
            replacement,
            CancellationToken.None);

        Assert.Equal(CredentialStoreCompareExchangeResult.Succeeded, write.Result);
        Assert.NotNull(write.WrittenVersion);
        using var snapshot = await store.ReadSnapshotAsync(reference, CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.True(write.WrittenVersion!.FixedTimeEquals(snapshot!.Version));
    }

    [Fact]
    public async Task Stale_snapshot_conflicts_across_store_instances_and_preserves_winner()
    {
        using var native = new FakeWindowsCredentialApi();
        var first = new WindowsCredentialStore(native);
        var second = new WindowsCredentialStore(native);
        var reference = CredentialReference.Create("windows", "cas-conflict");
        using var original = SecretBuffer.CopyFrom([1, 2, 3]);
        using var winner = SecretBuffer.CopyFrom([4, 5, 6]);
        using var loser = SecretBuffer.CopyFrom([7, 8, 9]);
        await first.SaveAsync(reference, original, CancellationToken.None);
        using var stale = await second.ReadSnapshotAsync(reference, CancellationToken.None);

        await first.SaveAsync(reference, winner, CancellationToken.None);
        Assert.Equal(
            CredentialStoreCompareExchangeResult.Conflict,
            await second.CompareExchangeAsync(
                reference,
                stale!.Version,
                loser,
                CancellationToken.None));

        using var actual = await first.ReadAsync(reference, CancellationToken.None);
        AssertSecret([4, 5, 6], actual!);
    }

    [Fact]
    public async Task Normal_writes_share_the_process_gate_across_store_instances()
    {
        using var native = new FakeWindowsCredentialApi
        {
            OperationDelay = TimeSpan.FromMilliseconds(20),
        };
        var first = new WindowsCredentialStore(native);
        var second = new WindowsCredentialStore(native);
        var reference = CredentialReference.Create("windows", "shared-gate");
        using var firstValue = SecretBuffer.CopyFrom([1]);
        using var secondValue = SecretBuffer.CopyFrom([2]);

        await Task.WhenAll(
            Task.Run(() => first.SaveAsync(reference, firstValue, CancellationToken.None).AsTask()),
            Task.Run(() => second.SaveAsync(reference, secondValue, CancellationToken.None).AsTask()));

        Assert.Equal(1, native.MaximumConcurrentOperations);
    }

    [Fact]
    public void Credential_store_version_rejects_access_after_disposal()
    {
        var version = CredentialStoreVersion.CopyFrom([1, 2, 3]);
        version.Dispose();

        Assert.Throws<ObjectDisposedException>(() => version.FixedTimeEquals([1, 2, 3]));
    }

    private static void AssertSecret(byte[] expected, ISecret actual)
    {
        var bytes = new byte[actual.Length];
        actual.CopyTo(bytes);
        Assert.Equal(expected, bytes);
    }

    private sealed class FakeWindowsCredentialApi : IWindowsCredentialApi, IDisposable
    {
        private byte[]? _value;
        private int _activeOperations;
        private int _maximumConcurrentOperations;
        private int _blockNextWrite;
        private bool _throwAfterNextWrite;
        private bool _rewriteRevisionAfterNextRead;
        private readonly ManualResetEventSlim _continueWrite = new(initialState: false);

        public int WriteCount { get; private set; }

        public string? LastTarget { get; private set; }

        public TimeSpan OperationDelay { get; init; }

        public int MaximumConcurrentOperations => _maximumConcurrentOperations;

        public int? RawLength => _value?.Length;

        private byte[]? ConcurrentWinner { get; set; }

        public bool ConcurrentWinnerRetained =>
            _value is not null &&
            ConcurrentWinner is not null &&
            _value.AsSpan().SequenceEqual(ConcurrentWinner);

        public TaskCompletionSource WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Write(string target, byte[] secret)
        {
            EnterOperation();
            try
            {
                WriteCount++;
                LastTarget = target;
                _value = secret.ToArray();
                if (Interlocked.Exchange(ref _blockNextWrite, 0) == 1)
                {
                    WriteStarted.TrySetResult();
                    _continueWrite.Wait(TimeSpan.FromSeconds(5));
                }

                if (_throwAfterNextWrite)
                {
                    _throwAfterNextWrite = false;
                    _rewriteRevisionAfterNextRead = true;
                    throw new IOException("native write uncertain");
                }
            }
            finally
            {
                ExitOperation();
            }
        }

        public byte[]? Read(string target)
        {
            EnterOperation();
            try
            {
                LastTarget = target;
                var result = _value?.ToArray();
                if (_rewriteRevisionAfterNextRead && _value is not null)
                {
                    _rewriteRevisionAfterNextRead = false;
                    _value[WindowsCredentialStore.RevisionOffset] ^= 0x01;
                    ConcurrentWinner = _value.ToArray();
                }

                return result;
            }
            finally
            {
                ExitOperation();
            }
        }

        public void Delete(string target)
        {
            EnterOperation();
            try
            {
                LastTarget = target;
                _value = null;
            }
            finally
            {
                ExitOperation();
            }
        }

        private void EnterOperation()
        {
            var active = Interlocked.Increment(ref _activeOperations);
            InterlockedExtensions.Max(ref _maximumConcurrentOperations, active);
            if (OperationDelay > TimeSpan.Zero)
            {
                Thread.Sleep(OperationDelay);
            }
        }

        private void ExitOperation() => Interlocked.Decrement(ref _activeOperations);

        public void SetRawValue(byte[] value) => _value = value.ToArray();

        public void ThrowAfterNextWriteAndRewriteRevisionAfterNextRead() =>
            _throwAfterNextWrite = true;

        public void BlockNextWrite() => Interlocked.Exchange(ref _blockNextWrite, 1);

        public void ReleaseBlockedWrite() => _continueWrite.Set();

        public void Dispose()
        {
            if (_value is not null)
            {
                CryptographicOperations.ZeroMemory(_value);
                _value = null;
            }

            if (ConcurrentWinner is not null)
            {
                CryptographicOperations.ZeroMemory(ConcurrentWinner);
                ConcurrentWinner = null;
            }

            _continueWrite.Dispose();
        }

        private static class InterlockedExtensions
        {
            public static void Max(ref int location, int value)
            {
                var current = Volatile.Read(ref location);
                while (current < value)
                {
                    var observed = Interlocked.CompareExchange(ref location, value, current);
                    if (observed == current)
                    {
                        return;
                    }

                    current = observed;
                }
            }
        }
    }

    private sealed class DisposableTestSecret(byte[] value) : ISecret
    {
        private byte[]? _value = value.ToArray();

        public int Length => _value?.Length ??
            throw new ObjectDisposedException(nameof(DisposableTestSecret));

        public void CopyTo(Span<byte> destination) =>
            (_value ?? throw new ObjectDisposedException(nameof(DisposableTestSecret)))
                .CopyTo(destination);

        public ISecret Clone() => new DisposableTestSecret(
            _value ?? throw new ObjectDisposedException(nameof(DisposableTestSecret)));

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _value, null);
            if (value is not null)
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
    }

    private sealed class MemorySourceCredentialStore(byte[] value) : ICredentialStore, IDisposable
    {
        private byte[]? _value = value.ToArray();

        public bool Deleted { get; private set; }

        public ValueTask SaveAsync(
            CredentialReference reference,
            ISecret secret,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ISecret?> ReadAsync(
            CredentialReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISecret?>(
                _value is null ? null : SecretBuffer.CopyFrom(_value));
        }

        public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(
            CredentialReference reference,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
            CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(
            CredentialReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Interlocked.Exchange(ref _value, null);
            if (current is not null)
            {
                CryptographicOperations.ZeroMemory(current);
            }

            Deleted = true;
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _value, null);
            if (current is not null)
            {
                CryptographicOperations.ZeroMemory(current);
            }
        }
    }
}
