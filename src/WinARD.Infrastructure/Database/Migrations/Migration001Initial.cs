using Microsoft.Data.Sqlite;

namespace WinARD.Infrastructure.Database.Migrations;

public sealed class Migration001Initial : IDatabaseMigration
{
    public int FromVersion => 0;

    public int ToVersion => 1;

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
            CREATE TABLE devices (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                host TEXT NOT NULL,
                port INTEGER NOT NULL CHECK(port BETWEEN 1 AND 65535),
                mac_username TEXT NOT NULL,
                transport_mode INTEGER NOT NULL CHECK(transport_mode IN (0, 1)),
                credential_store TEXT NULL,
                credential_key TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                CHECK((credential_store IS NULL) = (credential_key IS NULL))
            );
            CREATE UNIQUE INDEX ux_devices_host_port ON devices(host, port);

            CREATE TABLE ssh_profiles (
                device_id TEXT PRIMARY KEY REFERENCES devices(id) ON DELETE CASCADE,
                ssh_host TEXT NOT NULL,
                ssh_port INTEGER NOT NULL CHECK(ssh_port BETWEEN 1 AND 65535),
                ssh_username TEXT NOT NULL,
                private_key_path TEXT NULL,
                target_host TEXT NOT NULL,
                target_port INTEGER NOT NULL CHECK(target_port BETWEEN 1 AND 65535),
                password_credential_store TEXT NULL,
                password_credential_key TEXT NULL,
                passphrase_credential_store TEXT NULL,
                passphrase_credential_key TEXT NULL,
                pinned_host_key_algorithm TEXT NULL,
                pinned_host_key_sha256 TEXT NULL,
                host_key_endpoint_host TEXT NULL,
                host_key_endpoint_port INTEGER NULL,
                host_key_algorithm TEXT NULL,
                host_key_public_key_base64 TEXT NULL,
                host_key_fingerprint TEXT NULL,
                CHECK((password_credential_store IS NULL) = (password_credential_key IS NULL)),
                CHECK((passphrase_credential_store IS NULL) = (passphrase_credential_key IS NULL))
            );

            CREATE TABLE app_settings (
                setting_key TEXT PRIMARY KEY,
                setting_value TEXT NOT NULL
            );

            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
