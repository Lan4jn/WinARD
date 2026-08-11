using Microsoft.Data.Sqlite;

namespace WinARD.Infrastructure.Database.Migrations;

internal sealed class Migration003QualityProfile : IDatabaseMigration
{
    public int FromVersion => 2;

    public int ToVersion => 3;

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE devices ADD COLUMN quality_preset INTEGER NOT NULL DEFAULT 4
                CHECK(quality_preset IN (0, 1, 2, 3, 4));
            ALTER TABLE devices ADD COLUMN quality_bandwidth_bps INTEGER NULL
                CHECK(quality_bandwidth_bps IS NULL OR quality_bandwidth_bps BETWEEN 1 AND 1099511627776);
            ALTER TABLE devices ADD COLUMN quality_color INTEGER NOT NULL DEFAULT 0
                CHECK(quality_color IN (0, 1, 2, 3));
            ALTER TABLE devices ADD COLUMN quality_scale INTEGER NOT NULL DEFAULT 0
                CHECK(quality_scale IN (0, 1, 2, 3));
            ALTER TABLE devices ADD COLUMN quality_allow_gray INTEGER NOT NULL DEFAULT 1
                CHECK(quality_allow_gray IN (0, 1));
            ALTER TABLE devices ADD COLUMN quality_bandwidth_locked INTEGER NOT NULL DEFAULT 0
                CHECK(quality_bandwidth_locked IN (0, 1));
            ALTER TABLE devices ADD COLUMN quality_color_locked INTEGER NOT NULL DEFAULT 0
                CHECK(quality_color_locked IN (0, 1));
            ALTER TABLE devices ADD COLUMN quality_scale_locked INTEGER NOT NULL DEFAULT 0
                CHECK(quality_scale_locked IN (0, 1));
            ALTER TABLE devices ADD COLUMN quality_refresh_locked INTEGER NOT NULL DEFAULT 0
                CHECK(quality_refresh_locked IN (0, 1));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
