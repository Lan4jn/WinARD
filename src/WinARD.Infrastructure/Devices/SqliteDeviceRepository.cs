using System.Data;
using Microsoft.Data.Sqlite;
using WinARD.Application.Ports;
using WinARD.Domain.Connections;
using WinARD.Domain.Security;
using WinARD.Infrastructure.Database;

namespace WinARD.Infrastructure.Devices;

public sealed class SqliteDeviceRepository : IDeviceRepository
{
    private readonly WinArdDatabase _database;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public SqliteDeviceRepository(WinArdDatabase database, TimeProvider? timeProvider = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(profile);
        var canonicalHost = HostCanonicalizer.Canonicalize(profile.Host);
        var now = _timeProvider.GetUtcNow().ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            await RejectRetiredCredentialReferencesAsync(
                connection, transaction, profile, cancellationToken).ConfigureAwait(false);
            await SaveDeviceAsync(connection, transaction, profile, canonicalHost, now, cancellationToken).ConfigureAwait(false);
            await DeleteSshAsync(connection, transaction, profile.Id, cancellationToken).ConfigureAwait(false);
            if (profile.SshProfile is not null)
            {
                await SaveSshAsync(connection, transaction, profile.Id, profile.SshProfile, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new DeviceEndpointConflictException(canonicalHost, profile.Port, exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = CreateSelectCommand(connection);
        command.CommandText += " WHERE d.id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProfile(reader) : null;
    }

    public async Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = CreateSelectCommand(connection);
        command.CommandText += " ORDER BY d.display_name COLLATE NOCASE, d.id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var profiles = new List<ConnectionProfile>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM devices WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private static async Task SaveDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConnectionProfile profile,
        string canonicalHost,
        string now,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO devices (
                id, display_name, host, port, mac_username, transport_mode,
                credential_store, credential_key, refresh_mode, refresh_fps,
                quality_preset, quality_bandwidth_bps, quality_color, quality_scale, quality_allow_gray,
                quality_bandwidth_locked, quality_color_locked, quality_scale_locked, quality_refresh_locked,
                created_utc, updated_utc)
            VALUES (
                $id, $display_name, $host, $port, $mac_username, $transport_mode,
                $credential_store, $credential_key, $refresh_mode, $refresh_fps,
                $quality_preset, $quality_bandwidth_bps, $quality_color, $quality_scale, $quality_allow_gray,
                $quality_bandwidth_locked, $quality_color_locked, $quality_scale_locked, $quality_refresh_locked,
                $now, $now)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                host = excluded.host,
                port = excluded.port,
                mac_username = excluded.mac_username,
                transport_mode = excluded.transport_mode,
                credential_store = excluded.credential_store,
                credential_key = excluded.credential_key,
                refresh_mode = excluded.refresh_mode,
                refresh_fps = excluded.refresh_fps,
                quality_preset = excluded.quality_preset,
                quality_bandwidth_bps = excluded.quality_bandwidth_bps,
                quality_color = excluded.quality_color,
                quality_scale = excluded.quality_scale,
                quality_allow_gray = excluded.quality_allow_gray,
                quality_bandwidth_locked = excluded.quality_bandwidth_locked,
                quality_color_locked = excluded.quality_color_locked,
                quality_scale_locked = excluded.quality_scale_locked,
                quality_refresh_locked = excluded.quality_refresh_locked,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$display_name", profile.DisplayName);
        command.Parameters.AddWithValue("$host", canonicalHost);
        command.Parameters.AddWithValue("$port", profile.Port);
        command.Parameters.AddWithValue("$mac_username", profile.MacUsername);
        command.Parameters.AddWithValue("$transport_mode", (int)profile.TransportMode);
        AddCredentialParameters(command, "$credential_store", "$credential_key", profile.CredentialReference);
        command.Parameters.AddWithValue("$refresh_mode", (int)profile.FrameRefreshPolicy.Mode);
        command.Parameters.AddWithValue(
            "$refresh_fps",
            profile.FrameRefreshPolicy.FixedFramesPerSecond is { } framesPerSecond ? framesPerSecond : DBNull.Value);
        command.Parameters.AddWithValue("$quality_preset", (int)profile.Quality.Preset);
        command.Parameters.AddWithValue("$quality_bandwidth_bps", profile.Quality.TargetBytesPerSecond is { } bandwidth ? bandwidth : DBNull.Value);
        command.Parameters.AddWithValue("$quality_color", (int)profile.Quality.Color);
        command.Parameters.AddWithValue("$quality_scale", (int)profile.Quality.Scale);
        command.Parameters.AddWithValue("$quality_allow_gray", profile.Quality.AllowAutomaticGrayscale ? 1 : 0);
        command.Parameters.AddWithValue("$quality_bandwidth_locked", profile.Quality.BandwidthLocked ? 1 : 0);
        command.Parameters.AddWithValue("$quality_color_locked", profile.Quality.ColorLocked ? 1 : 0);
        command.Parameters.AddWithValue("$quality_scale_locked", profile.Quality.ScaleLocked ? 1 : 0);
        command.Parameters.AddWithValue("$quality_refresh_locked", profile.Quality.RefreshLocked ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteSshAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ssh_profiles WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveSshAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        SshProfile ssh,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ssh_profiles (
                device_id, ssh_host, ssh_port, ssh_username, private_key_path, target_host, target_port,
                password_credential_store, password_credential_key,
                passphrase_credential_store, passphrase_credential_key,
                pinned_host_key_algorithm, pinned_host_key_sha256,
                host_key_endpoint_host, host_key_endpoint_port, host_key_algorithm,
                host_key_public_key_base64, host_key_fingerprint)
            VALUES (
                $device_id, $ssh_host, $ssh_port, $ssh_username, $private_key_path, $target_host, $target_port,
                $password_store, $password_key, $passphrase_store, $passphrase_key,
                $pinned_algorithm, $pinned_sha256, $pin_host, $pin_port, $pin_algorithm,
                $pin_public_key, $pin_fingerprint);
            """;
        command.Parameters.AddWithValue("$device_id", id.ToString("D"));
        command.Parameters.AddWithValue("$ssh_host", HostCanonicalizer.Canonicalize(ssh.Host));
        command.Parameters.AddWithValue("$ssh_port", ssh.Port);
        command.Parameters.AddWithValue("$ssh_username", ssh.Username);
        command.Parameters.AddWithValue("$private_key_path", DbValue(ssh.PrivateKeyPath));
        command.Parameters.AddWithValue("$target_host", HostCanonicalizer.Canonicalize(ssh.TargetHost));
        command.Parameters.AddWithValue("$target_port", ssh.TargetPort);
        AddCredentialParameters(command, "$password_store", "$password_key", ssh.PasswordCredentialReference);
        AddCredentialParameters(command, "$passphrase_store", "$passphrase_key", ssh.PrivateKeyPassphraseCredentialReference);
        command.Parameters.AddWithValue("$pinned_algorithm", DbValue(ssh.PinnedHostKeyAlgorithm));
        command.Parameters.AddWithValue("$pinned_sha256", DbValue(ssh.PinnedHostKeySha256));
        command.Parameters.AddWithValue("$pin_host", DbValue(ssh.HostKeyPin?.Endpoint.Host));
        command.Parameters.AddWithValue("$pin_port", ssh.HostKeyPin is null ? DBNull.Value : ssh.HostKeyPin.Endpoint.Port);
        command.Parameters.AddWithValue("$pin_algorithm", DbValue(ssh.HostKeyPin?.Algorithm));
        command.Parameters.AddWithValue("$pin_public_key", DbValue(ssh.HostKeyPin?.PublicKeyBase64));
        command.Parameters.AddWithValue("$pin_fingerprint", DbValue(ssh.HostKeyPin?.Fingerprint));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand CreateSelectCommand(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                d.id, d.display_name, d.host, d.port, d.mac_username, d.transport_mode,
                d.credential_store, d.credential_key, d.refresh_mode, d.refresh_fps,
                d.quality_preset, d.quality_bandwidth_bps, d.quality_color, d.quality_scale, d.quality_allow_gray,
                d.quality_bandwidth_locked, d.quality_color_locked, d.quality_scale_locked, d.quality_refresh_locked,
                s.ssh_host, s.ssh_port, s.ssh_username, s.private_key_path, s.target_host, s.target_port,
                s.password_credential_store, s.password_credential_key,
                s.passphrase_credential_store, s.passphrase_credential_key,
                s.pinned_host_key_algorithm, s.pinned_host_key_sha256,
                s.host_key_endpoint_host, s.host_key_endpoint_port, s.host_key_algorithm,
                s.host_key_public_key_base64, s.host_key_fingerprint
            FROM devices d
            LEFT JOIN ssh_profiles s ON s.device_id = d.id
            """;
        return command;
    }

    private static ConnectionProfile ReadProfile(SqliteDataReader reader)
    {
        var transportModeValue = reader.GetInt32(Columns.TransportMode);
        var transportMode = transportModeValue switch
        {
            (int)TransportMode.Direct => TransportMode.Direct,
            (int)TransportMode.Ssh => TransportMode.Ssh,
            _ => throw new InvalidDataException($"Unsupported persisted transport mode {transportModeValue}."),
        };
        var hasSshProfile = !reader.IsDBNull(Columns.SshHost);
        if ((transportMode == TransportMode.Ssh) != hasSshProfile)
        {
            throw new InvalidDataException("Persisted transport mode does not match the SSH profile row.");
        }

        var profile = ConnectionProfile.Create(
            Guid.Parse(reader.GetString(Columns.Id)),
            reader.GetString(Columns.DisplayName),
            reader.GetString(Columns.Host),
            reader.GetInt32(Columns.Port),
            reader.GetString(Columns.MacUsername))
            .WithQualityProfile(ReadQualityProfile(reader));
        var macCredential = ReadCredential(reader, Columns.CredentialStore, Columns.CredentialKey);
        if (macCredential is not null)
        {
            profile = profile.WithCredential(macCredential);
        }

        if (hasSshProfile)
        {
            var ssh = SshProfile.Create(
                    reader.GetString(Columns.SshHost),
                    reader.GetInt32(Columns.SshPort),
                    reader.GetString(Columns.SshUsername),
                    ReadNullableString(reader, Columns.PrivateKeyPath),
                    reader.GetString(Columns.TargetHost),
                    reader.GetInt32(Columns.TargetPort),
                    null,
                    ReadNullableString(reader, Columns.PinnedHostKeyAlgorithm),
                    ReadNullableString(reader, Columns.PinnedHostKeySha256))
                .WithAuthenticationCredentials(
                    ReadCredential(reader, Columns.PasswordCredentialStore, Columns.PasswordCredentialKey),
                    ReadCredential(reader, Columns.PassphraseCredentialStore, Columns.PassphraseCredentialKey));
            if (!reader.IsDBNull(Columns.HostKeyEndpointHost))
            {
                ssh = ssh.WithHostKeyPin(new SshHostKeyPin(
                    new SshHostKeyEndpoint(
                        reader.GetString(Columns.HostKeyEndpointHost),
                        reader.GetInt32(Columns.HostKeyEndpointPort)),
                    reader.GetString(Columns.HostKeyAlgorithm),
                    reader.GetString(Columns.HostKeyPublicKeyBase64),
                    reader.GetString(Columns.HostKeyFingerprint)));
            }

            profile = profile.WithSsh(ssh);
        }

        return profile;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = _database.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await WinArdDatabase.ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void AddCredentialParameters(
        SqliteCommand command,
        string storeName,
        string keyName,
        CredentialReference? credential)
    {
        command.Parameters.AddWithValue(storeName, DbValue(credential?.Store));
        command.Parameters.AddWithValue(keyName, DbValue(credential?.Key));
    }

    private static CredentialReference? ReadCredential(SqliteDataReader reader, int storeOrdinal, int keyOrdinal) =>
        reader.IsDBNull(storeOrdinal)
            ? null
            : CredentialReference.Create(reader.GetString(storeOrdinal), reader.GetString(keyOrdinal));

    private static FrameRefreshPolicy ReadRefreshPolicy(SqliteDataReader reader, int modeOrdinal, int fpsOrdinal)
    {
        var mode = (FrameRefreshMode)reader.GetInt32(modeOrdinal);
        if (mode == FrameRefreshMode.Fixed && !reader.IsDBNull(fpsOrdinal))
        {
            var framesPerSecond = reader.GetInt32(fpsOrdinal);
            try
            {
                return FrameRefreshPolicy.Fixed(framesPerSecond);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new InvalidDataException("Persisted frame refresh policy is invalid.", exception);
            }
        }

        return mode switch
        {
            FrameRefreshMode.Automatic when reader.IsDBNull(fpsOrdinal) => FrameRefreshPolicy.Automatic,
            FrameRefreshMode.Unlimited when reader.IsDBNull(fpsOrdinal) => FrameRefreshPolicy.Unlimited,
            _ => throw new InvalidDataException("Persisted frame refresh policy is invalid."),
        };
    }

    private static async Task RejectRetiredCredentialReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        CredentialReference?[] references =
        [
            profile.CredentialReference,
            profile.SshProfile?.PasswordCredentialReference,
            profile.SshProfile?.PrivateKeyPassphraseCredentialReference,
        ];
        foreach (var reference in references.OfType<CredentialReference>().Distinct())
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*)
                FROM retired_credential_references
                WHERE backend = $backend AND credential_key = $credential_key;
                """;
            command.Parameters.AddWithValue("$backend", reference.Store);
            command.Parameters.AddWithValue("$credential_key", reference.Key);
            var retired = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 0;
            if (retired)
            {
                throw new RetiredCredentialReferenceException(reference);
            }
        }
    }

    private static QualityProfile ReadQualityProfile(SqliteDataReader reader)
    {
        var refresh = ReadRefreshPolicy(reader, Columns.RefreshMode, Columns.RefreshFps);
        var preset = ReadEnum<QualityPreset>(reader, Columns.QualityPreset, "quality preset");
        var color = ReadEnum<QualityColor>(reader, Columns.QualityColor, "quality color");
        var scale = ReadEnum<QualityScale>(reader, Columns.QualityScale, "quality scale");
        var bandwidth = reader.IsDBNull(Columns.QualityBandwidth)
            ? (long?)null
            : reader.GetInt64(Columns.QualityBandwidth);
        var allowGray = ReadBoolean(reader, Columns.QualityAllowGray, "quality allow-gray");
        var bandwidthLocked = ReadBoolean(reader, Columns.QualityBandwidthLocked, "quality bandwidth lock");
        var colorLocked = ReadBoolean(reader, Columns.QualityColorLocked, "quality color lock");
        var scaleLocked = ReadBoolean(reader, Columns.QualityScaleLocked, "quality scale lock");
        var refreshLocked = ReadBoolean(reader, Columns.QualityRefreshLocked, "quality refresh lock");

        try
        {
            if (preset == QualityPreset.Custom)
            {
                return QualityProfile.CreateCustom(
                    bandwidth,
                    color,
                    scale,
                    refresh,
                    allowGray,
                    bandwidthLocked,
                    colorLocked,
                    scaleLocked,
                    refreshLocked);
            }

            var named = preset switch
            {
                QualityPreset.Automatic => QualityProfile.Automatic,
                QualityPreset.Original => QualityProfile.Original,
                QualityPreset.Balanced => QualityProfile.Balanced,
                QualityPreset.Smooth => QualityProfile.Smooth,
                _ => throw new InvalidDataException($"Unsupported persisted quality preset {(int)preset}."),
            };
            if (bandwidth != named.TargetBytesPerSecond ||
                color != named.Color ||
                scale != named.Scale ||
                refresh != named.Refresh ||
                allowGray != named.AllowAutomaticGrayscale ||
                bandwidthLocked != named.BandwidthLocked ||
                colorLocked != named.ColorLocked ||
                scaleLocked != named.ScaleLocked ||
                refreshLocked != named.RefreshLocked)
            {
                throw new InvalidDataException("Persisted named quality preset has inconsistent fields.");
            }

            return named;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Persisted quality profile is invalid.", exception);
        }
    }

    private static TEnum ReadEnum<TEnum>(SqliteDataReader reader, int ordinal, string fieldName)
        where TEnum : struct, Enum
    {
        var value = reader.GetInt32(ordinal);
        if (!Enum.IsDefined(typeof(TEnum), value))
        {
            throw new InvalidDataException($"Unsupported persisted {fieldName} {value}.");
        }

        return (TEnum)Enum.ToObject(typeof(TEnum), value);
    }

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal, string fieldName) =>
        reader.GetInt32(ordinal) switch
        {
            0 => false,
            1 => true,
            var value => throw new InvalidDataException($"Unsupported persisted {fieldName} value {value}."),
        };

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;

    private static class Columns
    {
        public const int Id = 0;
        public const int DisplayName = 1;
        public const int Host = 2;
        public const int Port = 3;
        public const int MacUsername = 4;
        public const int TransportMode = 5;
        public const int CredentialStore = 6;
        public const int CredentialKey = 7;
        public const int RefreshMode = 8;
        public const int RefreshFps = 9;
        public const int QualityPreset = 10;
        public const int QualityBandwidth = 11;
        public const int QualityColor = 12;
        public const int QualityScale = 13;
        public const int QualityAllowGray = 14;
        public const int QualityBandwidthLocked = 15;
        public const int QualityColorLocked = 16;
        public const int QualityScaleLocked = 17;
        public const int QualityRefreshLocked = 18;
        public const int SshHost = 19;
        public const int SshPort = 20;
        public const int SshUsername = 21;
        public const int PrivateKeyPath = 22;
        public const int TargetHost = 23;
        public const int TargetPort = 24;
        public const int PasswordCredentialStore = 25;
        public const int PasswordCredentialKey = 26;
        public const int PassphraseCredentialStore = 27;
        public const int PassphraseCredentialKey = 28;
        public const int PinnedHostKeyAlgorithm = 29;
        public const int PinnedHostKeySha256 = 30;
        public const int HostKeyEndpointHost = 31;
        public const int HostKeyEndpointPort = 32;
        public const int HostKeyAlgorithm = 33;
        public const int HostKeyPublicKeyBase64 = 34;
        public const int HostKeyFingerprint = 35;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
