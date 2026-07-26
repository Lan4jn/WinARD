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

    private sealed class FakeWindowsCredentialApi : IWindowsCredentialApi
    {
        private byte[]? _value;

        public int WriteCount { get; private set; }

        public string? LastTarget { get; private set; }

        public void Write(string target, byte[] secret)
        {
            WriteCount++;
            LastTarget = target;
            _value = secret.ToArray();
        }

        public byte[]? Read(string target)
        {
            LastTarget = target;
            return _value?.ToArray();
        }

        public void Delete(string target)
        {
            LastTarget = target;
            _value = null;
        }
    }
}
