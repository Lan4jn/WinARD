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
    public MigrationTargetWriteUncertainException(Exception innerException)
        : base(
            "The target credential write failed after its final state became uncertain.",
            innerException)
    {
    }
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
            .ReadAsync(reference, cancellationToken)
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
                var recoveryExceptions = await RestoreTargetAsync(
                    target,
                    reference,
                    previousTarget).ConfigureAwait(false);
                if (recoveryExceptions.Count != 0)
                {
                    throw new AggregateException(
                        new[] { reportedPrimary }.Concat(recoveryExceptions));
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

    private static async ValueTask<IReadOnlyList<Exception>> RestoreTargetAsync(
        ICredentialStore target,
        CredentialReference reference,
        ISecret? previousTarget)
    {
        var exceptions = new List<Exception>();
        try
        {
            if (previousTarget is null)
            {
                await target
                    .DeleteAsync(reference, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await target
                    .SaveAsync(reference, previousTarget, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            exceptions.Add(exception);
        }

        try
        {
            using var recovered = await target
                .ReadAsync(reference, CancellationToken.None)
                .ConfigureAwait(false);
            var restored = previousTarget is null
                ? recovered is null
                : recovered is not null && FixedTimeEqual(previousTarget, recovered);
            if (!restored)
            {
                exceptions.Add(
                    new CryptographicException(
                        "Credential migration target recovery verification failed."));
            }
        }
        catch (Exception exception)
        {
            exceptions.Add(exception);
        }

        return exceptions;
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
