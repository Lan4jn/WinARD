using Microsoft.Data.Sqlite;

namespace WinARD.Infrastructure.Database.Migrations;

internal sealed class Migration002FrameRefreshPolicy : IDatabaseMigration
{
    public int FromVersion => 1;

    public int ToVersion => 2;

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE devices ADD COLUMN refresh_mode INTEGER NOT NULL DEFAULT 0
                CHECK(refresh_mode IN (0, 1, 2));
            ALTER TABLE devices ADD COLUMN refresh_fps INTEGER NULL
                CHECK(refresh_fps IS NULL OR refresh_fps IN (30, 45, 60, 75, 90, 105, 120));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
