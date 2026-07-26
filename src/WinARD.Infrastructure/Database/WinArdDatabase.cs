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
        InitializeAsync([new Migration001Initial()], cancellationToken);

    public async Task InitializeAsync(
        IReadOnlyCollection<IDatabaseMigration> migrations,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(migrations);
        ValidateMigrations(migrations);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        try
        {
            var currentVersion = await ReadVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var supportedVersion = migrations.Count == 0 ? 0 : migrations.Max(static migration => migration.Version);
            if (currentVersion > supportedVersion)
            {
                throw new UnsupportedSchemaVersionException(currentVersion, supportedVersion);
            }

            foreach (var migration in migrations.OrderBy(static migration => migration.Version))
            {
                if (migration.Version > currentVersion)
                {
                    await migration.ApplyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    currentVersion = migration.Version;
                }
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

    private static void ValidateMigrations(IReadOnlyCollection<IDatabaseMigration> migrations)
    {
        if (migrations.Any(static migration => migration.Version <= 0) ||
            migrations.Select(static migration => migration.Version).Distinct().Count() != migrations.Count)
        {
            throw new ArgumentException("Migration versions must be positive and unique.", nameof(migrations));
        }
    }

    private static async Task<int> ReadVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var tableCommand = connection.CreateCommand();
        tableCommand.Transaction = transaction;
        tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version';";
        var exists = Convert.ToInt64(
            await tableCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
        if (!exists)
        {
            return 0;
        }

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

        return reader.GetInt32(1);
    }
}
