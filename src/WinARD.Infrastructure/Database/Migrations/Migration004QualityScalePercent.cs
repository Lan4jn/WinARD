using Microsoft.Data.Sqlite;

namespace WinARD.Infrastructure.Database.Migrations;

public sealed class Migration004QualityScalePercent : IDatabaseMigration
{
    private const string PreviousConstraint = "CHECK(quality_scale IN (0, 1, 2, 3))";
    private const string CurrentConstraint = "CHECK(quality_scale IN (0, 1, 2, 3, 4))";

    public int FromVersion => 3;

    public int ToVersion => 4;

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var schemaVersionCommand = connection.CreateCommand();
        schemaVersionCommand.Transaction = transaction;
        schemaVersionCommand.CommandText = "PRAGMA schema_version;";
        var schemaVersion = Convert.ToInt32(
            await schemaVersionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            PRAGMA writable_schema=ON;
            UPDATE sqlite_schema
            SET sql = replace(sql, '{PreviousConstraint}', '{CurrentConstraint}')
            WHERE type = 'table'
              AND name = 'devices'
              AND instr(sql, '{PreviousConstraint}') > 0;
            PRAGMA writable_schema=OFF;
            PRAGMA schema_version={schemaVersion + 1};
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var verificationCommand = connection.CreateCommand();
        verificationCommand.Transaction = transaction;
        verificationCommand.CommandText = "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'devices';";
        var devicesSchema = Convert.ToString(
            await verificationCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (devicesSchema is null ||
            !devicesSchema.Contains(CurrentConstraint, StringComparison.Ordinal) ||
            devicesSchema.Contains(PreviousConstraint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The devices quality_scale constraint was not updated.");
        }

        var integrityCommand = connection.CreateCommand();
        integrityCommand.Transaction = transaction;
        integrityCommand.CommandText = "PRAGMA integrity_check;";
        var integrity = Convert.ToString(
            await integrityCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SQLite integrity validation failed after quality scale migration.");
        }
    }
}
