using System.Diagnostics;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Domain.Security;
using WinARD.Infrastructure.Settings;

namespace WinARD.Desktop.Services;

public sealed class ConnectionEditorService(
    IDeviceRepository repository,
    ICredentialStore credentialStore,
    ITransientCredentialStore transientStore,
    ConnectionAttemptWorkflow attemptWorkflow,
    CredentialMutationGate credentialMutationGate,
    ICredentialReferenceRetirementService retirementService)
{
    private readonly IDeviceRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ICredentialStore _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    private readonly ITransientCredentialStore _transientStore = transientStore ?? throw new ArgumentNullException(nameof(transientStore));
    private readonly ConnectionAttemptWorkflow _attemptWorkflow = attemptWorkflow ?? throw new ArgumentNullException(nameof(attemptWorkflow));
    private readonly CredentialMutationGate _credentialMutationGate = credentialMutationGate ?? throw new ArgumentNullException(nameof(credentialMutationGate));
    private readonly ICredentialReferenceRetirementService _retirementService = retirementService ?? throw new ArgumentNullException(nameof(retirementService));

    public async Task<ConnectionProfile> SaveAsync(
        ConnectionProfile profile,
        CredentialSaveMode mode,
        ISecret? secret,
        CancellationToken cancellationToken) =>
        (await SaveWithResultAsync(profile, mode, secret, cancellationToken).ConfigureAwait(false)).Profile;

    public async Task<ConnectionProfileSaveResult> SaveWithResultAsync(
        ConnectionProfile profile,
        CredentialSaveMode mode,
        ISecret? secret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var mutationLease = await _credentialMutationGate.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        var oldProfile = await _repository.GetAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        if (CredentialReferences(profile).Any(static reference => !IsSupportedStore(reference.Store)))
        {
            if (secret is not null)
            {
                throw new InvalidOperationException(
                    "凭据由不受支持的后端管理。请先选择受支持的凭据保存方式再修改密码。");
            }

            await _repository.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
            return await CompleteCommittedSaveAsync(oldProfile, profile).ConfigureAwait(false);
        }

        var package = secret as ConnectionEditorSecretPackage;
        using var macSecret = package is not null
            ? package.HasMacSecret ? package.CloneMacSecret() : null
            : secret?.Clone();
        using var sshSecret = package?.HasSshSecret == true ? package.CloneSshSecret() : null;
        var existingReference = profile.CredentialReference ?? oldProfile?.CredentialReference;
        var reference = mode == CredentialSaveMode.AskEveryTime
            ? ReferenceFor(profile.Id, mode)
            : macSecret is null && existingReference is not null
                ? existingReference
                : macSecret is null
                    ? null
                    : GenerationReference(profile.Id, mode, "mac");
        if (reference is null)
        {
            throw new InvalidOperationException("请选择密码后再保存到所选凭据存储。");
        }

        var savedProfile = profile.WithCredential(reference);
        savedProfile = WithSshCredentialReference(
            savedProfile,
            oldProfile,
            mode,
            sshSecret is not null);

        if (mode == CredentialSaveMode.AskEveryTime)
        {
            await _repository.SaveAsync(savedProfile, cancellationToken).ConfigureAwait(false);
            return new ConnectionProfileSaveResult(savedProfile);
        }

        CredentialReference? sshReference = null;
        if (sshSecret is not null && savedProfile.SshProfile is { } savedSsh)
        {
            sshReference = savedSsh.PrivateKeyPath is null
                ? savedSsh.PasswordCredentialReference
                : savedSsh.PrivateKeyPassphraseCredentialReference;
        }

        using var previous = macSecret is null
            ? null
            : await _credentialStore.ReadSnapshotAsync(reference, cancellationToken).ConfigureAwait(false);
        using var previousSsh = sshReference is null
            ? null
            : await _credentialStore.ReadSnapshotAsync(sshReference, cancellationToken).ConfigureAwait(false);
        CredentialStoreWriteResult? macWrite = null;
        CredentialStoreWriteResult? sshWrite = null;
        try
        {
            if (macSecret is not null)
            {
                macWrite = await _credentialStore.CompareExchangeWithVersionAsync(
                    reference,
                    previous?.Version,
                    macSecret,
                    cancellationToken).ConfigureAwait(false);
                EnsureWritten(macWrite);
            }

            if (sshReference is not null && sshSecret is not null)
            {
                sshWrite = await _credentialStore.CompareExchangeWithVersionAsync(
                    sshReference,
                    previousSsh?.Version,
                    sshSecret,
                    cancellationToken).ConfigureAwait(false);
                EnsureWritten(sshWrite);
            }

            await _repository.SaveAsync(savedProfile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception saveException)
        {
            var rollbackErrors = new List<Exception>();
            if (sshReference is not null && sshWrite?.WrittenVersion is not null)
            {
                await RestoreCredentialAsync(
                    sshReference,
                    sshWrite.WrittenVersion,
                    previousSsh,
                    rollbackErrors).ConfigureAwait(false);
            }

            if (macWrite?.WrittenVersion is not null)
            {
                await RestoreCredentialAsync(
                    reference,
                    macWrite.WrittenVersion,
                    previous,
                    rollbackErrors).ConfigureAwait(false);
            }

            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, saveException);
                throw new AggregateException("保存连接失败，且凭据回滚未能完整完成。", rollbackErrors);
            }

            throw;
        }
        finally
        {
            macWrite?.Dispose();
            sshWrite?.Dispose();
        }

        return await CompleteCommittedSaveAsync(oldProfile, savedProfile).ConfigureAwait(false);
    }

    public async Task<ConnectionProfileTestResult> TestAsync(
        ConnectionProfile profile,
        CredentialSaveMode mode,
        ISecret? secret,
        CancellationToken cancellationToken) =>
        await TestAsync(
            profile,
            mode,
            secret,
            hostKeyPrompt: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<ConnectionProfileTestResult> TestAsync(
        ConnectionProfile profile,
        CredentialSaveMode mode,
        ISecret? secret,
        ISshHostKeyPrompt? hostKeyPrompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var package = secret as ConnectionEditorSecretPackage;
        using var macSecret = package is not null
            ? package.HasMacSecret ? package.CloneMacSecret() : null
            : secret?.Clone();
        using var sshSecret = package?.HasSshSecret == true ? package.CloneSshSecret() : null;
        if (macSecret is null)
        {
            throw new InvalidOperationException("测试连接需要临时密码。");
        }

        var writtenReferences = new List<CredentialReference>();
        var timer = Stopwatch.StartNew();
        var completed = new List<ConnectionTestStageResult>();
        ConnectionStage? activeStage = null;
        void StageChanged(ConnectionStage stage)
        {
            if (stage == ConnectionStage.Resolving && activeStage is not null)
            {
                activeStage = null;
                timer.Restart();
            }

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
            var reference = CredentialReference.Create("transient", Guid.NewGuid().ToString("N"));
            await _transientStore.SaveAsync(reference, macSecret, cancellationToken).ConfigureAwait(false);
            writtenReferences.Add(reference);
            var testProfile = profile.WithCredential(reference);
            if (sshSecret is not null && testProfile.SshProfile is { } testSsh)
            {
                var sshReference = CredentialReference.Create("transient", Guid.NewGuid().ToString("N"));
                await _transientStore.SaveAsync(sshReference, sshSecret, cancellationToken).ConfigureAwait(false);
                writtenReferences.Add(sshReference);
                testProfile = testProfile.WithSsh(testSsh.PrivateKeyPath is null
                    ? testSsh.WithAuthenticationCredentials(sshReference, null)
                    : testSsh.WithAuthenticationCredentials(null, sshReference));
            }

            var outcome = await _attemptWorkflow.AttemptAsync(
                testProfile,
                StageChanged,
                acceptedHostKey: null,
                hostKeyPrompt,
                cancellationToken)
                .ConfigureAwait(false);
            var result = outcome.Result;
            var updatedDraft = profile;
            if (profile.SshProfile is { } draftSsh &&
                outcome.Profile.SshProfile?.HostKeyPin is { } acceptedPin)
            {
                updatedDraft = profile.WithSsh(draftSsh.WithHostKeyPin(acceptedPin));
            }

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
                return new ConnectionProfileTestResult(updatedDraft, completed);
            }

            var error = result.Error ?? throw new InvalidOperationException("连接失败但未返回错误信息。");
            completed.Add(new ConnectionTestStageResult(error.Stage, false, timer.Elapsed, error.UserMessage));
            return new ConnectionProfileTestResult(
                updatedDraft,
                completed,
                error,
                outcome.HostKeyFailure);
        }
        finally
        {
            foreach (var writtenReference in writtenReferences.AsEnumerable().Reverse())
            {
                await _transientStore.DeleteAsync(writtenReference, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<ConnectionProfileSaveResult> CompleteCommittedSaveAsync(
        ConnectionProfile? oldProfile,
        ConnectionProfile savedProfile)
    {
        try
        {
            await DeleteUnsharedOldReferencesAsync(oldProfile, savedProfile, CancellationToken.None)
                .ConfigureAwait(false);
            return new ConnectionProfileSaveResult(savedProfile);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            return new ConnectionProfileSaveResult(
                savedProfile,
                "连接已保存，但旧凭据未能清理。");
        }
    }

    private static void EnsureWritten(CredentialStoreWriteResult write)
    {
        if (write.Result != CredentialStoreCompareExchangeResult.Succeeded || write.WrittenVersion is null)
        {
            throw new InvalidOperationException("凭据已被其他操作修改，请重新载入后再试。");
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
            .Where(static candidate => IsManagedStore(candidate.Store))
            .Except(retained))
        {
            var candidates = await _retirementService.CaptureAsync(
                [oldReference], cancellationToken).ConfigureAwait(false);
            if (await _retirementService.RetireUnreferencedAsync(candidates, cancellationToken)
                    .ConfigureAwait(false) != 0)
            {
                throw new InvalidOperationException("旧凭据未能安全清理。");
            }
        }
    }

    private async Task RestoreCredentialAsync(
        CredentialReference reference,
        CredentialStoreVersion writtenVersion,
        CredentialStoreSnapshot? previous,
        List<Exception> errors)
    {
        try
        {
            var result = await _credentialStore.CompareExchangeAsync(
                reference,
                writtenVersion,
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

    private static bool IsSupportedStore(string store) =>
        IsManagedStore(store) || string.Equals(store, "ask", StringComparison.OrdinalIgnoreCase);

    private static bool IsManagedStore(string store) =>
        string.Equals(store, "windows", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(store, "vault", StringComparison.OrdinalIgnoreCase);

    private static CredentialReference ReferenceFor(Guid id, CredentialSaveMode mode) => mode switch
    {
        CredentialSaveMode.WindowsCredentialManager => CredentialReference.Create("windows", $"profile/{id:D}/mac"),
        CredentialSaveMode.EncryptedVault => CredentialReference.Create("vault", $"profile/{id:D}/mac"),
        CredentialSaveMode.AskEveryTime => CredentialReference.Create("ask", $"profile/{id:D}/mac"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static ConnectionProfile WithSshCredentialReference(
        ConnectionProfile profile,
        ConnectionProfile? oldProfile,
        CredentialSaveMode mode,
        bool replaceSecret)
    {
        if (profile.SshProfile is not { } ssh)
        {
            return profile;
        }

        if (!replaceSecret && mode != CredentialSaveMode.AskEveryTime)
        {
            var oldSsh = oldProfile?.SshProfile;
            var preservedReference = ssh.PrivateKeyPath is null
                ? ssh.PasswordCredentialReference ??
                    (oldSsh is { PrivateKeyPath: null }
                        ? oldSsh.PasswordCredentialReference
                        : null)
                : ssh.PrivateKeyPassphraseCredentialReference ??
                    (oldSsh?.PrivateKeyPath is not null
                        ? oldSsh.PrivateKeyPassphraseCredentialReference
                        : null);
            if (preservedReference is not null)
            {
                return profile.WithSsh(ssh.PrivateKeyPath is null
                    ? ssh.WithAuthenticationCredentials(preservedReference, null)
                    : ssh.WithAuthenticationCredentials(null, preservedReference));
            }
            return profile;
        }

        var store = mode switch
        {
            CredentialSaveMode.WindowsCredentialManager => "windows",
            CredentialSaveMode.EncryptedVault => "vault",
            CredentialSaveMode.AskEveryTime => "ask",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var purpose = ssh.PrivateKeyPath is null ? "ssh-password" : "ssh-passphrase";
        var reference = CredentialReference.Create(
            store,
            $"profile/{profile.Id:D}/{purpose}/{Guid.NewGuid():N}");
        return profile.WithSsh(ssh.PrivateKeyPath is null
            ? ssh.WithAuthenticationCredentials(reference, null)
            : ssh.WithAuthenticationCredentials(null, reference));
    }

    private static CredentialReference GenerationReference(
        Guid id,
        CredentialSaveMode mode,
        string purpose) => mode switch
        {
            CredentialSaveMode.WindowsCredentialManager => CredentialReference.Create(
                "windows", $"profile/{id:D}/{purpose}/{Guid.NewGuid():N}"),
            CredentialSaveMode.EncryptedVault => CredentialReference.Create(
                "vault", $"profile/{id:D}/{purpose}/{Guid.NewGuid():N}"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

}
