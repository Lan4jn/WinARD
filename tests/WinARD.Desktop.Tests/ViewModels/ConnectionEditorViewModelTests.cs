using WinARD.Application.Ports;
using WinARD.Desktop.ViewModels;
using WinARD.Desktop.Services;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Domain.Security;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class ConnectionEditorViewModelTests
{
    [Fact]
    public void Draft_prefills_endpoint_without_becoming_an_existing_profile()
    {
        var sut = ConnectionEditorViewModel.FromDraft(
            new ConnectionEditorDraft("Nearby Mac", "nearby.local", 5901, string.Empty),
            (saved, _, _, _) => Task.FromResult(new ConnectionProfileSaveResult(saved)),
            (candidate, _, _, _) => Task.FromResult(new ConnectionProfileTestResult(candidate, [])));

        Assert.Equal("Nearby Mac", sut.DisplayName);
        Assert.Equal("nearby.local", sut.Host);
        Assert.Equal(5901, sut.Port);
        Assert.Equal(string.Empty, sut.MacUsername);
        Assert.False(sut.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Ssh_authentication_mode_migrates_from_private_key_presence()
    {
        var password = ExistingSshProfile(privateKeyPath: null);
        var key = ExistingSshProfile("C:\\Keys\\id_ed25519");

        Assert.Equal(SshAuthenticationMode.Password, CreateFor(password).SshAuthenticationMode);
        Assert.Equal(SshAuthenticationMode.PrivateKey, CreateFor(key).SshAuthenticationMode);
    }

    [Fact]
    public void EditingPrivateKeyPathNeverChangesExplicitPasswordMode()
    {
        var sut = CreateValidViewModel();
        sut.SshAuthenticationMode = SshAuthenticationMode.Password;

        sut.PrivateKeyPath = "C:\\Keys\\stale_key";
        sut.PrivateKeyPath = string.Empty;

        Assert.Equal(SshAuthenticationMode.Password, sut.SshAuthenticationMode);
    }

    [Fact]
    public void EditingPrivateKeyPathNeverChangesExplicitPrivateKeyMode()
    {
        var sut = CreateValidViewModel();
        sut.SshAuthenticationMode = SshAuthenticationMode.PrivateKey;

        sut.PrivateKeyPath = "C:\\Keys\\id_ed25519";
        sut.PrivateKeyPath = string.Empty;

        Assert.Equal(SshAuthenticationMode.PrivateKey, sut.SshAuthenticationMode);
    }

    [Fact]
    public void UserAuthenticationModeChangeClearsCurrentSecretState()
    {
        var sut = CreateValidViewModel();
        sut.HasSshAuthenticationSecret = true;

        sut.SshAuthenticationMode = SshAuthenticationMode.PrivateKey;

        Assert.False(sut.HasSshAuthenticationSecret);
    }

    [Fact]
    public void AssigningSameAuthenticationModeDoesNotClearLoadedSecretState()
    {
        var sut = CreateValidViewModel();
        sut.HasSshAuthenticationSecret = true;

        sut.SshAuthenticationMode = SshAuthenticationMode.Password;

        Assert.True(sut.HasSshAuthenticationSecret);
    }

    [Fact]
    public void AuthenticationFieldChangesIncrementGeneration()
    {
        var sut = CreateValidViewModel();
        var initial = sut.SshAuthenticationGeneration;

        sut.SshUsername = "ssh-user";
        var afterUsername = sut.SshAuthenticationGeneration;
        sut.PrivateKeyPath = "C:\\Keys\\id_ed25519";

        Assert.True(afterUsername > initial);
        Assert.True(sut.SshAuthenticationGeneration > afterUsername);
    }

    [Fact]
    public async Task StaleHostKeyFailureAfterModeChangeIsDiscarded()
    {
        var release = new TaskCompletionSource<ConnectionProfileTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = ExistingSshProfile(privateKeyPath: null)
            .WithCredential(CredentialReference.Create("windows", "mac"));
        var sut = new ConnectionEditorViewModel(
            profile,
            (saved, _, _, _) => Task.FromResult(new ConnectionProfileSaveResult(saved)),
            (_, _, _, _) => release.Task);
        sut.CredentialSaveMode = CredentialSaveMode.AskEveryTime;
        var test = sut.TestConnectionAsync(null, CancellationToken.None);
        sut.SshAuthenticationMode = SshAuthenticationMode.PrivateKey;
        var failure = new SshHostKeyPromptRequest(
            new SshHostKeyEndpoint("jump.local", 22),
            "ssh-ed25519",
            "SHA256:new",
            PreviousFingerprint: null,
            IsChanged: false);

        release.SetResult(new ConnectionProfileTestResult(profile, [], HostKeyFailure: failure));
        await test;

        Assert.Null(sut.LastTestHostKeyFailure);
        Assert.Contains("配置已变", sut.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleHostKeyFailureAfterAuthenticationFieldChangeIsDiscarded()
    {
        var release = new TaskCompletionSource<ConnectionProfileTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = ExistingSshProfile(privateKeyPath: null)
            .WithCredential(CredentialReference.Create("windows", "mac"));
        var sut = new ConnectionEditorViewModel(
            profile,
            (saved, _, _, _) => Task.FromResult(new ConnectionProfileSaveResult(saved)),
            (_, _, _, _) => release.Task);
        sut.CredentialSaveMode = CredentialSaveMode.AskEveryTime;
        var test = sut.TestConnectionAsync(null, CancellationToken.None);
        sut.SshUsername = "changed-user";
        var failure = new SshHostKeyPromptRequest(
            new SshHostKeyEndpoint("jump.local", 22),
            "ssh-ed25519",
            "SHA256:new",
            PreviousFingerprint: null,
            IsChanged: false);

        release.SetResult(new ConnectionProfileTestResult(profile, [], HostKeyFailure: failure));
        await test;

        Assert.Null(sut.LastTestHostKeyFailure);
        Assert.Contains("配置已变", sut.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateKeyModePreservesOnlyPassphraseReference()
    {
        var password = CredentialReference.Create("windows", "ssh/password");
        var passphrase = CredentialReference.Create("windows", "ssh/passphrase");
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "operator")
            .WithSsh(SshProfile.Create("jump.local", 22, "ssh-user", "C:\\Keys\\id_ed25519",
                    "target.local", 5900, null, null, null)
                .WithAuthenticationCredentials(password, passphrase));
        var sut = CreateFor(profile);

        var ssh = sut.BuildProfile().SshProfile!;

        Assert.Null(ssh.PasswordCredentialReference);
        Assert.Equal(passphrase, ssh.PrivateKeyPassphraseCredentialReference);
    }

    [Fact]
    public void Ssh_jump_and_target_endpoints_round_trip_independently()
    {
        var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "library.local", 5900, "operator")
            .WithSsh(SshProfile.Create("jump.local", 2222, "ssh-user", null, "target.internal", 5907, null, null, null)
                .WithAuthenticationCredentials(CredentialReference.Create("windows", "ssh"), null));
        var sut = CreateFor(original);

        Assert.Equal("jump.local", sut.SshHost);
        Assert.Equal(2222, sut.SshPort);
        Assert.Equal("target.internal", sut.SshTargetHost);
        Assert.Equal(5907, sut.SshTargetPort);
        var built = sut.BuildProfile().SshProfile!;
        Assert.Equal("target.internal", built.TargetHost);
        Assert.Equal(5907, built.TargetPort);
    }

    [Fact]
    public void Building_new_ssh_profile_never_copies_top_level_endpoint_to_target()
    {
        var sut = CreateValidViewModel();
        sut.UseSsh = true;
        sut.SshHost = "jump.local";
        sut.SshPort = 22;
        sut.SshUsername = "ssh-user";
        sut.SshTargetHost = "target.internal";
        sut.SshTargetPort = 5908;
        sut.SshAuthenticationMode = SshAuthenticationMode.Password;
        sut.HasSshAuthenticationSecret = true;

        var ssh = sut.BuildProfile().SshProfile!;

        Assert.Equal("target.internal", ssh.TargetHost);
        Assert.Equal(5908, ssh.TargetPort);
        Assert.NotEqual(sut.Host, ssh.TargetHost);
    }

    private static ConnectionProfile ExistingSshProfile(string? privateKeyPath) =>
        ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "operator")
            .WithSsh(SshProfile.Create("jump.local", 22, "ssh-user", privateKeyPath,
                "target.internal", 5900, null, null, null));

    private static ConnectionEditorViewModel CreateFor(ConnectionProfile profile) => new(
        profile,
        (saved, _, _, _) => Task.FromResult(saved),
        (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));
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

        sut.SshAuthenticationMode = SshAuthenticationMode.PrivateKey;
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
    public async Task DialogInitializationLeavesOpaqueCredentialModeUntouched()
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

        sut.DisplayName = "Renamed";
        await sut.SaveAsync(null, CancellationToken.None);

        Assert.False(sut.CredentialModeChanged);
        Assert.Equal(macReference, saved!.CredentialReference);
        Assert.Equal(sshReference, saved.SshProfile!.PasswordCredentialReference);
        Assert.Equal(pin, saved.SshProfile.HostKeyPin);
    }

    [Fact]
    public void DialogInitializationSameSupportedModeDoesNotMarkModeChanged()
    {
        var profile = ConnectionProfile.Create(
                Guid.NewGuid(), "Original", "mac.local", 5900, "mac-user")
            .WithCredential(CredentialReference.Create("windows", "mac"))
            .WithSsh(SshProfile.Create(
                "jump.local", 22, "ssh-user", null, "mac.local", 5900,
                CredentialReference.Create("windows", "ssh"), null, null));
        var sut = new ConnectionEditorViewModel(
            profile,
            (saved, _, _, _) => Task.FromResult(saved),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

        sut.CredentialSaveMode = CredentialSaveMode.WindowsCredentialManager;

        Assert.False(sut.CredentialModeChanged);
        Assert.False(sut.HasUnsupportedCredentialReference);
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
    public async Task UnknownCredentialStoreAllowsNonSecretEditButDisablesTest()
    {
        var macReference = CredentialReference.Create("legacy-plugin", "shared/mac");
        var sshReference = CredentialReference.Create("opaque-ssh", "shared/ssh");
        var pin = new SshHostKeyPin(
            new SshHostKeyEndpoint("jump.local", 22),
            "ssh-ed25519",
            "AAAAC3NzaC1lZDI1NTE5AAAAIFixture",
            "SHA256:fixture");
        var profile = ConnectionProfile.Create(
                Guid.NewGuid(), "Legacy", "legacy.local", 5900, "operator")
            .WithCredential(macReference)
            .WithSsh(SshProfile.Create(
                    "jump.local", 22, "ssh-user", null, "legacy.local", 5900,
                    credentialReference: null, null, null)
                .WithAuthenticationCredentials(sshReference, null)
                .WithHostKeyPin(pin));
        ConnectionProfile? saved = null;
        var sut = new ConnectionEditorViewModel(
            profile,
            (candidate, _, _, _) =>
            {
                saved = candidate;
                return Task.FromResult(candidate);
            },
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

        sut.DisplayName = "Legacy Renamed";

        Assert.True(sut.SaveCommand.CanExecute(null));
        Assert.False(sut.TestConnectionCommand.CanExecute(null));
        Assert.True(sut.HasUnsupportedCredentialReference);
        Assert.Contains("不受支持的后端", sut.StatusMessage, StringComparison.Ordinal);

        await sut.SaveAsync(secret: null, CancellationToken.None);

        Assert.Equal(macReference, saved!.CredentialReference);
        Assert.Equal(sshReference, saved.SshProfile!.PasswordCredentialReference);
        Assert.Equal(pin, saved.SshProfile.HostKeyPin);
    }

    [Fact]
    public void ExplicitCredentialModeChangeStopsPreservingOpaqueReferences()
    {
        var profile = ConnectionProfile.Create(
                Guid.NewGuid(), "Legacy", "legacy.local", 5900, "operator")
            .WithCredential(CredentialReference.Create("legacy-plugin", "shared/mac"))
            .WithSsh(SshProfile.Create(
                    "jump.local", 22, "ssh-user", null, "legacy.local", 5900,
                    credentialReference: null, null, null)
                .WithAuthenticationCredentials(
                    CredentialReference.Create("opaque-ssh", "shared/ssh"),
                    null));
        var sut = new ConnectionEditorViewModel(
            profile,
            (saved, _, _, _) => Task.FromResult(saved),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

        sut.CredentialSaveMode = CredentialSaveMode.AskEveryTime;

        Assert.True(sut.SaveCommand.CanExecute(null));
        Assert.False(sut.HasUnsupportedCredentialReference);
        Assert.Null(sut.BuildProfile().CredentialReference);
        Assert.Null(sut.BuildProfile().SshProfile!.PasswordCredentialReference);
    }

    [Fact]
    public void ExplicitWindowsSelectionMigratesUnknownFallbackMode()
    {
        var profile = ConnectionProfile.Create(
                Guid.NewGuid(), "Legacy", "legacy.local", 5900, "operator")
            .WithCredential(CredentialReference.Create("legacy-plugin", "shared/mac"));
        var sut = new ConnectionEditorViewModel(
            profile,
            (saved, _, _, _) => Task.FromResult(saved),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

        Assert.Equal(CredentialSaveMode.WindowsCredentialManager, sut.CredentialSaveMode);
        sut.CredentialSaveMode = CredentialSaveMode.WindowsCredentialManager;

        Assert.True(sut.CredentialModeChanged);
        Assert.False(sut.HasUnsupportedCredentialReference);
        Assert.Null(sut.BuildProfile().CredentialReference);
    }

    [Fact]
    public async Task UnknownCredentialStoreRejectsSecretUntilModeChanges()
    {
        var profile = ConnectionProfile.Create(
                Guid.NewGuid(), "Legacy", "legacy.local", 5900, "operator")
            .WithCredential(CredentialReference.Create("legacy-plugin", "shared/mac"));
        var saveCalls = 0;
        var sut = new ConnectionEditorViewModel(
            profile,
            (saved, _, _, _) =>
            {
                saveCalls++;
                return Task.FromResult(saved);
            },
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));
        using var secret = new TestSecret();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SaveAsync(secret.Clone(), CancellationToken.None));

        Assert.Contains("先选择受支持的凭据保存方式", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, saveCalls);
    }

    [Fact]
    public void OpaqueSshCredentialDisablesTestWhileKeepingNonSecretSaveEnabled()
    {
        var sshReference = CredentialReference.Create("opaque-ssh", "shared/ssh");
        var profile = ConnectionProfile.Create(
                Guid.NewGuid(), "Legacy SSH", "legacy.local", 5900, "operator")
            .WithCredential(CredentialReference.Create("windows", "shared/mac"))
            .WithSsh(SshProfile.Create(
                    "jump.local", 22, "ssh-user", null, "legacy.local", 5900,
                    credentialReference: null, null, null)
                .WithAuthenticationCredentials(sshReference, null));
        var sut = new ConnectionEditorViewModel(
            profile,
            (saved, _, _, _) => Task.FromResult(saved),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

        Assert.True(sut.HasUnsupportedCredentialReference);
        Assert.True(sut.SaveCommand.CanExecute(null));
        Assert.False(sut.TestConnectionCommand.CanExecute(null));
        Assert.Equal(sshReference, sut.BuildProfile().SshProfile!.PasswordCredentialReference);
    }

    private sealed class TestSecret : ISecret
    {
        public int Length => 1;

        public void CopyTo(Span<byte> destination) => destination[0] = 1;

        public ISecret Clone() => new TestSecret();

        public void Dispose()
        {
        }
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
        sut.SshAuthenticationMode = SshAuthenticationMode.Password;

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
