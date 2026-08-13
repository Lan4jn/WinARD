using System.Data;
using Microsoft.Data.Sqlite;
using WinARD.Infrastructure.Database.Migrations;

namespace WinARD.Infrastructure.Database;

public sealed class WinArdDatabase : IAsyncDisposable
{
    private readonly string _databasePath;
    private int _disposed;

    public WinArdDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path cannot be blank.", nameof(databasePath));
        }

        _databasePath = Path.GetFullPath(databasePath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 5,
        };
        ConnectionString = builder.ToString();
    }

    public string ConnectionString { get; }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        InitializeAsync(
            [
                new Migration001Initial(),
                new Migration002FrameRefreshPolicy(),
                new Migration003QualityProfile(),
                new Migration004QualityScalePercent(),
            ],
            cancellationToken);

    public async Task InitializeAsync(
        IReadOnlyCollection<IDatabaseMigration> migrations,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(migrations);
        var migrationChain = migrations.ToArray();
        ValidateMigrations(migrationChain);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        try
        {
            await EnsureVersionTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var currentVersion = await ReadVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var supportedVersion = migrationChain.Length == 0 ? 0 : migrationChain[^1].ToVersion;
            if (currentVersion > supportedVersion)
            {
                throw new UnsupportedSchemaVersionException(currentVersion, supportedVersion);
            }

            var pending = GetPendingMigrations(migrationChain, currentVersion, supportedVersion);
            foreach (var migration in pending)
            {
                await migration.ApplyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await AdvanceVersionAsync(connection, transaction, migration.ToVersion, cancellationToken).ConfigureAwait(false);
                currentVersion = migration.ToVersion;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public SqliteConnection CreateConnection()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new SqliteConnection(ConnectionString);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    internal static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateMigrations(IDatabaseMigration[] migrations)
    {
        for (var index = 0; index < migrations.Length; index++)
        {
            var migration = migrations[index] ?? throw new ArgumentException("Migrations cannot contain null entries.", nameof(migrations));
            if (migration.FromVersion < 0 || migration.ToVersion <= migration.FromVersion)
            {
                throw new ArgumentException("Migration transitions must advance from a non-negative version.", nameof(migrations));
            }

            if (index > 0 && migration.FromVersion != migrations[index - 1].ToVersion)
            {
                throw new ArgumentException("Migrations must be supplied as one continuous, ordered chain.", nameof(migrations));
            }
        }
    }

    private static IDatabaseMigration[] GetPendingMigrations(
        IDatabaseMigration[] migrations,
        int currentVersion,
        int supportedVersion)
    {
        if (currentVersion == supportedVersion)
        {
            return [];
        }

        var startIndex = -1;
        for (var index = 0; index < migrations.Length; index++)
        {
            if (migrations[index].FromVersion == currentVersion)
            {
                startIndex = index;
                break;
            }
        }

        if (startIndex < 0)
        {
            throw new InvalidOperationException($"No migration starts at schema version {currentVersion}.");
        }

        return migrations.Skip(startIndex).ToArray();
    }

    private static async Task EnsureVersionTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var existsCommand = connection.CreateCommand();
        existsCommand.Transaction = transaction;
        existsCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version';";
        var exists = Convert.ToInt64(
            await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
        if (exists)
        {
            return;
        }

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE schema_version(version INTEGER NOT NULL);
            INSERT INTO schema_version(version) VALUES (0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AdvanceVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE schema_version SET version = $to;";
        command.Parameters.AddWithValue("$to", version);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("schema_version must contain exactly one version row.");
        }

        var actualVersion = await ReadVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (actualVersion != version)
        {
            throw new InvalidOperationException($"Expected schema version {version}, but found {actualVersion}.");
        }
    }

    private static async Task<int> ReadVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "SELECT COUNT(*), MIN(version), MAX(version) FROM schema_version;";
        await using var reader = await versionCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt64(0) != 1 ||
            reader.GetInt64(1) != reader.GetInt64(2))
        {
            throw new InvalidOperationException("schema_version must contain exactly one version row.");
        }

        var version = reader.GetInt32(1);
        if (version < 0)
        {
            throw new InvalidOperationException("schema_version cannot be negative.");
        }

        return version;
    }
}
