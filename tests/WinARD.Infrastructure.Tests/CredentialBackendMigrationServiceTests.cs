using System.Text;
using Microsoft.Data.Sqlite;
using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Domain.Settings;
using WinARD.Infrastructure.Database;
using WinARD.Infrastructure.Settings;
using Xunit;

#pragma warning disable CA1707
#pragma warning disable CA1001

namespace WinARD.Infrastructure.Tests;

public sealed class CredentialBackendMigrationServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"));
    private WinArdDatabase _database = null!;
    private FakeCredentialStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = new WinArdDatabase(Path.Combine(_directory, "winard.db"));
        await _database.InitializeAsync(CancellationToken.None);
        _store = new FakeCredentialStore();
        await InsertDeviceAsync("windows", "profile/one/mac");
        _store.Put(CredentialReference.Create("windows", "profile/one/mac"), "alpha");
    }

    [Fact]
    public async Task Successful_copy_and_readback_commits_references_and_default_before_optional_source_delete()
    {
        var service = CreateService();

        var kept = await service.MigrateAsync(
            CredentialBackend.EncryptedVault,
            deleteUnreferencedSources: false,
            CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.Succeeded, kept.Code);
        Assert.Equal(1, kept.ManagedReferenceCount);
        Assert.Equal(1, kept.MigratedReferenceCount);
        Assert.Equal(0, kept.CompensationFailureCount);
        Assert.Equal(0, kept.SourceCleanupFailureCount);
        var migrated = await ReadMacReferenceAsync();
        Assert.Equal("vault", migrated.Store);
        Assert.Equal("alpha", _store.Text(migrated));
        Assert.Equal("alpha", _store.Text(CredentialReference.Create("windows", "profile/one/mac")));
        Assert.Equal(CredentialBackend.EncryptedVault, await ReadDefaultBackendAsync());
    }

    [Fact]
    public async Task User_accepted_cleanup_deletes_only_committed_unreferenced_sources()
    {
        var source = CredentialReference.Create("windows", "profile/one/mac");

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault,
            deleteUnreferencedSources: true,
            CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.Succeeded, result.Code);
        Assert.Null(_store.Text(source));
        Assert.Equal("alpha", _store.Text(await ReadMacReferenceAsync()));
    }

    [Fact]
    public async Task Cleanup_decision_is_requested_only_after_database_commit()
    {
        var committedBeforePrompt = false;

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault,
            async (_, cancellationToken) =>
            {
                committedBeforePrompt = (await ReadMacReferenceAsync()).Store == "vault";
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            },
            CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.Succeeded, result.Code);
        Assert.True(committedBeforePrompt);
    }

    [Fact]
    public async Task Source_changed_after_commit_is_not_deleted_by_cleanup()
    {
        var source = CredentialReference.Create("windows", "profile/one/mac");

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault,
            (_, _) =>
            {
                _store.Put(source, "newer");
                return ValueTask.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.SucceededWithCleanupFailures, result.Code);
        Assert.Equal(1, result.SourceCleanupFailureCount);
        Assert.Equal("newer", _store.Text(source));
    }

    [Theory]
    [InlineData(CredentialBackend.EncryptedVault)]
    [InlineData(CredentialBackend.AskEveryTime)]
    public async Task Cleanup_confirmation_failure_after_commit_keeps_sources_and_returns_stable_success(
        CredentialBackend backend)
    {
        var source = CredentialReference.Create("windows", "profile/one/mac");

        var result = await CreateService().MigrateAsync(
            backend,
            (_, _) => ValueTask.FromException<bool>(
                new InvalidOperationException("sensitive confirmation failure")),
            CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.Succeeded, result.Code);
        Assert.Equal("alpha", _store.Text(source));
        Assert.Equal(backend, await ReadDefaultBackendAsync());
        Assert.DoesNotContain("sensitive", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task References_already_in_target_backend_are_not_rewritten_or_cleaned()
    {
        await ExecuteAsync("""
            UPDATE devices SET credential_store = 'vault', credential_key = 'existing/vault';
            UPDATE app_settings SET setting_value = '2' WHERE setting_key = 'credential_backend';
            """);
        _store = new FakeCredentialStore();
        var existing = CredentialReference.Create("vault", "existing/vault");
        _store.Put(existing, "alpha");

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.Succeeded, result.Code);
        Assert.Equal(existing, await ReadMacReferenceAsync());
        Assert.Equal("alpha", _store.Text(existing));
        Assert.Equal(0, result.MigratedReferenceCount);
    }

    [Fact]
    public async Task Empty_database_still_requires_vault_validation_when_vault_becomes_default()
    {
        await ExecuteAsync("UPDATE devices SET credential_store = 'ask', credential_key = '';");
        var requiresVault = false;
        var service = CreateService((_, requested, _) =>
        {
            requiresVault = requested;
            return ValueTask.CompletedTask;
        });

        var result = await service.MigrateAsync(
            CredentialBackend.EncryptedVault, false, CancellationToken.None);

        Assert.True(requiresVault);
        Assert.Equal(CredentialBackendMigrationCode.Succeeded, result.Code);
    }

    [Fact]
    public async Task Existing_vault_reference_still_requires_validation_when_vault_becomes_default()
    {
        await ExecuteAsync(
            "UPDATE devices SET credential_store = 'vault', credential_key = 'existing/vault';");
        _store = new FakeCredentialStore();
        _store.Put(CredentialReference.Create("vault", "existing/vault"), "alpha");
        var requiresVault = false;
        var service = CreateService((_, requested, _) =>
        {
            requiresVault = requested;
            return ValueTask.CompletedTask;
        });

        var result = await service.MigrateAsync(
            CredentialBackend.EncryptedVault, false, CancellationToken.None);

        Assert.True(requiresVault);
        Assert.Equal(CredentialBackendMigrationCode.Succeeded, result.Code);
        Assert.Equal(CredentialBackend.EncryptedVault, await ReadDefaultBackendAsync());
    }

    [Fact]
    public async Task Pre_cancelled_migration_returns_stable_cancelled_result()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, cancellation.Token);

        Assert.Equal(CredentialBackendMigrationCode.Cancelled, result.Code);
        Assert.Equal("windows", (await ReadMacReferenceAsync()).Store);
        Assert.Equal(CredentialBackend.Windows, await ReadDefaultBackendAsync());
    }

    [Theory]
    [InlineData(CredentialBackend.Windows)]
    [InlineData(CredentialBackend.AskEveryTime)]
    public async Task Locked_vault_source_requires_unlock_before_migrating_to_non_vault_target(
        CredentialBackend target)
    {
        await ExecuteAsync("""
            UPDATE devices SET credential_store = 'vault', credential_key = 'profile/one/mac';
            UPDATE app_settings SET setting_value = '2' WHERE setting_key = 'credential_backend';
            """);
        _store = new FakeCredentialStore();
        _store.Put(CredentialReference.Create("vault", "profile/one/mac"), "alpha");
        var vaultUnlockRequested = false;
        var service = CreateService((_, requiresVault, _) =>
        {
            vaultUnlockRequested = requiresVault;
            return ValueTask.FromException(new OperationCanceledException());
        });

        var result = await service.MigrateAsync(target, true, CancellationToken.None);

        Assert.True(vaultUnlockRequested);
        Assert.Equal(CredentialBackendMigrationCode.Cancelled, result.Code);
        Assert.Equal("vault", (await ReadMacReferenceAsync()).Store);
        Assert.Equal(CredentialBackend.EncryptedVault, await ReadDefaultBackendAsync());
    }

    [Theory]
    [InlineData(CredentialBackend.Windows)]
    [InlineData(CredentialBackend.AskEveryTime)]
    public async Task Unlocked_vault_source_migrates_to_non_vault_target(
        CredentialBackend target)
    {
        await ExecuteAsync("""
            UPDATE devices SET credential_store = 'vault', credential_key = 'profile/one/mac';
            UPDATE app_settings SET setting_value = '2' WHERE setting_key = 'credential_backend';
            """);
        _store = new FakeCredentialStore();
        _store.Put(CredentialReference.Create("vault", "profile/one/mac"), "alpha");
        var vaultUnlockRequested = false;
        var service = CreateService((_, requiresVault, _) =>
        {
            vaultUnlockRequested = requiresVault;
            return ValueTask.CompletedTask;
        });

        var result = await service.MigrateAsync(target, false, CancellationToken.None);

        Assert.True(vaultUnlockRequested);
        Assert.Equal(CredentialBackendMigrationCode.Succeeded, result.Code);
        Assert.Equal(target, await ReadDefaultBackendAsync());
        Assert.Equal(target == CredentialBackend.Windows ? "windows" : "ask",
            (await ReadMacReferenceAsync()).Store);
    }

    [Fact]
    public async Task Ask_every_time_confirms_source_cleanup_individually()
    {
        var first = CredentialReference.Create("windows", "profile/one/mac");
        var second = CredentialReference.Create("windows", "profile/one/ssh");
        await InsertSshReferenceAsync(second.Store, second.Key);
        _store.Put(second, "bravo");
        var prompts = new List<CredentialSourceCleanupPrompt>();

        var result = await CreateService().MigrateAsync(
            CredentialBackend.AskEveryTime,
            (prompt, _) =>
            {
                prompts.Add(prompt);
                return ValueTask.FromResult(prompt.ItemNumber == 1);
            },
            CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.Succeeded, result.Code);
        Assert.Equal(2, prompts.Count);
        Assert.All(prompts, prompt => Assert.True(prompt.RequiresIndividualConfirmation));
        Assert.Null(_store.Text(first));
        Assert.Equal("bravo", _store.Text(second));
        Assert.Equal("ask", (await ReadMacReferenceAsync()).Store);
        Assert.Equal("ask", await ReadSshPasswordStoreAsync());
    }

    [Fact]
    public async Task Ask_every_time_cancellation_during_second_source_read_returns_stable_cancelled_result()
    {
        await InsertSshReferenceAsync("windows", "profile/one/ssh");
        _store.Put(CredentialReference.Create("windows", "profile/one/ssh"), "bravo");
        using var cancellation = new CancellationTokenSource();
        _store.OnSourceSnapshot = count =>
        {
            if (count == 2)
            {
                cancellation.Cancel();
            }
        };

        var result = await CreateService().MigrateAsync(
            CredentialBackend.AskEveryTime, true, cancellation.Token);

        Assert.Equal(CredentialBackendMigrationCode.Cancelled, result.Code);
        Assert.Equal("windows", (await ReadMacReferenceAsync()).Store);
        Assert.Equal(CredentialBackend.Windows, await ReadDefaultBackendAsync());
    }

    [Fact]
    public async Task Ask_every_time_failure_during_second_source_read_returns_stable_failure()
    {
        await InsertSshReferenceAsync("windows", "profile/one/ssh");
        _store.Put(CredentialReference.Create("windows", "profile/one/ssh"), "bravo");
        _store.ThrowOnSourceSnapshotNumber = 2;

        var result = await CreateService().MigrateAsync(
            CredentialBackend.AskEveryTime, true, CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.SourceReadFailed, result.Code);
        Assert.Equal("windows", (await ReadMacReferenceAsync()).Store);
        Assert.DoesNotContain("bravo", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Target_validation_failure_leaves_database_and_source_untouched()
    {
        var service = CreateService((_, _, _) => ValueTask.FromException(
            new InvalidOperationException("sensitive target failure")));

        var result = await service.MigrateAsync(
            CredentialBackend.EncryptedVault, true, CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.TargetValidationFailed, result.Code);
        Assert.Equal("windows", (await ReadMacReferenceAsync()).Store);
        Assert.Equal("alpha", _store.Text(CredentialReference.Create("windows", "profile/one/mac")));
        Assert.Equal(CredentialBackend.Windows, await ReadDefaultBackendAsync());
    }

    [Fact]
    public async Task Partial_target_write_failure_compensates_copies_and_preserves_sources()
    {
        await InsertSshReferenceAsync("windows", "profile/one/ssh");
        _store.Put(CredentialReference.Create("windows", "profile/one/ssh"), "bravo");
        _store.FailTargetWriteNumber = 2;

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.TargetWriteFailed, result.Code);
        Assert.Equal(0, _store.References.Count(static item => item.Store == "vault"));
        Assert.Equal("windows", (await ReadMacReferenceAsync()).Store);
        Assert.Equal("alpha", _store.Text(CredentialReference.Create("windows", "profile/one/mac")));
        Assert.Equal("bravo", _store.Text(CredentialReference.Create("windows", "profile/one/ssh")));
    }

    [Fact]
    public async Task Uncertain_target_write_is_read_back_and_compensated()
    {
        _store.ThrowAfterTargetWrite = true;

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.TargetWriteFailed, result.Code);
        Assert.Equal(0, result.CompensationFailureCount);
        Assert.Equal(0, _store.References.Count(static item => item.Store == "vault"));
        Assert.Equal("alpha", _store.Text(CredentialReference.Create("windows", "profile/one/mac")));
    }

    [Fact]
    public async Task Target_readback_mismatch_compensates_without_exposing_secret_details()
    {
        _store.ReturnMismatchedTargetReadback = true;

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.TargetVerificationFailed, result.Code);
        Assert.Equal(0, _store.References.Count(static item => item.Store == "vault"));
        Assert.DoesNotContain("alpha", result.ToString(), StringComparison.Ordinal);
        Assert.Equal("windows", (await ReadMacReferenceAsync()).Store);
    }

    [Fact]
    public async Task Database_commit_failure_compensates_target_and_keeps_original_reference()
    {
        await ExecuteAsync("""
            CREATE TRIGGER fail_backend_update
            BEFORE UPDATE ON app_settings
            WHEN OLD.setting_key = 'credential_backend'
            BEGIN SELECT RAISE(ABORT, 'fixture commit failure'); END;
            """);

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.DatabaseCommitFailed, result.Code);
        Assert.Equal("windows", (await ReadMacReferenceAsync()).Store);
        Assert.Equal(CredentialBackend.Windows, await ReadDefaultBackendAsync());
        Assert.Equal(0, _store.References.Count(static item => item.Store == "vault"));
    }

    [Fact]
    public async Task Cancellation_compensates_target_and_keeps_original_reference()
    {
        using var cancellation = new CancellationTokenSource();
        _store.AfterTargetWrite = cancellation.Cancel;

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, cancellation.Token);

        Assert.Equal(CredentialBackendMigrationCode.Cancelled, result.Code);
        Assert.Equal("windows", (await ReadMacReferenceAsync()).Store);
        Assert.Equal(0, _store.References.Count(static item => item.Store == "vault"));
    }

    [Fact]
    public async Task Concurrent_profile_reference_change_rejects_database_commit()
    {
        _store.AfterTargetRead = () => ExecuteAsync(
            "UPDATE devices SET credential_store = 'windows', credential_key = 'profile/concurrent/mac';").GetAwaiter().GetResult();

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.ConcurrentReferenceChanged, result.Code);
        Assert.Equal("profile/concurrent/mac", (await ReadMacReferenceAsync()).Key);
        Assert.Equal(CredentialBackend.Windows, await ReadDefaultBackendAsync());
        Assert.Equal("alpha", _store.Text(CredentialReference.Create("windows", "profile/one/mac")));
    }

    [Fact]
    public async Task Concurrent_source_secret_change_before_commit_compensates_target_and_preserves_database()
    {
        var source = CredentialReference.Create("windows", "profile/one/mac");
        _store.AfterTargetRead = () => _store.Put(source, "newer");

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.ConcurrentReferenceChanged, result.Code);
        Assert.Equal(source, await ReadMacReferenceAsync());
        Assert.Equal(CredentialBackend.Windows, await ReadDefaultBackendAsync());
        Assert.Equal("newer", _store.Text(source));
        Assert.Equal(0, _store.References.Count(static reference => reference.Store == "vault"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_revalidation_failure_or_cancellation_compensates_before_database_commit(
        bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            _store.OnSourceSnapshot = count =>
            {
                if (count == 2)
                {
                    cancellation.Cancel();
                }
            };
        }
        else
        {
            _store.ThrowOnSourceSnapshotNumber = 2;
        }

        var result = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, cancellation.Token);

        Assert.Equal(
            cancel ? CredentialBackendMigrationCode.Cancelled : CredentialBackendMigrationCode.SourceReadFailed,
            result.Code);
        Assert.Equal("windows", (await ReadMacReferenceAsync()).Store);
        Assert.Equal(0, _store.References.Count(static reference => reference.Store == "vault"));
    }

    [Theory]
    [InlineData(CredentialBackend.EncryptedVault)]
    [InlineData(CredentialBackend.AskEveryTime)]
    public async Task Cleanup_skips_source_when_new_managed_reference_is_added_after_commit(
        CredentialBackend target)
    {
        var source = CredentialReference.Create("windows", "profile/one/mac");
        var inserted = false;

        var result = await CreateService().MigrateAsync(
            target,
            async (_, _) =>
            {
                if (!inserted)
                {
                    inserted = true;
                    await InsertDeviceAsync(source.Store, source.Key, "late-reference.local");
                }
                return true;
            },
            CancellationToken.None);

        Assert.Equal(CredentialBackendMigrationCode.SucceededWithCleanupFailures, result.Code);
        Assert.Equal(1, result.SourceCleanupFailureCount);
        Assert.Equal("alpha", _store.Text(source));
    }

    [Fact]
    public async Task Cleanup_waits_for_uncommitted_writer_then_preserves_new_ssh_passphrase_reference()
    {
        var source = CredentialReference.Create("windows", "profile/one/mac");
        await using var writer = _database.CreateConnection();
        await writer.OpenAsync();
        var busyTimeout = writer.CreateCommand();
        busyTimeout.CommandText = "PRAGMA busy_timeout = 5000;";
        await busyTimeout.ExecuteNonQueryAsync();
        SqliteTransaction? writerTransaction = null;
        var writerReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var migration = Task.Run(() => CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault,
            async (_, cancellationToken) =>
            {
                writerTransaction = writer.BeginTransaction(
                    System.Data.IsolationLevel.Serializable, deferred: false);
                var command = writer.CreateCommand();
                command.Transaction = writerTransaction;
                command.CommandText = """
                    INSERT INTO ssh_profiles (
                        device_id, ssh_host, ssh_port, ssh_username, target_host, target_port,
                        passphrase_credential_store, passphrase_credential_key)
                    SELECT id, 'late-jump.local', 22, 'alex', 'late-target.local', 5900,
                           $store, $key
                    FROM devices LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$store", source.Store);
                command.Parameters.AddWithValue("$key", source.Key);
                await command.ExecuteNonQueryAsync(cancellationToken);
                writerReady.SetResult();
                return true;
            },
            CancellationToken.None));

        await writerReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            migration.WaitAsync(TimeSpan.FromMilliseconds(250)));
        await writerTransaction!.CommitAsync();
        await writerTransaction.DisposeAsync();

        var result = await migration;

        Assert.Equal(CredentialBackendMigrationCode.SucceededWithCleanupFailures, result.Code);
        Assert.Equal(1, result.SourceCleanupFailureCount);
        Assert.Equal("alpha", _store.Text(source));
    }

    [Fact]
    public async Task Compensation_and_source_cleanup_failures_are_reported_only_as_stable_counts()
    {
        _store.ReturnMismatchedTargetReadback = true;
        _store.FailTargetCompensation = true;
        var compensation = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, CancellationToken.None);
        Assert.Equal(CredentialBackendMigrationCode.TargetVerificationFailed, compensation.Code);
        Assert.Equal(1, compensation.CompensationFailureCount);

        _store = new FakeCredentialStore { FailSourceDelete = true };
        _store.Put(CredentialReference.Create("windows", "profile/one/mac"), "alpha");
        var cleanup = await CreateService().MigrateAsync(
            CredentialBackend.EncryptedVault, true, CancellationToken.None);
        Assert.Equal(CredentialBackendMigrationCode.SucceededWithCleanupFailures, cleanup.Code);
        Assert.Equal(1, cleanup.SourceCleanupFailureCount);
        Assert.DoesNotContain("alpha", cleanup.ToString(), StringComparison.Ordinal);
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private CredentialBackendMigrationService CreateService(
        Func<CredentialBackend, bool, CancellationToken, ValueTask>? validator = null) =>
        new(_database, _store, validator ?? ((_, _, _) => ValueTask.CompletedTask));

    private Task InsertDeviceAsync(string store, string key) =>
        InsertDeviceAsync(store, key, "fixture.local");

    private async Task InsertDeviceAsync(string store, string key, string host)
    {
        await ExecuteAsync($"""
            INSERT INTO devices (
                id, display_name, host, port, mac_username, transport_mode,
                credential_store, credential_key, created_utc, updated_utc)
            VALUES (
                '{Guid.NewGuid():D}', 'Fixture', '{host}', 5900, 'alex', 0,
                '{store}', '{key}', '2026-08-19T00:00:00Z', '2026-08-19T00:00:00Z');
            """);
    }

    private Task InsertSshReferenceAsync(string store, string key) => ExecuteAsync($"""
        INSERT INTO ssh_profiles (
            device_id, ssh_host, ssh_port, ssh_username, target_host, target_port,
            password_credential_store, password_credential_key)
        SELECT id, 'jump.local', 22, 'alex', 'target.local', 5900, '{store}', '{key}'
        FROM devices LIMIT 1;
        """);

    private async Task<CredentialReference> ReadMacReferenceAsync()
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT credential_store, credential_key FROM devices LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return CredentialReference.Create(reader.GetString(0), reader.GetString(1));
    }

    private async Task<CredentialBackend> ReadDefaultBackendAsync()
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM app_settings WHERE setting_key = 'credential_backend';";
        return (CredentialBackend)Convert.ToInt32(
            await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string> ReadSshPasswordStoreAsync()
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT password_credential_store FROM ssh_profiles LIMIT 1;";
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<CredentialReference, Entry> _entries = [];
        private long _version;
        private int _targetWrites;
        private int _sourceSnapshots;

        public IEnumerable<CredentialReference> References => _entries.Keys;
        public int FailTargetWriteNumber { get; set; }
        public bool ReturnMismatchedTargetReadback { get; set; }
        public bool FailTargetCompensation { get; set; }
        public bool FailSourceDelete { get; set; }
        public bool ThrowAfterTargetWrite { get; set; }
        public Action? AfterTargetWrite { get; set; }
        public Action? AfterTargetRead { get; set; }
        public Action<int>? OnSourceSnapshot { get; set; }
        public int ThrowOnSourceSnapshotNumber { get; set; }

        public void Put(CredentialReference reference, string value) =>
            _entries[reference] = new Entry(Encoding.UTF8.GetBytes(value), ++_version);

        public string? Text(CredentialReference reference) => _entries.TryGetValue(reference, out var entry)
            ? Encoding.UTF8.GetString(entry.Bytes)
            : null;

        public ValueTask SaveAsync(CredentialReference reference, ISecret secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ISecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.Store == "vault" && AfterTargetRead is { } after)
            {
                AfterTargetRead = null;
                after();
            }
            if (!_entries.TryGetValue(reference, out var entry))
            {
                return ValueTask.FromResult<ISecret?>(null);
            }
            var bytes = reference.Store == "vault" && ReturnMismatchedTargetReadback
                ? Encoding.UTF8.GetBytes("mismatch")
                : entry.Bytes;
            return ValueTask.FromResult<ISecret?>(new TestSecret(bytes));
        }

        public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(
            CredentialReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_entries.ContainsKey(reference) && reference.Store == "windows")
            {
                _sourceSnapshots++;
                OnSourceSnapshot?.Invoke(_sourceSnapshots);
                cancellationToken.ThrowIfCancellationRequested();
                if (_sourceSnapshots == ThrowOnSourceSnapshotNumber)
                {
                    throw new IOException("sensitive source read failure");
                }
            }
            return ValueTask.FromResult(_entries.TryGetValue(reference, out var entry)
                ? new CredentialStoreSnapshot(
                    new TestSecret(entry.Bytes),
                    CredentialStoreVersion.CopyFrom(BitConverter.GetBytes(entry.Version)))
                : null);
        }

        public async ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
            CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken)
        {
            using var result = await CompareExchangeWithVersionAsync(
                reference, expectedVersion, replacement, cancellationToken);
            return result.Result;
        }

        public ValueTask<CredentialStoreWriteResult> CompareExchangeWithVersionAsync(
            CredentialReference reference,
            CredentialStoreVersion? expectedVersion,
            ISecret? replacement,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasCurrent = _entries.TryGetValue(reference, out var current);
            using var currentVersion = hasCurrent
                ? CredentialStoreVersion.CopyFrom(BitConverter.GetBytes(current!.Version))
                : null;
            if (hasCurrent != (expectedVersion is not null) ||
                (hasCurrent && !expectedVersion!.FixedTimeEquals(currentVersion!)))
            {
                return ValueTask.FromResult(new CredentialStoreWriteResult(
                    CredentialStoreCompareExchangeResult.Conflict, null));
            }

            if (reference.Store == "vault" && replacement is not null &&
                ++_targetWrites == FailTargetWriteNumber)
            {
                throw new IOException("sensitive target write failure");
            }
            if (reference.Store == "vault" && replacement is null && FailTargetCompensation)
            {
                throw new IOException("sensitive compensation failure");
            }
            if (reference.Store == "windows" && replacement is null && FailSourceDelete)
            {
                throw new IOException("sensitive source delete failure");
            }

            if (replacement is null)
            {
                _entries.Remove(reference);
                return ValueTask.FromResult(new CredentialStoreWriteResult(
                    CredentialStoreCompareExchangeResult.Succeeded, null));
            }

            var bytes = new byte[replacement.Length];
            replacement.CopyTo(bytes);
            var version = ++_version;
            _entries[reference] = new Entry(bytes, version);
            AfterTargetWrite?.Invoke();
            if (reference.Store == "vault" && ThrowAfterTargetWrite)
            {
                ThrowAfterTargetWrite = false;
                throw new IOException("sensitive uncertain target write");
            }
            return ValueTask.FromResult(new CredentialStoreWriteResult(
                CredentialStoreCompareExchangeResult.Succeeded,
                CredentialStoreVersion.CopyFrom(BitConverter.GetBytes(version))));
        }

        public ValueTask DeleteAsync(CredentialReference reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.Store == "windows" && FailSourceDelete)
            {
                throw new IOException("sensitive source delete failure");
            }
            _entries.Remove(reference);
            return ValueTask.CompletedTask;
        }

        private sealed record Entry(byte[] Bytes, long Version);
    }

    private sealed class TestSecret : ISecret
    {
        private byte[]? _bytes;
        public TestSecret(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();
        public int Length => _bytes?.Length ?? throw new ObjectDisposedException(nameof(TestSecret));
        public void CopyTo(Span<byte> destination) => (_bytes ?? throw new ObjectDisposedException(nameof(TestSecret))).CopyTo(destination);
        public ISecret Clone() => new TestSecret(_bytes ?? throw new ObjectDisposedException(nameof(TestSecret)));
        public void Dispose() => _bytes = null;
    }
}
