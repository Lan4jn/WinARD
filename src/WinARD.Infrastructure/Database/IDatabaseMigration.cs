using Microsoft.Data.Sqlite;

namespace WinARD.Infrastructure.Database;

public interface IDatabaseMigration
{
    int Version { get; }

    Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken);
}
