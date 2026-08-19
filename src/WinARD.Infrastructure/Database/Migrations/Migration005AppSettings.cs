using Microsoft.Data.Sqlite;

namespace WinARD.Infrastructure.Database.Migrations;

public sealed class Migration005AppSettings : IDatabaseMigration
{
    public int FromVersion => 4;

    public int ToVersion => 5;

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
            INSERT OR IGNORE INTO app_settings(setting_key, setting_value) VALUES
                ('settings_version', '1'),
                ('revision', '0'),
                ('theme', '0'),
                ('diagnostic_level', '1'),
                ('clipboard_default', '1'),
                ('credential_backend', '1'),
                ('vault_idle_minutes', '15');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
