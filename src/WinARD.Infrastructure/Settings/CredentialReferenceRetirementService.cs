using System.Data;
using Microsoft.Data.Sqlite;
using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Infrastructure.Database;

namespace WinARD.Infrastructure.Settings;

public sealed class CredentialReferenceRetirementService(
    WinArdDatabase database,
    ICredentialStore store) : ICredentialReferenceRetirementService
{
    private readonly WinArdDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly ICredentialStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async ValueTask<IReadOnlyList<CredentialRetirementCandidate>> CaptureAsync(
        IEnumerable<CredentialReference> references,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        var candidates = new List<CredentialRetirementCandidate>();
        try
        {
            foreach (var reference in references.Distinct(CredentialReferenceComparer.Instance))
            {
                using var snapshot = await _store.ReadSnapshotAsync(reference, cancellationToken)
                    .ConfigureAwait(false);
                candidates.Add(new CredentialRetirementCandidate(
                    reference, snapshot?.Version.Clone()));
            }
            return candidates;
        }
        catch
        {
            foreach (var candidate in candidates)
            {
                candidate.Dispose();
            }
            throw;
        }
    }

    public async Task<int> RetireUnreferencedAsync(
        IEnumerable<CredentialRetirementCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var failures = 0;
        var seen = new HashSet<CredentialReference>(CredentialReferenceComparer.Instance);
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Reference))
            {
                candidate.Dispose();
                continue;
            }
            try
            {
                if (!await TryRetireAsync(candidate, cancellationToken).ConfigureAwait(false))
                {
                    failures++;
                }
            }
            catch
            {
                failures++;
            }
            finally
            {
                candidate.Dispose();
            }
        }
        return failures;
    }

    private sealed class CredentialReferenceComparer : IEqualityComparer<CredentialReference>
    {
        public static CredentialReferenceComparer Instance { get; } = new();

        public bool Equals(CredentialReference? x, CredentialReference? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null &&
             StringComparer.OrdinalIgnoreCase.Equals(x.Store, y.Store) &&
             StringComparer.Ordinal.Equals(x.Key, y.Key));

        public int GetHashCode(CredentialReference value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Store),
            StringComparer.Ordinal.GetHashCode(value.Key));
    }

    private async Task<bool> TryRetireAsync(
        CredentialRetirementCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await WinArdDatabase.ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        try
        {
            if (await CountReferencesAsync(connection, transaction, candidate.Reference, cancellationToken)
                    .ConfigureAwait(false) != 0)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            await RetireReferenceAsync(connection, transaction, candidate.Reference, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
            return false;
        }

        try
        {
            using var result = await _store.CompareExchangeWithVersionAsync(
                candidate.Reference,
                candidate.Version,
                replacement: null,
                CancellationToken.None).ConfigureAwait(false);
            return result.Result == CredentialStoreCompareExchangeResult.Succeeded;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<long> CountReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM devices
                 WHERE credential_store = $store COLLATE NOCASE AND credential_key = $key) +
                (SELECT COUNT(*) FROM ssh_profiles
                 WHERE password_credential_store = $store COLLATE NOCASE AND password_credential_key = $key) +
                (SELECT COUNT(*) FROM ssh_profiles
                 WHERE passphrase_credential_store = $store COLLATE NOCASE AND passphrase_credential_key = $key);
            """;
        command.Parameters.AddWithValue("$store", reference.Store);
        command.Parameters.AddWithValue("$key", reference.Key);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task RetireReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO retired_credential_references(
                backend, credential_key, retired_utc)
            VALUES ($backend, $key, $retired_utc);
            """;
        command.Parameters.AddWithValue("$backend", reference.Store);
        command.Parameters.AddWithValue("$key", reference.Key);
        command.Parameters.AddWithValue(
            "$retired_utc",
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
