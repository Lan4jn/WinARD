using System.Security.Cryptography;
using WinARD.Desktop.Services;
using WinARD.Desktop.ViewModels;
using WinARD.Security.Secrets;
using WinARD.Security.Vault;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class VaultCredentialStoreSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"winard-vault-session-{Guid.NewGuid():N}");

    [Fact]
    public async Task FirstUnlockCreatesVaultWithoutPersistingMasterPassword()
    {
        Directory.CreateDirectory(_directory);
        await using var sut = CreateSession();
        using var master = Secret("correct horse battery staple");

        await sut.UnlockAsync(master, CancellationToken.None);

        Assert.True(sut.IsUnlocked);
        Assert.True(File.Exists(Path.Combine(_directory, "credentials.vault")));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task WrongPasswordLeavesExistingVaultLocked()
    {
        Directory.CreateDirectory(_directory);
        await using (var creator = CreateSession())
        {
            using var master = Secret("correct horse battery staple");
            await creator.UnlockAsync(master, CancellationToken.None);
        }

        await using var sut = CreateSession();
        using var wrong = Secret("wrong password");

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => sut.UnlockAsync(wrong, CancellationToken.None).AsTask());
        Assert.False(sut.IsUnlocked);
    }

    [Fact]
    public async Task CancelledPromptKeepsVaultLockedAndDisposesTemporarySecret()
    {
        await using var sut = CreateSession();
        using var prompt = new CredentialPromptViewModel();
        var secret = new TrackingSecret();
        prompt.Supply(secret);

        prompt.Dispose();

        Assert.False(sut.IsUnlocked);
        Assert.True(secret.WasDisposed);
    }

    [Fact]
    public async Task LockedVaultCanBeUnlockedAgain()
    {
        await using var sut = CreateSession();
        using var first = Secret("correct horse battery staple");
        await sut.UnlockAsync(first, CancellationToken.None);
        await sut.LockAsync();
        using var second = Secret("correct horse battery staple");

        await sut.UnlockAsync(second, CancellationToken.None);

        Assert.True(sut.IsUnlocked);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private VaultCredentialStoreSession CreateSession() => new(
        new FileVaultStorage(Path.Combine(_directory, "credentials.vault")),
        TimeProvider.System,
        TimeSpan.FromMinutes(5));

    private static SecretBuffer Secret(string value) =>
        SecretBuffer.CopyFrom(System.Text.Encoding.UTF8.GetBytes(value));

    private sealed class TrackingSecret : WinARD.Application.Ports.ISecret
    {
        private readonly bool _isClone;

        public TrackingSecret(bool isClone = false) => _isClone = isClone;

        public int Length => 1;

        public bool WasDisposed { get; private set; }

        public void CopyTo(Span<byte> destination) => destination[0] = 1;

        public WinARD.Application.Ports.ISecret Clone() => new TrackingSecret(isClone: true)
        {
            _owner = this,
        };

        private TrackingSecret? _owner;

        public void Dispose()
        {
            if (_isClone && _owner is not null)
            {
                _owner.WasDisposed = true;
            }
            else
            {
                WasDisposed = true;
            }
        }
    }
}
