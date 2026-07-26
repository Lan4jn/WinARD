using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using WinARD.Application.Ports;
using WinARD.Domain.Security;

namespace WinARD.Security.Migration;

public sealed class MigrationSourceDeleteUncertainException : IOException
{
    public MigrationSourceDeleteUncertainException(Exception innerException)
        : base(
            "The credential was verified in the target store, but source deletion is uncertain.",
            innerException)
    {
    }
}

public sealed class MigrationTargetWriteUncertainException : IOException
{
    public MigrationTargetWriteUncertainException(
        Exception innerException,
        string safeCode = "MIGRATION_TARGET_WRITE_UNCERTAIN")
        : base(
            "The target credential write failed after its final state became uncertain.",
            innerException)
    {
        SafeCode = safeCode;
    }

    public string SafeCode { get; }
}

public sealed class CredentialStoreMigrator
{
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The migrator is an injectable application service.")]
    public async ValueTask MoveAsync(
        ICredentialStore source,
        ICredentialStore target,
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(reference);

        using var sourceSecret = await source
            .ReadAsync(reference, cancellationToken)
            .ConfigureAwait(false);
        if (sourceSecret is null)
        {
            return;
        }

        using var previousTarget = await target
            .ReadSnapshotAsync(reference, cancellationToken)
            .ConfigureAwait(false);
        var targetWriteAttempted = false;
        var targetWriteCompleted = false;
        var targetWasVerified = false;
        try
        {
            targetWriteAttempted = true;
            await target
                .SaveAsync(reference, sourceSecret, cancellationToken)
                .ConfigureAwait(false);
            targetWriteCompleted = true;
            using var readback = await target
                .ReadAsync(reference, cancellationToken)
                .ConfigureAwait(false);
            if (readback is null || !FixedTimeEqual(sourceSecret, readback))
            {
                throw new CryptographicException(
                    "Credential migration verification failed.");
            }

            targetWasVerified = true;
            await source.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primaryException)
        {
            if (targetWasVerified)
            {
                throw new MigrationSourceDeleteUncertainException(primaryException);
            }

            if (targetWriteAttempted)
            {
                var reportedPrimary = targetWriteCompleted
                    ? primaryException
                    : new MigrationTargetWriteUncertainException(primaryException);
                try
                {
                    var restored = await RestoreTargetAsync(
                        target,
                        reference,
                        sourceSecret,
                        previousTarget).ConfigureAwait(false);
                    if (!restored)
                    {
                        throw new MigrationTargetWriteUncertainException(
                            primaryException,
                            "MIGRATION_TARGET_CONCURRENT_CHANGE");
                    }
                }
                catch (Exception recoveryException)
                    when (recoveryException is not MigrationTargetWriteUncertainException)
                {
                    throw new AggregateException(reportedPrimary, recoveryException);
                }

                if (!targetWriteCompleted)
                {
                    throw reportedPrimary;
                }
            }

            ExceptionDispatchInfo.Capture(primaryException).Throw();
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private static async ValueTask<bool> RestoreTargetAsync(
        ICredentialStore target,
        CredentialReference reference,
        ISecret attemptedSecret,
        CredentialStoreSnapshot? previousTarget)
    {
        using var current = await target
            .ReadSnapshotAsync(reference, CancellationToken.None)
            .ConfigureAwait(false);
        if (current is null || !FixedTimeEqual(attemptedSecret, current.Secret))
        {
            return false;
        }

        var result = await target
            .CompareExchangeAsync(
                reference,
                current.Version,
                previousTarget?.Secret,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (result != CredentialStoreCompareExchangeResult.Succeeded)
        {
            return false;
        }

        return true;
    }

    private static bool FixedTimeEqual(ISecret left, ISecret right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var leftBytes = new byte[left.Length];
        var rightBytes = new byte[right.Length];
        Span<byte> leftDigest = stackalloc byte[32];
        Span<byte> rightDigest = stackalloc byte[32];
        try
        {
            left.CopyTo(leftBytes);
            right.CopyTo(rightBytes);
            SHA256.HashData(leftBytes, leftDigest);
            SHA256.HashData(rightBytes, rightDigest);
            return CryptographicOperations.FixedTimeEquals(leftDigest, rightDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
            CryptographicOperations.ZeroMemory(leftDigest);
            CryptographicOperations.ZeroMemory(rightDigest);
        }
    }
}
