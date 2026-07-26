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
        var targetWasWritten = false;
        var targetWasVerified = false;
        try
        {
            await target
                .SaveAsync(reference, sourceSecret, cancellationToken)
                .ConfigureAwait(false);
            targetWasWritten = true;
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
            targetWasWritten = false;
        }
        catch (Exception primaryException)
        {
            if (targetWasVerified)
            {
                throw new MigrationSourceDeleteUncertainException(primaryException);
            }

            if (targetWasWritten)
            {
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
                catch (Exception rollbackException)
                {
                    throw new AggregateException(primaryException, rollbackException);
                }
            }

            ExceptionDispatchInfo.Capture(primaryException).Throw();
            throw new InvalidOperationException("Unreachable.");
        }
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
