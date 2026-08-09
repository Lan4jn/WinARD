using Microsoft.Data.Sqlite;
using WinARD.Domain.Connections;
using WinARD.Infrastructure.Database;
using WinARD.Infrastructure.Database.Migrations;
using WinARD.Infrastructure.Devices;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Infrastructure.Tests;

public sealed class WinArdDatabaseTests
{
    [Fact]
    public async Task Existing_device_migrates_to_automatic_refresh()
    {
        await using var fixture = await DatabaseFixture.CreateAtVersionOneAsync();
        await fixture.Database.InitializeAsync(CancellationToken.None);
        await using var repository = new SqliteDeviceRepository(fixture.Database);

        var profile = Assert.Single(await repository.GetAllAsync(CancellationToken.None));

        Assert.Equal(FrameRefreshPolicy.Automatic, profile.FrameRefreshPolicy);
        Assert.Equal(2, await fixture.ReadSchemaVersionAsync());
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
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal(2L, reader.GetInt64(2));
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

        public static async Task<DatabaseFixture> CreateAtVersionOneAsync()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"));
            var path = System.IO.Path.Combine(directory, "winard.db");
            var database = new WinArdDatabase(path);
            await database.InitializeAsync([new Migration001Initial()], CancellationToken.None);
            await using (var connection = database.CreateConnection())
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO devices (
                        id, display_name, host, port, mac_username, transport_mode,
                        credential_store, credential_key, created_utc, updated_utc)
                    VALUES (
                        $id, 'Existing Mac', 'existing.local', 5900, 'alex', 0,
                        NULL, NULL, '2026-08-02T00:00:00.0000000+00:00', '2026-08-02T00:00:00.0000000+00:00');
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                await command.ExecuteNonQueryAsync();
            }

            return new DatabaseFixture(directory, database);
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
