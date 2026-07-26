using System.ComponentModel;
using WinARD.Application.Ports;
using WinARD.Domain.Security;
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
        var native = new FakeWindowsCredentialApi();
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
        var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);
        var reference = CredentialReference.Create("windows", "missing");

        Assert.Null(await store.ReadAsync(reference, CancellationToken.None));
        await store.DeleteAsync(reference, CancellationToken.None);
    }

    [Fact]
    public async Task Rejects_credential_blobs_over_the_Windows_limit_before_native_call()
    {
        var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);
        using var oversized = SecretBuffer.CopyFrom(new byte[2561]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.SaveAsync(
                CredentialReference.Create("windows", "large"),
                oversized,
                CancellationToken.None).AsTask());

        Assert.Equal(0, native.WriteCount);
    }

    [Fact]
    public async Task Snapshot_compare_exchange_replaces_and_deletes_atomically()
    {
        var native = new FakeWindowsCredentialApi();
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
    public async Task Stale_snapshot_conflicts_across_store_instances_and_preserves_winner()
    {
        var native = new FakeWindowsCredentialApi();
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
        var native = new FakeWindowsCredentialApi { OperationDelay = TimeSpan.FromMilliseconds(20) };
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

    private sealed class FakeWindowsCredentialApi : IWindowsCredentialApi
    {
        private byte[]? _value;
        private int _activeOperations;
        private int _maximumConcurrentOperations;

        public int WriteCount { get; private set; }

        public string? LastTarget { get; private set; }

        public TimeSpan OperationDelay { get; init; }

        public int MaximumConcurrentOperations => _maximumConcurrentOperations;

        public void Write(string target, byte[] secret)
        {
            EnterOperation();
            try
            {
                WriteCount++;
                LastTarget = target;
                _value = secret.ToArray();
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
                return _value?.ToArray();
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
}
