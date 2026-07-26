using Microsoft.Data.Sqlite;

namespace WinARD.Infrastructure.Database;

public interface IDatabaseMigration
{
    int FromVersion { get; }

    int ToVersion { get; }

    Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken);
}
