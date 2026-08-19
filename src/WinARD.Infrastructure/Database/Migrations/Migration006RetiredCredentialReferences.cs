using Microsoft.Data.Sqlite;

namespace WinARD.Infrastructure.Database.Migrations;

public sealed class Migration006RetiredCredentialReferences : IDatabaseMigration
{
    public int FromVersion => 5;

    public int ToVersion => 6;

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
            CREATE TABLE retired_credential_references (
                backend TEXT NOT NULL COLLATE NOCASE,
                credential_key TEXT NOT NULL,
                retired_utc TEXT NOT NULL,
                PRIMARY KEY (backend, credential_key)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
