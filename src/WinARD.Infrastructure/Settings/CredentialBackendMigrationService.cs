using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Domain.Settings;
using WinARD.Infrastructure.Database;

namespace WinARD.Infrastructure.Settings;

public enum CredentialBackendMigrationCode
{
    Succeeded,
    SucceededWithCleanupFailures,
    TargetValidationFailed,
    SourceReadFailed,
    TargetWriteFailed,
    TargetVerificationFailed,
    ConcurrentReferenceChanged,
    DatabaseCommitFailed,
    Cancelled,
}

public sealed record CredentialBackendMigrationResult(
    CredentialBackendMigrationCode Code,
    int ManagedReferenceCount,
    int MigratedReferenceCount,
    int CompensationFailureCount,
    int SourceCleanupFailureCount);

public sealed record CredentialSourceCleanupPrompt(
    int ItemNumber,
    int TotalCount,
    bool RequiresIndividualConfirmation);

public sealed class CredentialBackendMigrationService
{
    private readonly WinArdDatabase _database;
    private readonly ICredentialStore _store;
    private readonly Func<CredentialBackend, CancellationToken, ValueTask> _validateTarget;

    public CredentialBackendMigrationService(
        WinArdDatabase database,
        ICredentialStore store,
        Func<CredentialBackend, CancellationToken, ValueTask> validateTarget)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validateTarget = validateTarget ?? throw new ArgumentNullException(nameof(validateTarget));
    }

    public async Task<CredentialBackendMigrationResult> MigrateAsync(
        CredentialBackend targetBackend,
        bool deleteUnreferencedSources,
        CancellationToken cancellationToken) => await MigrateAsync(
            targetBackend,
            (_, _) => ValueTask.FromResult(deleteUnreferencedSources),
            cancellationToken).ConfigureAwait(false);

    public async Task<CredentialBackendMigrationResult> MigrateAsync(
        CredentialBackend targetBackend,
        Func<CredentialSourceCleanupPrompt, CancellationToken, ValueTask<bool>> confirmSourceCleanup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmSourceCleanup);
        if (!Enum.IsDefined(targetBackend))
        {
            throw new ArgumentOutOfRangeException(nameof(targetBackend));
        }

        try
        {
            await _validateTarget(targetBackend, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Empty(CredentialBackendMigrationCode.Cancelled);
        }
        catch
        {
            return Empty(CredentialBackendMigrationCode.TargetValidationFailed);
        }

        var managed = await ReadManagedReferencesAsync(cancellationToken).ConfigureAwait(false);
        var targetStore = StoreName(targetBackend);
        var sources = managed
            .Where(reference => !string.Equals(
                reference.Store, targetStore, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();
        var settingsState = await ReadSettingsStateAsync(cancellationToken).ConfigureAwait(false);
        if (targetBackend == CredentialBackend.AskEveryTime)
        {
            var cleanupVersions = new List<SourceCleanup>(sources.Length);
            try
            {
                foreach (var source in sources)
                {
                    using var snapshot = await _store.ReadSnapshotAsync(source, cancellationToken)
                        .ConfigureAwait(false);
                    if (snapshot is null)
                    {
                        DisposeCleanupVersions(cleanupVersions);
                        return new(CredentialBackendMigrationCode.SourceReadFailed, managed.Count, 0, 0, 0);
                    }
                    cleanupVersions.Add(new SourceCleanup(source, snapshot.Version.Clone()));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DisposeCleanupVersions(cleanupVersions);
                return new(CredentialBackendMigrationCode.Cancelled, managed.Count, 0, 0, 0);
            }
            catch
            {
                DisposeCleanupVersions(cleanupVersions);
                return new(CredentialBackendMigrationCode.SourceReadFailed, managed.Count, 0, 0, 0);
            }
            var rewrites = sources.Select(source => new ReferenceRewrite(
                source, TargetReference(targetBackend))).ToArray();
            var commit = await TryCommitAsync(
                managed, rewrites, settingsState, targetBackend, cancellationToken)
                .ConfigureAwait(false);
            if (commit != CredentialBackendMigrationCode.Succeeded)
            {
                DisposeCleanupVersions(cleanupVersions);
                return new(commit, managed.Count, 0, 0, 0);
            }
            var askCleanupFailures = 0;
            for (var index = 0; index < cleanupVersions.Count; index++)
            {
                var cleanup = cleanupVersions[index];
                var approved = await ConfirmCleanupAsync(
                    confirmSourceCleanup,
                    new CredentialSourceCleanupPrompt(
                        index + 1, cleanupVersions.Count, RequiresIndividualConfirmation: true),
                    cancellationToken).ConfigureAwait(false);
                if (approved)
                {
                    askCleanupFailures += await CleanupSourcesAsync([cleanup]).ConfigureAwait(false);
                }
                else
                {
                    cleanup.Dispose();
                }
            }
            return new(
                askCleanupFailures == 0
                    ? CredentialBackendMigrationCode.Succeeded
                    : CredentialBackendMigrationCode.SucceededWithCleanupFailures,
                managed.Count, sources.Length, 0, askCleanupFailures);
        }
        var copies = new List<CredentialCopy>(sources.Length);
        var failure = CredentialBackendMigrationCode.Succeeded;
        var uncertainTargetWriteCount = 0;
        try
        {
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var sourceSnapshot = await _store.ReadSnapshotAsync(source, cancellationToken)
                    .ConfigureAwait(false);
                if (sourceSnapshot is null)
                {
                    failure = CredentialBackendMigrationCode.SourceReadFailed;
                    break;
                }

                var target = TargetReference(targetBackend);
                using var priorTarget = await _store.ReadSnapshotAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                CredentialStoreWriteResult write;
                try
                {
                    write = await _store.CompareExchangeWithVersionAsync(
                        target,
                        priorTarget?.Version,
                        sourceSnapshot.Secret,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    uncertainTargetWriteCount += await CaptureUncertainTargetAsync(
                        source, target, sourceSnapshot, priorTarget, copies).ConfigureAwait(false);
                    failure = CredentialBackendMigrationCode.Cancelled;
                    break;
                }
                catch
                {
                    uncertainTargetWriteCount += await CaptureUncertainTargetAsync(
                        source, target, sourceSnapshot, priorTarget, copies).ConfigureAwait(false);
                    failure = CredentialBackendMigrationCode.TargetWriteFailed;
                    break;
                }
                using (write)
                {
                    if (write.Result != CredentialStoreCompareExchangeResult.Succeeded ||
                        write.WrittenVersion is null)
                    {
                        failure = CredentialBackendMigrationCode.TargetWriteFailed;
                        break;
                    }
                    copies.Add(new CredentialCopy(
                        source,
                        target,
                        sourceSnapshot.Version.Clone(),
                        write.WrittenVersion.Clone(),
                        priorTarget?.Secret.Clone()));
                }

                using var readback = await _store.ReadAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                if (readback is null || !SecretsEqual(sourceSnapshot.Secret, readback))
                {
                    failure = CredentialBackendMigrationCode.TargetVerificationFailed;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = CredentialBackendMigrationCode.Cancelled;
        }
        catch
        {
            failure = CredentialBackendMigrationCode.SourceReadFailed;
        }

        if (failure != CredentialBackendMigrationCode.Succeeded)
        {
            var compensationFailures = await CompensateAsync(copies).ConfigureAwait(false);
            DisposeCopies(copies);
            return new(
                failure,
                managed.Count,
                0,
                compensationFailures + uncertainTargetWriteCount,
                0);
        }

        var commitCode = await TryCommitAsync(
            managed,
            copies.Select(static copy => new ReferenceRewrite(copy.Source, copy.Target)).ToArray(),
            settingsState,
            targetBackend,
            cancellationToken).ConfigureAwait(false);
        if (commitCode != CredentialBackendMigrationCode.Succeeded)
        {
            var compensationFailures = await CompensateAsync(copies).ConfigureAwait(false);
            DisposeCopies(copies);
            return new(commitCode, managed.Count, 0, compensationFailures, 0);
        }

        var deleteSources = await ConfirmCleanupAsync(
            confirmSourceCleanup,
            new CredentialSourceCleanupPrompt(
                sources.Length, sources.Length, RequiresIndividualConfirmation: false),
            cancellationToken).ConfigureAwait(false);
        var cleanupFailures = deleteSources
            ? await CleanupSourcesAsync(copies.Select(static copy =>
                new SourceCleanup(copy.Source, copy.SourceVersion.Clone())).ToArray()).ConfigureAwait(false)
            : 0;
        DisposeCopies(copies);
        return new(
            cleanupFailures == 0
                ? CredentialBackendMigrationCode.Succeeded
                : CredentialBackendMigrationCode.SucceededWithCleanupFailures,
            managed.Count,
            copies.Count,
            0,
            cleanupFailures);
    }

    private async Task<CredentialBackendMigrationCode> TryCommitAsync(
        IReadOnlyList<CredentialReference> expectedReferences,
        IReadOnlyList<ReferenceRewrite> rewrites,
        SettingsState settingsState,
        CredentialBackend targetBackend,
        CancellationToken cancellationToken)
    {
        await using var connection = _database.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await WinArdDatabase.ConfigureConnectionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(
                IsolationLevel.Serializable, deferred: false);
            try
            {
                var actualReferences = await ReadManagedReferencesAsync(
                    connection, transaction, cancellationToken).ConfigureAwait(false);
                if (!expectedReferences.SequenceEqual(actualReferences))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return CredentialBackendMigrationCode.ConcurrentReferenceChanged;
                }

                foreach (var rewrite in rewrites)
                {
                    var affected = await UpdateReferenceAsync(
                        connection, transaction, rewrite.Source, rewrite.Target,
                        cancellationToken).ConfigureAwait(false);
                    if (affected != expectedReferences.Count(reference => reference == rewrite.Source))
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return CredentialBackendMigrationCode.ConcurrentReferenceChanged;
                    }
                }

                var setting = connection.CreateCommand();
                setting.Transaction = transaction;
                setting.CommandText = """
                    UPDATE app_settings
                    SET setting_value = $backend
                    WHERE setting_key = 'credential_backend' AND setting_value = $old_backend;
                    """;
                setting.Parameters.AddWithValue("$backend", (int)targetBackend);
                setting.Parameters.AddWithValue("$old_backend", (int)settingsState.Backend);
                if (await setting.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return CredentialBackendMigrationCode.ConcurrentReferenceChanged;
                }
                var revision = connection.CreateCommand();
                revision.Transaction = transaction;
                revision.CommandText = """
                    UPDATE app_settings
                    SET setting_value = $new_revision
                    WHERE setting_key = 'revision' AND setting_value = $old_revision;
                    """;
                revision.Parameters.AddWithValue("$new_revision", checked(settingsState.Revision + 1));
                revision.Parameters.AddWithValue("$old_revision", settingsState.Revision);
                if (await revision.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return CredentialBackendMigrationCode.ConcurrentReferenceChanged;
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CredentialBackendMigrationCode.Succeeded;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return CredentialBackendMigrationCode.Cancelled;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return CredentialBackendMigrationCode.DatabaseCommitFailed;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CredentialBackendMigrationCode.Cancelled;
        }
        catch
        {
            return CredentialBackendMigrationCode.DatabaseCommitFailed;
        }
    }

    private static async Task<int> UpdateReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CredentialReference source,
        CredentialReference target,
        CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var columns in ReferenceColumns)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE {columns.Table}
                SET {columns.Store} = $new_store, {columns.Key} = $new_key
                WHERE {columns.Store} = $old_store AND {columns.Key} = $old_key;
                """;
            command.Parameters.AddWithValue("$new_store", target.Store);
            command.Parameters.AddWithValue("$new_key", target.Key);
            command.Parameters.AddWithValue("$old_store", source.Store);
            command.Parameters.AddWithValue("$old_key", source.Key);
            total += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return total;
    }

    private async Task<IReadOnlyList<CredentialReference>> ReadManagedReferencesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await WinArdDatabase.ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        return await ReadManagedReferencesAsync(connection, transaction: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<CredentialReference>> ReadManagedReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT credential_store, credential_key FROM devices
            WHERE credential_store IN ('windows', 'vault')
            UNION ALL
            SELECT password_credential_store, password_credential_key FROM ssh_profiles
            WHERE password_credential_store IN ('windows', 'vault')
            UNION ALL
            SELECT passphrase_credential_store, passphrase_credential_key FROM ssh_profiles
            WHERE passphrase_credential_store IN ('windows', 'vault')
            ORDER BY 1, 2;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var references = new List<CredentialReference>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            references.Add(CredentialReference.Create(reader.GetString(0), reader.GetString(1)));
        }
        return references;
    }

    private async Task<SettingsState> ReadSettingsStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT setting_value FROM app_settings WHERE setting_key = 'revision'),
                (SELECT setting_value FROM app_settings WHERE setting_key = 'credential_backend');
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("App settings schema is incomplete.");
        }
        return new SettingsState(
            long.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
            (CredentialBackend)int.Parse(
                reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<int> CompensateAsync(IReadOnlyList<CredentialCopy> copies)
    {
        var failures = 0;
        for (var index = copies.Count - 1; index >= 0; index--)
        {
            var copy = copies[index];
            try
            {
                using var result = await _store.CompareExchangeWithVersionAsync(
                    copy.Target,
                    copy.WrittenVersion,
                    copy.PreviousTarget,
                    CancellationToken.None).ConfigureAwait(false);
                if (result.Result != CredentialStoreCompareExchangeResult.Succeeded)
                {
                    failures++;
                }
            }
            catch
            {
                failures++;
            }
        }
        return failures;
    }

    private async Task<int> CaptureUncertainTargetAsync(
        CredentialReference source,
        CredentialReference target,
        CredentialStoreSnapshot sourceSnapshot,
        CredentialStoreSnapshot? priorTarget,
        ICollection<CredentialCopy> copies)
    {
        try
        {
            using var observed = await _store.ReadSnapshotAsync(target, CancellationToken.None)
                .ConfigureAwait(false);
            if (observed is null)
            {
                return 0;
            }
            if (!SecretsEqual(sourceSnapshot.Secret, observed.Secret))
            {
                return 1;
            }
            copies.Add(new CredentialCopy(
                source,
                target,
                sourceSnapshot.Version.Clone(),
                observed.Version.Clone(),
                priorTarget?.Secret.Clone()));
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private async Task<int> CleanupSourcesAsync(IEnumerable<SourceCleanup> sources)
    {
        var failures = 0;
        foreach (var source in sources)
        {
            try
            {
                using var result = await _store.CompareExchangeWithVersionAsync(
                    source.Reference,
                    source.Version,
                    replacement: null,
                    CancellationToken.None).ConfigureAwait(false);
                if (result.Result != CredentialStoreCompareExchangeResult.Succeeded)
                {
                    failures++;
                }
            }
            catch
            {
                failures++;
            }
            finally
            {
                source.Dispose();
            }
        }
        return failures;
    }

    private static void DisposeCleanupVersions(IEnumerable<SourceCleanup> sources)
    {
        foreach (var source in sources)
        {
            source.Dispose();
        }
    }

    private static bool SecretsEqual(ISecret left, ISecret right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }
        var leftBytes = new byte[left.Length];
        var rightBytes = new byte[right.Length];
        try
        {
            left.CopyTo(leftBytes);
            right.CopyTo(rightBytes);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static CredentialReference TargetReference(CredentialBackend backend) =>
        CredentialReference.Create(StoreName(backend), $"migration/{Guid.NewGuid():N}");

    private static string StoreName(CredentialBackend backend) => backend switch
    {
        CredentialBackend.Windows => "windows",
        CredentialBackend.EncryptedVault => "vault",
        CredentialBackend.AskEveryTime => "ask",
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    private static CredentialBackendMigrationResult Empty(CredentialBackendMigrationCode code) =>
        new(code, 0, 0, 0, 0);

    private static async ValueTask<bool> ConfirmCleanupAsync(
        Func<CredentialSourceCleanupPrompt, CancellationToken, ValueTask<bool>> confirmSourceCleanup,
        CredentialSourceCleanupPrompt prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await confirmSourceCleanup(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static void DisposeCopies(IEnumerable<CredentialCopy> copies)
    {
        foreach (var copy in copies)
        {
            copy.Dispose();
        }
    }

    private static readonly ReferenceColumn[] ReferenceColumns =
    [
        new("devices", "credential_store", "credential_key"),
        new("ssh_profiles", "password_credential_store", "password_credential_key"),
        new("ssh_profiles", "passphrase_credential_store", "passphrase_credential_key"),
    ];

    private sealed record ReferenceColumn(string Table, string Store, string Key);

    private sealed record SettingsState(long Revision, CredentialBackend Backend);

    private sealed record ReferenceRewrite(
        CredentialReference Source,
        CredentialReference Target);

    private sealed record CredentialCopy(
        CredentialReference Source,
        CredentialReference Target,
        CredentialStoreVersion SourceVersion,
        CredentialStoreVersion WrittenVersion,
        ISecret? PreviousTarget) : IDisposable
    {
        public void Dispose()
        {
            SourceVersion.Dispose();
            WrittenVersion.Dispose();
            PreviousTarget?.Dispose();
        }
    }

    private sealed record SourceCleanup(
        CredentialReference Reference,
        CredentialStoreVersion Version) : IDisposable
    {
        public void Dispose() => Version.Dispose();
    }
}
