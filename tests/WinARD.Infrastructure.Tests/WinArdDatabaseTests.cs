using Microsoft.Data.Sqlite;
using WinARD.Domain.Connections;
using WinARD.Domain.Security;
using WinARD.Infrastructure.Database;
using WinARD.Infrastructure.Database.Migrations;
using WinARD.Infrastructure.Devices;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Infrastructure.Tests;

public sealed class WinArdDatabaseTests
{
    [Fact]
    public async Task Migration005_adds_app_settings_defaults_after_existing_version_four()
    {
        await using var fixture = await DatabaseFixture.CreateAtVersionThreeAsync();

        await fixture.Database.InitializeAsync(
            [new Migration004QualityScalePercent()],
            CancellationToken.None);

        await fixture.Database.InitializeAsync(CancellationToken.None);

        await using var connection = fixture.Database.CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM app_settings WHERE setting_key IN ('settings_version', 'revision', 'theme', 'diagnostic_level', 'clipboard_default', 'credential_backend', 'vault_idle_minutes');";
        Assert.Equal(7L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(5, await fixture.ReadSchemaVersionAsync());
    }

    [Fact]
    public async Task Migration005_preserves_existing_and_unknown_settings_while_filling_missing_defaults()
    {
        await using var fixture = await DatabaseFixture.CreateAtVersionThreeAsync();
        await fixture.Database.InitializeAsync(
            [new Migration004QualityScalePercent()], CancellationToken.None);
        await using (var connection = fixture.Database.CreateConnection())
        {
            await connection.OpenAsync();
            var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO app_settings(setting_key, setting_value) VALUES
                    ('theme', '2'),
                    ('future_setting', 'preserved');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await fixture.Database.InitializeAsync(CancellationToken.None);

        await using var reopened = fixture.Database.CreateConnection();
        await reopened.OpenAsync();
        var command = reopened.CreateCommand();
        command.CommandText = "SELECT setting_key, setting_value FROM app_settings ORDER BY setting_key;";
        await using var reader = await command.ExecuteReaderAsync();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0), reader.GetString(1));
        }
        Assert.Equal("2", values["theme"]);
        Assert.Equal("preserved", values["future_setting"]);
        Assert.Equal("15", values["vault_idle_minutes"]);
        Assert.Equal(8, values.Count);
    }
    [Fact]
    public void Quality_scale_migration_uses_a_regular_table_rebuild()
    {
        var source = File.ReadAllText(
            Path.Combine(GetRepositoryRoot(), "src", "WinARD.Infrastructure", "Database", "Migrations", "Migration004QualityScalePercent.cs"));

        Assert.DoesNotContain("writable_schema", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlite_schema", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE devices_v4", source, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO devices_v4", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fresh_database_quality_scale_constraint_accepts_percent25_and_rejects_out_of_range()
    {
        using var fixture = new TempDatabase();
        await using var database = new WinArdDatabase(fixture.Path);
        await database.InitializeAsync(CancellationToken.None);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();

        await InsertDeviceWithScaleAsync(connection, 4, "accepted.local");
        await Assert.ThrowsAsync<SqliteException>(() =>
            InsertDeviceWithScaleAsync(connection, 5, "rejected.local"));
    }

    [Fact]
    public async Task Version_three_quality_scales_migrate_in_place_and_percent25_is_allowed()
    {
        await using var fixture = await DatabaseFixture.CreateAtVersionThreeAsync();

        await fixture.Database.InitializeAsync(CancellationToken.None);

        Assert.Equal(5, await fixture.ReadSchemaVersionAsync());
        Assert.Equal(new long[] { 0, 1, 2, 3 }, await fixture.ReadQualityScalesAsync());
        await fixture.InsertDeviceWithQualityScaleAsync(4);
        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, await fixture.ReadQualityScalesAsync());
    }

    [Fact]
    public async Task Quality_scale_migration_preserves_foreign_keys_indexes_and_data_after_reopen()
    {
        await using var fixture = await DatabaseFixture.CreateAtVersionThreeAsync(includeSshProfile: true);
        await fixture.Database.InitializeAsync(CancellationToken.None);
        await fixture.Database.DisposeAsync();

        await using var reopened = new WinArdDatabase(fixture.DatabasePath);
        await reopened.InitializeAsync(CancellationToken.None);
        await using var connection = reopened.CreateConnection();
        await connection.OpenAsync();
        var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await integrity.ExecuteScalarAsync());
        var index = connection.CreateCommand();
        index.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_devices_host_port';";
        Assert.Equal(1L, await index.ExecuteScalarAsync());
        var data = connection.CreateCommand();
        data.CommandText = "SELECT COUNT(*) FROM devices;";
        Assert.Equal(4L, await data.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Cancelled_quality_scale_migration_rolls_back_and_can_resume()
    {
        await using var fixture = await DatabaseFixture.CreateAtVersionThreeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Database.InitializeAsync(cancellation.Token));
        Assert.Equal(3, await fixture.ReadSchemaVersionAsync());

        await fixture.Database.InitializeAsync(CancellationToken.None);
        Assert.Equal(5, await fixture.ReadSchemaVersionAsync());
    }

    [Fact]
    public async Task Failed_quality_scale_migration_rolls_back_schema_and_data()
    {
        await using var fixture = await DatabaseFixture.CreateAtVersionThreeAsync();
        await fixture.InsertOrphanSshProfileAsync();

        await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Database.InitializeAsync(CancellationToken.None));

        Assert.Equal(3, await fixture.ReadSchemaVersionAsync());
        await using var connection = fixture.Database.CreateConnection();
        await connection.OpenAsync();
        var devices = connection.CreateCommand();
        devices.CommandText = "SELECT COUNT(*) FROM devices;";
        Assert.Equal(4L, await devices.ExecuteScalarAsync());
        var orphan = connection.CreateCommand();
        orphan.CommandText = "SELECT COUNT(*) FROM ssh_profiles WHERE device_id = 'orphan';";
        Assert.Equal(1L, await orphan.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Version_two_devices_migrate_to_custom_quality_without_losing_refresh()
    {
        await using var fixture = await DatabaseFixture.CreateAtVersionTwoAsync();
        await fixture.Database.InitializeAsync(CancellationToken.None);
        await using var repository = new SqliteDeviceRepository(fixture.Database);

        var profiles = (await repository.GetAllAsync(CancellationToken.None))
            .ToDictionary(profile => profile.DisplayName, StringComparer.Ordinal);

        Assert.Equal(FrameRefreshPolicy.Automatic, profiles["Automatic Mac"].FrameRefreshPolicy);
        Assert.Equal(FrameRefreshPolicy.Fixed(60), profiles["Fixed Mac"].FrameRefreshPolicy);
        Assert.Equal(FrameRefreshPolicy.Unlimited, profiles["Unlimited Mac"].FrameRefreshPolicy);
        Assert.All(profiles.Values, profile =>
        {
            Assert.Equal(QualityPreset.Custom, profile.Quality.Preset);
            Assert.Null(profile.Quality.TargetBytesPerSecond);
            Assert.Equal(QualityColor.Automatic, profile.Quality.Color);
            Assert.Equal(QualityScale.Automatic, profile.Quality.Scale);
            Assert.True(profile.Quality.AllowAutomaticGrayscale);
            Assert.False(profile.Quality.BandwidthLocked);
            Assert.False(profile.Quality.ColorLocked);
            Assert.False(profile.Quality.ScaleLocked);
            Assert.False(profile.Quality.RefreshLocked);
        });
        var sshProfile = profiles["SSH Mac"];
        Assert.Equal(TransportMode.Ssh, sshProfile.TransportMode);
        Assert.Equal(FrameRefreshPolicy.Fixed(60), sshProfile.FrameRefreshPolicy);
        Assert.Equal(CredentialReference.Create("windows", "mac-password"), sshProfile.CredentialReference);
        var ssh = Assert.IsType<SshProfile>(sshProfile.SshProfile);
        Assert.Equal("jump.local", ssh.Host);
        Assert.Equal(2222, ssh.Port);
        Assert.Equal("jump-user", ssh.Username);
        Assert.Equal("C:\\keys\\id_ed25519", ssh.PrivateKeyPath);
        Assert.Equal("mac.internal", ssh.TargetHost);
        Assert.Equal(5901, ssh.TargetPort);
        Assert.Equal(CredentialReference.Create("windows", "ssh-password"), ssh.PasswordCredentialReference);
        Assert.Equal(CredentialReference.Create("vault", "key-passphrase"), ssh.PrivateKeyPassphraseCredentialReference);
        Assert.Equal("ssh-ed25519", ssh.PinnedHostKeyAlgorithm);
        Assert.Equal("SHA256:legacy", ssh.PinnedHostKeySha256);
        Assert.Equal(new SshHostKeyEndpoint("jump.local", 2222), ssh.HostKeyPin!.Endpoint);
        Assert.Equal("ssh-ed25519", ssh.HostKeyPin.Algorithm);
        Assert.Equal("AAAALegacyKey", ssh.HostKeyPin.PublicKeyBase64);
        Assert.Equal("SHA256:legacy", ssh.HostKeyPin.Fingerprint);
        Assert.Equal(5, await fixture.ReadSchemaVersionAsync());
    }

    [Fact]
    public async Task Failed_migration_rolls_back_schema_and_version()
    {
        using var fixture = new TempDatabase();
        await using var database = new WinArdDatabase(fixture.Path);
        var migration = new FailingMigration();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.InitializeAsync([migration], CancellationToken.None));

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('partial_table', 'schema_version');";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? -1L));
    }

    [Fact]
    public async Task Future_schema_version_is_rejected_without_modification()
    {
        using var fixture = new TempDatabase();
        var builder = new SqliteConnectionStringBuilder { DataSource = fixture.Path, Pooling = false };
        await using (var connection = new SqliteConnection(builder.ToString()))
        {
            await connection.OpenAsync();
            var seed = connection.CreateCommand();
            seed.CommandText = "CREATE TABLE schema_version(version INTEGER NOT NULL); INSERT INTO schema_version VALUES (999);";
            await seed.ExecuteNonQueryAsync();
        }

        await using var database = new WinArdDatabase(fixture.Path);
        await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(() => database.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_initialization_leaves_exactly_one_version_row()
    {
        using var fixture = new TempDatabase();
        var databases = Enumerable.Range(0, 8).Select(_ => new WinArdDatabase(fixture.Path)).ToArray();
        try
        {
            await Task.WhenAll(databases.Select(database => database.InitializeAsync(CancellationToken.None)));
            await using var connection = new SqliteConnection(databases[0].ConnectionString);
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), MIN(version), MAX(version) FROM schema_version;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(5L, reader.GetInt64(1));
            Assert.Equal(5L, reader.GetInt64(2));
        }
        finally
        {
            foreach (var database in databases)
            {
                await database.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Framework_advances_version_after_migration_without_version_sql()
    {
        using var fixture = new TempDatabase();
        await using var database = new WinArdDatabase(fixture.Path);
        var migration = new RecordingMigration(0, 1, "CREATE TABLE migrated(id INTEGER);");

        await database.InitializeAsync([migration], CancellationToken.None);

        Assert.Equal(1, await ReadVersionAsync(database.ConnectionString));
        Assert.Equal(1, migration.ApplyCount);
    }

    [Fact]
    public async Task Migration_gap_is_rejected_before_any_migration_runs()
    {
        using var fixture = new TempDatabase();
        await using var database = new WinArdDatabase(fixture.Path);
        var first = new RecordingMigration(0, 1, "CREATE TABLE first_table(id INTEGER);");
        var gap = new RecordingMigration(2, 3, "CREATE TABLE gap_table(id INTEGER);");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.InitializeAsync([first, gap], CancellationToken.None));

        Assert.Equal(0, first.ApplyCount);
        Assert.Equal(0, gap.ApplyCount);
    }

    [Fact]
    public async Task Out_of_order_migrations_are_rejected_before_any_migration_runs()
    {
        using var fixture = new TempDatabase();
        await using var database = new WinArdDatabase(fixture.Path);
        var second = new RecordingMigration(1, 2, "CREATE TABLE second_table(id INTEGER);");
        var first = new RecordingMigration(0, 1, "CREATE TABLE first_table(id INTEGER);");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.InitializeAsync([second, first], CancellationToken.None));

        Assert.Equal(0, second.ApplyCount);
        Assert.Equal(0, first.ApplyCount);
    }

    [Fact]
    public async Task Duplicate_migration_origins_are_rejected_before_any_migration_runs()
    {
        using var fixture = new TempDatabase();
        await using var database = new WinArdDatabase(fixture.Path);
        var first = new RecordingMigration(0, 1, "CREATE TABLE first_table(id INTEGER);");
        var duplicate = new RecordingMigration(0, 2, "CREATE TABLE duplicate_table(id INTEGER);");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.InitializeAsync([first, duplicate], CancellationToken.None));

        Assert.Equal(0, first.ApplyCount);
        Assert.Equal(0, duplicate.ApplyCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("INSERT INTO schema_version(version) VALUES (0), (0);")]
    [InlineData("INSERT INTO schema_version(version) VALUES (-1);")]
    public async Task Corrupt_schema_version_is_rejected(string rowsSql)
    {
        using var fixture = new TempDatabase();
        await SeedVersionTableAsync(fixture.Path, rowsSql);
        await using var database = new WinArdDatabase(fixture.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_rolls_back_schema_and_version()
    {
        using var fixture = new TempDatabase();
        await using var database = new WinArdDatabase(fixture.Path);
        using var cancellation = new CancellationTokenSource();
        var migration = new CancellingMigration(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            database.InitializeAsync([migration], cancellation.Token));

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('cancelled_table', 'schema_version');";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? -1L));
    }

    private static async Task<int> ReadVersionAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task InsertDeviceWithScaleAsync(
        SqliteConnection connection,
        int scale,
        string host)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO devices (
                id, display_name, host, port, mac_username, transport_mode,
                quality_scale, created_utc, updated_utc)
            VALUES ($id, 'Scale fixture', $host, 5900, 'alex', 0, $scale, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$scale", scale);
        command.Parameters.AddWithValue("$now", "2026-08-13T00:00:00.0000000+00:00");
        await command.ExecuteNonQueryAsync();
    }

    private static string GetRepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

    private static async Task SeedVersionTableAsync(string path, string rowsSql)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE schema_version(version INTEGER NOT NULL); {rowsSql}";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FailingMigration : IDatabaseMigration
    {
        public int FromVersion => 0;

        public int ToVersion => 1;

        public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE partial_table(id INTEGER);";
            await command.ExecuteNonQueryAsync(cancellationToken);
            throw new InvalidOperationException("fixture migration failure");
        }
    }

    private sealed class RecordingMigration(int fromVersion, int toVersion, string sql) : IDatabaseMigration
    {
        public int FromVersion { get; } = fromVersion;

        public int ToVersion { get; } = toVersion;

        public int ApplyCount { get; private set; }

        public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            ApplyCount++;
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed class CancellingMigration(CancellationTokenSource cancellation) : IDatabaseMigration
    {
        public int FromVersion => 0;

        public int ToVersion => 1;

        public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE cancelled_table(id INTEGER);";
            await command.ExecuteNonQueryAsync(cancellationToken);
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"));

        public TempDatabase()
        {
            System.IO.Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "winard.db");
        }

        public string Path { get; }

        public void Dispose() => System.IO.Directory.Delete(_directory, recursive: true);
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private DatabaseFixture(string directory, WinArdDatabase database)
        {
            Directory = directory;
            Database = database;
        }

        public string Directory { get; }

        public WinArdDatabase Database { get; }

        public string DatabasePath => Path.Combine(Directory, "winard.db");

        public static async Task<DatabaseFixture> CreateAtVersionTwoAsync()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"));
            var path = System.IO.Path.Combine(directory, "winard.db");
            var database = new WinArdDatabase(path);
            await database.InitializeAsync(
                [
                    new Migration001Initial(),
                    new RecordingMigration(
                        1,
                        2,
                        "ALTER TABLE devices ADD COLUMN refresh_mode INTEGER NOT NULL DEFAULT 0 CHECK(refresh_mode IN (0, 1, 2)); ALTER TABLE devices ADD COLUMN refresh_fps INTEGER NULL CHECK(refresh_fps IS NULL OR refresh_fps IN (30, 45, 60, 75, 90, 105, 120));"),
                ],
                CancellationToken.None);
            await using (var connection = database.CreateConnection())
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO devices (
                        id, display_name, host, port, mac_username, transport_mode,
                        credential_store, credential_key, refresh_mode, refresh_fps, created_utc, updated_utc)
                    VALUES (
                        $automatic_id, 'Automatic Mac', 'automatic.local', 5900, 'alex', 0,
                        NULL, NULL, 0, NULL, $now, $now),
                    (
                        $fixed_id, 'Fixed Mac', 'fixed.local', 5900, 'alex', 0,
                        NULL, NULL, 1, 60, $now, $now),
                    (
                        $unlimited_id, 'Unlimited Mac', 'unlimited.local', 5900, 'alex', 0,
                        NULL, NULL, 2, NULL, $now, $now),
                    (
                        $ssh_id, 'SSH Mac', 'ssh-mac.local', 5901, 'casey', 1,
                        'windows', 'mac-password', 1, 60, $now, $now);

                    INSERT INTO ssh_profiles (
                        device_id, ssh_host, ssh_port, ssh_username, private_key_path, target_host, target_port,
                        password_credential_store, password_credential_key,
                        passphrase_credential_store, passphrase_credential_key,
                        pinned_host_key_algorithm, pinned_host_key_sha256,
                        host_key_endpoint_host, host_key_endpoint_port, host_key_algorithm,
                        host_key_public_key_base64, host_key_fingerprint)
                    VALUES (
                        $ssh_id, 'jump.local', 2222, 'jump-user', 'C:\keys\id_ed25519', 'mac.internal', 5901,
                        'windows', 'ssh-password', 'vault', 'key-passphrase',
                        'ssh-ed25519', 'SHA256:legacy', 'jump.local', 2222, 'ssh-ed25519',
                        'AAAALegacyKey', 'SHA256:legacy');
                    """;
                command.Parameters.AddWithValue("$automatic_id", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$fixed_id", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$unlimited_id", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$ssh_id", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$now", "2026-08-02T00:00:00.0000000+00:00");
                await command.ExecuteNonQueryAsync();
            }

            return new DatabaseFixture(directory, database);
        }

        public static async Task<DatabaseFixture> CreateAtVersionThreeAsync(bool includeSshProfile = false)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"));
            var path = System.IO.Path.Combine(directory, "winard.db");
            var database = new WinArdDatabase(path);
            await database.InitializeAsync(
                [
                    new Migration001Initial(),
                    new RecordingMigration(
                        1,
                        2,
                        "ALTER TABLE devices ADD COLUMN refresh_mode INTEGER NOT NULL DEFAULT 0 CHECK(refresh_mode IN (0, 1, 2)); ALTER TABLE devices ADD COLUMN refresh_fps INTEGER NULL CHECK(refresh_fps IS NULL OR refresh_fps IN (30, 45, 60, 75, 90, 105, 120));"),
                    new RecordingMigration(
                        2,
                        3,
                        "ALTER TABLE devices ADD COLUMN quality_preset INTEGER NOT NULL DEFAULT 4 CHECK(quality_preset IN (0, 1, 2, 3, 4)); ALTER TABLE devices ADD COLUMN quality_bandwidth_bps INTEGER NULL CHECK(quality_bandwidth_bps IS NULL OR quality_bandwidth_bps BETWEEN 1 AND 1099511627776); ALTER TABLE devices ADD COLUMN quality_color INTEGER NOT NULL DEFAULT 0 CHECK(quality_color IN (0, 1, 2, 3)); ALTER TABLE devices ADD COLUMN quality_scale INTEGER NOT NULL DEFAULT 0 CHECK(quality_scale IN (0, 1, 2, 3)); ALTER TABLE devices ADD COLUMN quality_allow_gray INTEGER NOT NULL DEFAULT 1 CHECK(quality_allow_gray IN (0, 1)); ALTER TABLE devices ADD COLUMN quality_bandwidth_locked INTEGER NOT NULL DEFAULT 0 CHECK(quality_bandwidth_locked IN (0, 1)); ALTER TABLE devices ADD COLUMN quality_color_locked INTEGER NOT NULL DEFAULT 0 CHECK(quality_color_locked IN (0, 1)); ALTER TABLE devices ADD COLUMN quality_scale_locked INTEGER NOT NULL DEFAULT 0 CHECK(quality_scale_locked IN (0, 1)); ALTER TABLE devices ADD COLUMN quality_refresh_locked INTEGER NOT NULL DEFAULT 0 CHECK(quality_refresh_locked IN (0, 1));"),
                ],
                CancellationToken.None);
            await using var connection = database.CreateConnection();
            await connection.OpenAsync();
            for (var scale = 0; scale <= 3; scale++)
            {
                var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO devices (
                        id, display_name, host, port, mac_username, transport_mode,
                        quality_scale, created_utc, updated_utc)
                    VALUES ($id, $name, $host, 5900, 'alex', 0, $scale, $now, $now);
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$name", $"Scale {scale}");
                command.Parameters.AddWithValue("$host", $"scale-{scale}.local");
                command.Parameters.AddWithValue("$scale", scale);
                command.Parameters.AddWithValue("$now", "2026-08-13T00:00:00.0000000+00:00");
                await command.ExecuteNonQueryAsync();
            }

            if (includeSshProfile)
            {
                var device = connection.CreateCommand();
                device.CommandText = "SELECT id FROM devices WHERE quality_scale = 3;";
                var deviceId = (string)(await device.ExecuteScalarAsync() ?? throw new InvalidOperationException());
                var ssh = connection.CreateCommand();
                ssh.CommandText = """
                    INSERT INTO ssh_profiles(device_id, ssh_host, ssh_port, ssh_username, target_host, target_port)
                    VALUES($device_id, 'jump.local', 22, 'alex', 'target.local', 5900);
                    """;
                ssh.Parameters.AddWithValue("$device_id", deviceId);
                await ssh.ExecuteNonQueryAsync();
            }

            return new DatabaseFixture(directory, database);
        }

        public async Task InsertDeviceWithQualityScaleAsync(int scale)
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO devices (
                    id, display_name, host, port, mac_username, transport_mode,
                    quality_scale, created_utc, updated_utc)
                VALUES ($id, 'Percent 25', 'scale-25.local', 5900, 'alex', 0, $scale, $now, $now);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$scale", scale);
            command.Parameters.AddWithValue("$now", "2026-08-13T00:00:00.0000000+00:00");
            await command.ExecuteNonQueryAsync();
        }

        public async Task InsertOrphanSshProfileAsync()
        {
            var builder = new SqliteConnectionStringBuilder(Database.ConnectionString);
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync();
            var disableForeignKeys = connection.CreateCommand();
            disableForeignKeys.CommandText = "PRAGMA foreign_keys=OFF;";
            await disableForeignKeys.ExecuteNonQueryAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ssh_profiles(device_id, ssh_host, ssh_port, ssh_username, target_host, target_port)
                VALUES('orphan', 'jump.local', 22, 'alex', 'target.local', 5900);
                """;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long[]> ReadQualityScalesAsync()
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT quality_scale FROM devices ORDER BY quality_scale;";
            await using var reader = await command.ExecuteReaderAsync();
            var values = new List<long>();
            while (await reader.ReadAsync())
            {
                values.Add(reader.GetInt64(0));
            }

            return values.ToArray();
        }

        public async Task<int> ReadSchemaVersionAsync()
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM schema_version;";
            return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
