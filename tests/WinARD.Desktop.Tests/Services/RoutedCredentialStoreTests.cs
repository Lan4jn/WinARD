using System.Security.Cryptography;
using System.Text;
using WinARD.Application.Ports;
using WinARD.Desktop.Services;
using WinARD.Domain.Security;
using WinARD.Security.Secrets;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class RoutedCredentialStoreTests
{
    [Fact]
    public async Task AskReferenceUsesReferencePromptWithoutPersistingSecret()
    {
        using var windows = new TransientCredentialStore();
        using var vault = new TransientCredentialStore();
        using var transient = new TransientCredentialStore();
        var prompt = new CredentialPromptService();
        CredentialPromptRequest? prompted = null;
        prompt.SetReferenceHandler((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            prompted = request;
            return ValueTask.FromResult<ISecret>(
                SecretBuffer.CopyFrom(Encoding.UTF8.GetBytes("prompted-ssh-secret")));
        });
        await using var sut = new RoutedCredentialStore(windows, vault, transient, prompt);
        var reference = CredentialReference.Create("ask", "profile/id/ssh-password");

        var request = new CredentialPromptRequest(reference, CredentialPromptPurpose.MacPassword);
        using var secret = await sut.ReadForPromptAsync(request, CancellationToken.None);

        Assert.Equal(request, prompted);
        Assert.Equal("prompted-ssh-secret", Read(secret!));
        Assert.Null(await windows.ReadAsync(reference, CancellationToken.None));
        Assert.Null(await vault.ReadAsync(reference, CancellationToken.None));
    }

    [Theory]
    [InlineData(CredentialPromptPurpose.SshPassword)]
    [InlineData(CredentialPromptPurpose.PrivateKeyPassphrase)]
    public async Task OpaqueAskReferenceKeepsExplicitSshPurpose(CredentialPromptPurpose purpose)
    {
        using var windows = new TransientCredentialStore();
        using var vault = new TransientCredentialStore();
        using var transient = new TransientCredentialStore();
        var prompt = new CredentialPromptService();
        CredentialPromptRequest? prompted = null;
        prompt.SetReferenceHandler((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            prompted = request;
            return ValueTask.FromResult<ISecret>(SecretBuffer.CopyFrom([1]));
        });
        await using var sut = new RoutedCredentialStore(windows, vault, transient, prompt);
        var request = new CredentialPromptRequest(
            CredentialReference.Create("ask", "opaque-reference-without-purpose"),
            purpose);

        using var secret = await sut.ReadForPromptAsync(request, CancellationToken.None);

        Assert.Equal(request, prompted);
    }

    [Fact]
    public async Task VersionedCompareExchangeDelegatesAtomicOperationToSelectedStore()
    {
        var windows = new AtomicOnlyStore();
        using var vault = new TransientCredentialStore();
        using var transient = new TransientCredentialStore();
        await using var sut = new RoutedCredentialStore(
            windows, vault, transient, new CredentialPromptService());
        var reference = CredentialReference.Create("windows", "profile/id/mac");
        using var secret = SecretBuffer.CopyFrom([1, 2, 3]);

        using var write = await sut.CompareExchangeWithVersionAsync(
            reference, null, secret, CancellationToken.None);

        Assert.True(windows.AtomicMethodCalled);
        Assert.Equal(CredentialStoreCompareExchangeResult.Succeeded, write.Result);
        Assert.NotNull(write.WrittenVersion);
    }

    private static string Read(ISecret secret)
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

    private sealed class AtomicOnlyStore : ICredentialStore
    {
        public bool AtomicMethodCalled { get; private set; }

        public ValueTask SaveAsync(CredentialReference reference, ISecret secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ISecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(CredentialReference reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The routed operation must not re-read the written value.");

        public ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
            CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The routed operation must preserve the version-returning call.");

        public ValueTask<CredentialStoreWriteResult> CompareExchangeWithVersionAsync(
            CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken)
        {
            AtomicMethodCalled = true;
            return ValueTask.FromResult(new CredentialStoreWriteResult(
                CredentialStoreCompareExchangeResult.Succeeded,
                CredentialStoreVersion.CopyFrom([9])));
        }

        public ValueTask DeleteAsync(CredentialReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
