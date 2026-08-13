using Microsoft.Data.Sqlite;

namespace WinARD.Infrastructure.Database.Migrations;

public sealed class Migration004QualityScalePercent : IDatabaseMigration
{
    public int FromVersion => 3;

    public int ToVersion => 4;

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ssh_profiles_v4 AS SELECT * FROM ssh_profiles;
            DROP TABLE ssh_profiles;

            CREATE TABLE devices_v4 (
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
                refresh_mode INTEGER NOT NULL DEFAULT 0 CHECK(refresh_mode IN (0, 1, 2)),
                refresh_fps INTEGER NULL CHECK(refresh_fps IS NULL OR refresh_fps IN (30, 45, 60, 75, 90, 105, 120)),
                quality_preset INTEGER NOT NULL DEFAULT 4 CHECK(quality_preset IN (0, 1, 2, 3, 4)),
                quality_bandwidth_bps INTEGER NULL CHECK(quality_bandwidth_bps IS NULL OR quality_bandwidth_bps BETWEEN 1 AND 1099511627776),
                quality_color INTEGER NOT NULL DEFAULT 0 CHECK(quality_color IN (0, 1, 2, 3)),
                quality_scale INTEGER NOT NULL DEFAULT 0 CHECK(quality_scale IN (0, 1, 2, 3, 4)),
                quality_allow_gray INTEGER NOT NULL DEFAULT 1 CHECK(quality_allow_gray IN (0, 1)),
                quality_bandwidth_locked INTEGER NOT NULL DEFAULT 0 CHECK(quality_bandwidth_locked IN (0, 1)),
                quality_color_locked INTEGER NOT NULL DEFAULT 0 CHECK(quality_color_locked IN (0, 1)),
                quality_scale_locked INTEGER NOT NULL DEFAULT 0 CHECK(quality_scale_locked IN (0, 1)),
                quality_refresh_locked INTEGER NOT NULL DEFAULT 0 CHECK(quality_refresh_locked IN (0, 1)),
                CHECK((credential_store IS NULL) = (credential_key IS NULL))
            );

            INSERT INTO devices_v4 (
                id, display_name, host, port, mac_username, transport_mode,
                credential_store, credential_key, created_utc, updated_utc,
                refresh_mode, refresh_fps, quality_preset, quality_bandwidth_bps,
                quality_color, quality_scale, quality_allow_gray,
                quality_bandwidth_locked, quality_color_locked, quality_scale_locked,
                quality_refresh_locked)
            SELECT
                id, display_name, host, port, mac_username, transport_mode,
                credential_store, credential_key, created_utc, updated_utc,
                refresh_mode, refresh_fps, quality_preset, quality_bandwidth_bps,
                quality_color, quality_scale, quality_allow_gray,
                quality_bandwidth_locked, quality_color_locked, quality_scale_locked,
                quality_refresh_locked
            FROM devices;

            DROP TABLE devices;
            ALTER TABLE devices_v4 RENAME TO devices;
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

            INSERT INTO ssh_profiles SELECT * FROM ssh_profiles_v4;
            DROP TABLE ssh_profiles_v4;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
