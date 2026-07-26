using WinARD.Application.Ports;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Domain.Security;
using Xunit;

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class ConnectionEditorViewModelTests
{
    [Fact]
    public void SaveCommandTracksRequiredFieldsAndPortRange()
    {
        var sut = CreateViewModel();

        Assert.False(sut.SaveCommand.CanExecute(null));

        sut.DisplayName = "Studio Mac";
        sut.Host = "studio.local";
        sut.Port = 5900;
        sut.MacUsername = "operator";

        Assert.True(sut.SaveCommand.CanExecute(null));

        sut.Port = 65536;

        Assert.False(sut.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void SshModeRequiresCompleteEndpointAndAuthenticationMaterial()
    {
        var sut = CreateValidViewModel();
        sut.UseSsh = true;
        sut.SshHost = "jump.local";
        sut.SshPort = 22;
        sut.SshUsername = "tunnel";

        Assert.False(sut.SaveCommand.CanExecute(null));

        sut.PrivateKeyPath = "C:\\Keys\\id_ed25519";

        Assert.True(sut.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task EditingRoundtripsPinsAndCredentialReferences()
    {
        var macReference = CredentialReference.Create("windows", "mac");
        var sshReference = CredentialReference.Create("vault", "ssh");
        var pin = new SshHostKeyPin(
            new SshHostKeyEndpoint("jump.local", 22),
            "ssh-ed25519",
            "AAAAC3NzaC1lZDI1NTE5AAAAIFixture",
            "SHA256:fixture");
        var original = ConnectionProfile.Create(
                Guid.NewGuid(), "Original", "mac.local", 5900, "mac-user")
            .WithCredential(macReference)
            .WithSsh(SshProfile.Create(
                    "jump.local", 22, "ssh-user", null, "mac.local", 5900,
                    sshReference, null, null)
                .WithHostKeyPin(pin));
        ConnectionProfile? saved = null;
        var sut = new ConnectionEditorViewModel(
            original,
            (profile, _, _, _) =>
            {
                saved = profile;
                return Task.FromResult(profile);
            },
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

        sut.DisplayName = "Renamed";
        await sut.SaveAsync(null, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal("Renamed", saved.DisplayName);
        Assert.Equal(macReference, saved.CredentialReference);
        Assert.Equal(sshReference, saved.SshProfile!.PasswordCredentialReference);
        Assert.Equal(pin, saved.SshProfile.HostKeyPin);
    }

    [Fact]
    public async Task DialogInitializationSameModeDoesNotDiscardOpaqueSshReferenceOrPin()
    {
        var macReference = CredentialReference.Create("windows", "mac");
        var sshReference = CredentialReference.Create("opaque-store", "ssh/password");
        var pin = new SshHostKeyPin(
            new SshHostKeyEndpoint("jump.local", 22),
            "ssh-ed25519",
            "AAAAC3NzaC1lZDI1NTE5AAAAIFixture",
            "SHA256:fixture");
        var original = ConnectionProfile.Create(
                Guid.NewGuid(), "Original", "mac.local", 5900, "mac-user")
            .WithCredential(macReference)
            .WithSsh(SshProfile.Create(
                    "jump.local", 22, "ssh-user", null, "mac.local", 5900,
                    credentialReference: null, null, null)
                .WithAuthenticationCredentials(sshReference, null)
                .WithHostKeyPin(pin));
        ConnectionProfile? saved = null;
        var sut = new ConnectionEditorViewModel(
            original,
            (profile, _, _, _) =>
            {
                saved = profile;
                return Task.FromResult(profile);
            },
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

        sut.CredentialSaveMode = CredentialSaveMode.WindowsCredentialManager;
        sut.DisplayName = "Renamed";
        await sut.SaveAsync(null, CancellationToken.None);

        Assert.False(sut.CredentialModeChanged);
        Assert.Equal(macReference, saved!.CredentialReference);
        Assert.Equal(sshReference, saved.SshProfile!.PasswordCredentialReference);
        Assert.Equal(pin, saved.SshProfile.HostKeyPin);
    }

    [Theory]
    [InlineData(CredentialSaveMode.WindowsCredentialManager)]
    [InlineData(CredentialSaveMode.EncryptedVault)]
    [InlineData(CredentialSaveMode.AskEveryTime)]
    public async Task SelectedCredentialModeIsForwardedWithoutFallback(CredentialSaveMode mode)
    {
        CredentialSaveMode? observed = null;
        var sut = new ConnectionEditorViewModel(
            null,
            (profile, selected, _, _) =>
            {
                observed = selected;
                return Task.FromResult(profile);
            },
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));
        PopulateRequiredFields(sut);
        sut.CredentialSaveMode = mode;

        await sut.SaveAsync(null, CancellationToken.None);

        Assert.Equal(mode, observed);
    }

    [Fact]
    public async Task TestConnectionNeverInvokesSaveAndReportsStages()
    {
        var saveCalls = 0;
        var expected = new[]
        {
            new ConnectionTestStageResult(ConnectionStage.Connected, true, TimeSpan.FromMilliseconds(2), "连接成功。"),
        };
        var sut = new ConnectionEditorViewModel(
            null,
            (profile, mode, secret, cancellationToken) =>
            {
                saveCalls++;
                return Task.FromResult(profile);
            },
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>(expected));
        PopulateRequiredFields(sut);

        await sut.TestConnectionAsync(null, CancellationToken.None);

        Assert.Equal(0, saveCalls);
        Assert.Equal(expected, sut.TestResults);
        Assert.Equal("连接成功。", sut.StatusMessage);
    }

    [Fact]
    public async Task TrustedTestConnectionPinIsIncludedInSubsequentSave()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var pin = new SshHostKeyPin(
            endpoint,
            "ssh-ed25519",
            "AAAAC3NzaC1lZDI1NTE5AAAAIFixture",
            "SHA256:fixture");
        var original = ConnectionProfile.Create(
                Guid.NewGuid(), "Studio", "studio.local", 5900, "operator")
            .WithCredential(CredentialReference.Create("windows", "mac"))
            .WithSsh(SshProfile.Create(
                endpoint.Host, endpoint.Port, "ssh-user", null,
                "studio.local", 5900,
                CredentialReference.Create("windows", "ssh"), null, null));
        ConnectionProfile? saved = null;
        var stages = new[]
        {
            new ConnectionTestStageResult(
                ConnectionStage.Connected,
                true,
                TimeSpan.FromMilliseconds(1),
                "连接成功。"),
        };
        var sut = new ConnectionEditorViewModel(
            original,
            (profile, _, _, _) =>
            {
                saved = profile;
                return Task.FromResult(new ConnectionProfileSaveResult(profile));
            },
            (profile, _, _, _) => Task.FromResult(new ConnectionProfileTestResult(
                profile.WithSsh(profile.SshProfile!.WithHostKeyPin(pin)),
                stages)));

        await sut.TestConnectionAsync(null, CancellationToken.None);
        await sut.SaveAsync(null, CancellationToken.None);

        Assert.Equal(pin, sut.BuildProfile().SshProfile!.HostKeyPin);
        Assert.Equal(pin, saved!.SshProfile!.HostKeyPin);
    }

    [Fact]
    public async Task BusyGateRejectsReentrantSave()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var sut = new ConnectionEditorViewModel(
            null,
            async (profile, _, _, _) =>
            {
                calls++;
                await release.Task;
                return profile;
            },
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));
        PopulateRequiredFields(sut);

        var first = sut.SaveAsync(null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SaveAsync(null, CancellationToken.None));
        release.SetResult();
        await first;

        Assert.Equal(1, calls);
    }

    [Fact]
    public void UnknownCredentialStoreBlocksSaveUntilUserChoosesMode()
    {
        var profile = ConnectionProfile.Create(
                Guid.NewGuid(), "Legacy", "legacy.local", 5900, "operator")
            .WithCredential(CredentialReference.Create("custom-store", "shared/key"));
        var sut = new ConnectionEditorViewModel(
            profile,
            (saved, _, _, _) => Task.FromResult(saved),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

        Assert.False(sut.SaveCommand.CanExecute(null));
        Assert.True(sut.HasUnsupportedCredentialReference);

        sut.CredentialSaveMode = CredentialSaveMode.AskEveryTime;

        Assert.True(sut.SaveCommand.CanExecute(null));
        Assert.False(sut.HasUnsupportedCredentialReference);
    }

    [Fact]
    public void SwitchingFromPrivateKeyToPasswordDoesNotReusePassphraseReference()
    {
        var passphrase = CredentialReference.Create("vault", "ssh/passphrase");
        var profile = ConnectionProfile.Create(
                Guid.NewGuid(), "Key Mac", "key.local", 5900, "operator")
            .WithSsh(SshProfile.Create(
                    "jump.local", 22, "ssh-user", "C:\\Keys\\id_ed25519",
                    "key.local", 5900, null, null, null)
                .WithAuthenticationCredentials(null, passphrase));
        var sut = new ConnectionEditorViewModel(
            profile,
            (saved, _, _, _) => Task.FromResult(saved),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

        sut.PrivateKeyPath = string.Empty;

        Assert.False(sut.SaveCommand.CanExecute(null));
        sut.HasSshAuthenticationSecret = true;
        Assert.Null(sut.BuildProfile().SshProfile!.PasswordCredentialReference);
    }

    private static ConnectionEditorViewModel CreateViewModel() => new(
        null,
        (profile, _, _, _) => Task.FromResult(profile),
        (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

    private static ConnectionEditorViewModel CreateValidViewModel()
    {
        var sut = CreateViewModel();
        PopulateRequiredFields(sut);
        return sut;
    }

    private static void PopulateRequiredFields(ConnectionEditorViewModel sut)
    {
        sut.DisplayName = "Studio Mac";
        sut.Host = "studio.local";
        sut.Port = 5900;
        sut.MacUsername = "operator";
    }
}
