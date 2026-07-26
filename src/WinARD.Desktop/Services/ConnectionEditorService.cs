using System.Diagnostics;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Domain.Security;

namespace WinARD.Desktop.Services;

public sealed class ConnectionEditorService(
    IDeviceRepository repository,
    ICredentialStore credentialStore,
    TransientCredentialStore transientStore,
    ConnectDeviceHandler connectHandler)
{
    private readonly IDeviceRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ICredentialStore _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    private readonly TransientCredentialStore _transientStore = transientStore ?? throw new ArgumentNullException(nameof(transientStore));
    private readonly ConnectDeviceHandler _connectHandler = connectHandler ?? throw new ArgumentNullException(nameof(connectHandler));

    public async Task<ConnectionProfile> SaveAsync(
        ConnectionProfile profile,
        CredentialSaveMode mode,
        ISecret? secret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var oldProfile = await _repository.GetAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        var reference = ReferenceFor(profile.Id, mode);
        var savedProfile = profile.WithCredential(reference);
        var package = secret as ConnectionEditorSecretPackage;
        using var macSecret = package?.HasMacSecret == true ? package.CloneMacSecret() : secret?.Clone();
        using var sshSecret = package?.HasSshSecret == true ? package.CloneSshSecret() : null;
        savedProfile = WithSshCredentialReference(savedProfile, mode, sshSecret is not null);

        if (mode == CredentialSaveMode.AskEveryTime)
        {
            await _repository.SaveAsync(savedProfile, cancellationToken).ConfigureAwait(false);
            await DeleteUnsharedOldReferencesAsync(oldProfile, savedProfile, cancellationToken)
                .ConfigureAwait(false);
            return savedProfile;
        }

        if (macSecret is null)
        {
            if (oldProfile?.CredentialReference == reference)
            {
                await _repository.SaveAsync(savedProfile, cancellationToken).ConfigureAwait(false);
                return savedProfile;
            }

            throw new InvalidOperationException("请选择密码后再保存到所选凭据存储。");
        }

        CredentialReference? sshReference = null;
        if (sshSecret is not null && savedProfile.SshProfile is { } savedSsh)
        {
            sshReference = savedSsh.PrivateKeyPath is null
                ? savedSsh.PasswordCredentialReference
                : savedSsh.PrivateKeyPassphraseCredentialReference;
        }

        using var previous = await _credentialStore.ReadSnapshotAsync(reference, cancellationToken).ConfigureAwait(false);
        using var previousSsh = sshReference is null
            ? null
            : await _credentialStore.ReadSnapshotAsync(sshReference, cancellationToken).ConfigureAwait(false);
        try
        {
            await _credentialStore.SaveAsync(reference, macSecret, cancellationToken).ConfigureAwait(false);
            if (sshReference is not null && sshSecret is not null)
            {
                await _credentialStore.SaveAsync(sshReference, sshSecret, cancellationToken).ConfigureAwait(false);
            }

            await _repository.SaveAsync(savedProfile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception saveException)
        {
            var rollbackErrors = new List<Exception>();
            await RestoreCredentialAsync(reference, previous, rollbackErrors).ConfigureAwait(false);
            if (sshReference is not null)
            {
                await RestoreCredentialAsync(sshReference, previousSsh, rollbackErrors).ConfigureAwait(false);
            }

            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, saveException);
                throw new AggregateException("保存连接失败，且凭据回滚未能完整完成。", rollbackErrors);
            }

            throw;
        }

        await DeleteUnsharedOldReferencesAsync(oldProfile, savedProfile, cancellationToken)
            .ConfigureAwait(false);
        return savedProfile;
    }

    public async Task<IReadOnlyList<ConnectionTestStageResult>> TestAsync(
        ConnectionProfile profile,
        CredentialSaveMode mode,
        ISecret? secret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var package = secret as ConnectionEditorSecretPackage;
        using var macSecret = package?.HasMacSecret == true ? package.CloneMacSecret() : secret?.Clone();
        using var sshSecret = package?.HasSshSecret == true ? package.CloneSshSecret() : null;
        if (macSecret is null)
        {
            throw new InvalidOperationException("测试连接需要临时密码。");
        }

        var reference = CredentialReference.Create("transient", Guid.NewGuid().ToString("N"));
        await _transientStore.SaveAsync(reference, macSecret, cancellationToken).ConfigureAwait(false);
        var testProfile = profile.WithCredential(reference);
        CredentialReference? sshReference = null;
        if (sshSecret is not null && testProfile.SshProfile is { } testSsh)
        {
            sshReference = CredentialReference.Create("transient", Guid.NewGuid().ToString("N"));
            await _transientStore.SaveAsync(sshReference, sshSecret, cancellationToken).ConfigureAwait(false);
            testProfile = testProfile.WithSsh(testSsh.PrivateKeyPath is null
                ? testSsh.WithAuthenticationCredentials(sshReference, null)
                : testSsh.WithAuthenticationCredentials(null, sshReference));
        }
        var timer = Stopwatch.StartNew();
        var completed = new List<ConnectionTestStageResult>();
        ConnectionStage? activeStage = null;
        void StageChanged(ConnectionStage stage)
        {
            if (activeStage is { } previous)
            {
                completed.Add(new ConnectionTestStageResult(
                    previous,
                    true,
                    timer.Elapsed,
                    "阶段完成。"));
            }

            activeStage = stage;
            timer.Restart();
        }
        try
        {
            var result = await _connectHandler.HandleAsync(testProfile, StageChanged, cancellationToken)
                .ConfigureAwait(false);
            if (result.Session is not null)
            {
                if (activeStage is { } connected)
                {
                    completed.Add(new ConnectionTestStageResult(
                        connected,
                        true,
                        timer.Elapsed,
                        "连接成功。"));
                }

                await result.Session.DisposeAsync().ConfigureAwait(false);
                return completed;
            }

            var error = result.Error ?? throw new InvalidOperationException("连接失败但未返回错误信息。");
            completed.Add(new ConnectionTestStageResult(error.Stage, false, timer.Elapsed, error.UserMessage));
            return completed;
        }
        finally
        {
            await _transientStore.DeleteAsync(reference, CancellationToken.None).ConfigureAwait(false);
            if (sshReference is not null)
            {
                await _transientStore.DeleteAsync(sshReference, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task DeleteUnsharedOldReferencesAsync(
        ConnectionProfile? oldProfile,
        ConnectionProfile newProfile,
        CancellationToken cancellationToken)
    {
        if (oldProfile is null)
        {
            return;
        }

        var profiles = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var retained = profiles.SelectMany(CredentialReferences).ToHashSet();
        foreach (var oldReference in CredentialReferences(oldProfile)
            .Except(CredentialReferences(newProfile))
            .Where(candidate => !string.Equals(candidate.Store, "ask", StringComparison.OrdinalIgnoreCase))
            .Except(retained))
        {
            await _credentialStore.DeleteAsync(oldReference, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RestoreCredentialAsync(
        CredentialReference reference,
        CredentialStoreSnapshot? previous,
        List<Exception> errors)
    {
        try
        {
            using var current = await _credentialStore.ReadSnapshotAsync(reference, CancellationToken.None)
                .ConfigureAwait(false);
            var result = await _credentialStore.CompareExchangeAsync(
                reference,
                current?.Version,
                previous?.Secret,
                CancellationToken.None).ConfigureAwait(false);
            if (result != CredentialStoreCompareExchangeResult.Succeeded)
            {
                errors.Add(new InvalidOperationException("凭据存储并发更新阻止了回滚。"));
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static IEnumerable<CredentialReference> CredentialReferences(ConnectionProfile profile)
    {
        if (profile.CredentialReference is not null)
        {
            yield return profile.CredentialReference;
        }

        if (profile.SshProfile?.PasswordCredentialReference is not null)
        {
            yield return profile.SshProfile.PasswordCredentialReference;
        }

        if (profile.SshProfile?.PrivateKeyPassphraseCredentialReference is not null)
        {
            yield return profile.SshProfile.PrivateKeyPassphraseCredentialReference;
        }
    }

    private static CredentialReference ReferenceFor(Guid id, CredentialSaveMode mode) => mode switch
    {
        CredentialSaveMode.WindowsCredentialManager => CredentialReference.Create("windows", $"profile/{id:D}/mac"),
        CredentialSaveMode.EncryptedVault => CredentialReference.Create("vault", $"profile/{id:D}/mac"),
        CredentialSaveMode.AskEveryTime => CredentialReference.Create("ask", $"profile/{id:D}/mac"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static ConnectionProfile WithSshCredentialReference(
        ConnectionProfile profile,
        CredentialSaveMode mode,
        bool replaceSecret)
    {
        if (profile.SshProfile is not { } ssh || (!replaceSecret && mode != CredentialSaveMode.AskEveryTime))
        {
            return profile;
        }

        var store = mode switch
        {
            CredentialSaveMode.WindowsCredentialManager => "windows",
            CredentialSaveMode.EncryptedVault => "vault",
            CredentialSaveMode.AskEveryTime => "ask",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var reference = CredentialReference.Create(
            store,
            $"profile/{profile.Id:D}/{(ssh.PrivateKeyPath is null ? "ssh-password" : "ssh-passphrase")}");
        return profile.WithSsh(ssh.PrivateKeyPath is null
            ? ssh.WithAuthenticationCredentials(reference, null)
            : ssh.WithAuthenticationCredentials(null, reference));
    }

}
