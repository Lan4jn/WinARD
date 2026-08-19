using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using WinARD.Application.Ports;
using WinARD.Domain.Settings;
using WinARD.Infrastructure.Database;

namespace WinARD.Infrastructure.Settings;

public sealed class SqliteAppSettingsRepository(WinArdDatabase database) : IAppSettingsRepository
{
    private readonly WinArdDatabase _database = database ??
        throw new ArgumentNullException(nameof(database));
    private int _disposed;

    public async Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var values = await ReadAsync(connection, transaction: null, cancellationToken)
            .ConfigureAwait(false);
        return Parse(values);
    }

    public async Task<bool> TryUpdateAsync(
        long expectedRevision,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        try
        {
            var current = Parse(await ReadAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false));
            if (current.Revision != expectedRevision)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            var values = Serialize(settings, checked(expectedRevision + 1));
            foreach (var (key, value) in values)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE app_settings SET setting_value = $value WHERE setting_key = $key;";
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", value);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidDataException("App settings schema is incomplete.");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the primary settings failure; rollback is best effort.
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = _database.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await WinArdDatabase.ConfigureConnectionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Dictionary<string, string>> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT setting_key, setting_value FROM app_settings;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0), reader.GetString(1));
        }
        return values;
    }

    private static AppSettingsSnapshot Parse(IReadOnlyDictionary<string, string> values)
    {
        try
        {
            return new AppSettingsSnapshot(
                AppSettings.Create(
                    ParseInt(values, "settings_version"),
                    ParseEnum<AppTheme>(values, "theme"),
                    ParseEnum<SafeDiagnosticLevel>(values, "diagnostic_level"),
                    ParseBool(values, "clipboard_default"),
                    ParseEnum<CredentialBackend>(values, "credential_backend"),
                    TimeSpan.FromMinutes(ParseInt(values, "vault_idle_minutes"))),
                ParseLong(values, "revision"));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or
            FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("Persisted app settings are invalid.", exception);
        }
    }

    private static Dictionary<string, string> Serialize(AppSettings settings, long revision) =>
        new(StringComparer.Ordinal)
        {
            ["settings_version"] = settings.Version.ToString(CultureInfo.InvariantCulture),
            ["revision"] = revision.ToString(CultureInfo.InvariantCulture),
            ["theme"] = ((int)settings.Theme).ToString(CultureInfo.InvariantCulture),
            ["diagnostic_level"] = ((int)settings.DiagnosticLevel).ToString(CultureInfo.InvariantCulture),
            ["clipboard_default"] = settings.ClipboardEnabledByDefault ? "1" : "0",
            ["credential_backend"] = ((int)settings.DefaultCredentialBackend).ToString(CultureInfo.InvariantCulture),
            ["vault_idle_minutes"] = checked((int)settings.VaultIdleTimeout.TotalMinutes)
                .ToString(CultureInfo.InvariantCulture),
        };

    private static int ParseInt(IReadOnlyDictionary<string, string> values, string key) =>
        int.Parse(values[key], NumberStyles.None, CultureInfo.InvariantCulture);

    private static long ParseLong(IReadOnlyDictionary<string, string> values, string key) =>
        long.Parse(values[key], NumberStyles.None, CultureInfo.InvariantCulture);

    private static T ParseEnum<T>(IReadOnlyDictionary<string, string> values, string key)
        where T : struct, Enum => (T)Enum.ToObject(typeof(T), ParseInt(values, key));

    private static bool ParseBool(IReadOnlyDictionary<string, string> values, string key) =>
        values[key] switch
        {
            "0" => false,
            "1" => true,
            _ => throw new FormatException(),
        };

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
