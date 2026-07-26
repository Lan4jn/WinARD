using System.Security.Cryptography;
using System.Text;
using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Security.Migration;
using WinARD.Security.Secrets;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Security.Tests;

public sealed class CredentialStoreMigratorTests
{
    private static readonly CredentialReference Reference =
        CredentialReference.Create("migration", "entry-1");

    [Fact]
    public async Task Deletes_source_only_after_verified_target_readback()
    {
        var source = new MemoryCredentialStore("migrate-cf18");
        var target = new MemoryCredentialStore();

        await new CredentialStoreMigrator().MoveAsync(
            source,
            target,
            Reference,
            CancellationToken.None);

        Assert.True(source.Deleted);
        AssertSecretText("migrate-cf18", target.ReadText());
    }

    [Fact]
    public async Task Verification_failure_keeps_source_and_rolls_back_target()
    {
        var source = new MemoryCredentialStore("migrate-cf18");
        var target = new MemoryCredentialStore { CorruptReads = true };

        await Assert.ThrowsAsync<CryptographicException>(
            () => new CredentialStoreMigrator().MoveAsync(
                source,
                target,
                Reference,
                CancellationToken.None).AsTask());

        Assert.False(source.Deleted);
        Assert.True(target.Deleted);
    }

    [Fact]
    public async Task Rollback_restores_previous_target_value()
    {
        var source = new MemoryCredentialStore("new-value-8a11");
        var target = new MemoryCredentialStore("old-value-4e21")
        {
            CorruptReadsAfterSave = true,
        };

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => new CredentialStoreMigrator().MoveAsync(
                source,
                target,
                Reference,
                CancellationToken.None).AsTask());

        AssertSecretText("old-value-4e21", target.ReadText());
        Assert.False(source.Deleted);
    }

    [Fact]
    public async Task Rollback_failure_preserves_the_verification_exception()
    {
        var source = new MemoryCredentialStore("new-value-8a11");
        var target = new MemoryCredentialStore
        {
            CorruptReads = true,
            DeleteException = new IOException("rollback failed"),
        };

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => new CredentialStoreMigrator().MoveAsync(
                source,
                target,
                Reference,
                CancellationToken.None).AsTask());

        Assert.IsAssignableFrom<CryptographicException>(aggregate.InnerExceptions[0]);
        Assert.Equal("rollback failed", aggregate.InnerExceptions[1].Message);
        Assert.False(source.Deleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_delete_failure_is_uncertain_and_preserves_verified_target(
        bool deleteBeforeThrow)
    {
        var source = new MemoryCredentialStore("new-value-8a11")
        {
            DeleteException = new IOException("source delete uncertain"),
            DeleteValueBeforeThrow = deleteBeforeThrow,
        };
        var target = new MemoryCredentialStore();

        var exception = await Assert.ThrowsAsync<MigrationSourceDeleteUncertainException>(
            () => new CredentialStoreMigrator().MoveAsync(
                source,
                target,
                Reference,
                CancellationToken.None).AsTask());

        Assert.IsType<IOException>(exception.InnerException);
        AssertSecretText("new-value-8a11", target.ReadText());
        Assert.Equal(deleteBeforeThrow, source.Deleted);
    }

    [Fact]
    public async Task Source_delete_cancellation_preserves_verified_target()
    {
        var source = new MemoryCredentialStore("new-value-8a11")
        {
            DeleteException = new OperationCanceledException(),
        };
        var target = new MemoryCredentialStore();

        await Assert.ThrowsAsync<MigrationSourceDeleteUncertainException>(
            () => new CredentialStoreMigrator().MoveAsync(
                source,
                target,
                Reference,
                CancellationToken.None).AsTask());

        AssertSecretText("new-value-8a11", target.ReadText());
        Assert.False(source.Deleted);
    }

    [Fact]
    public async Task Target_save_commit_then_throw_deletes_new_target_and_keeps_source()
    {
        var source = new MemoryCredentialStore("new-value-8a11");
        var target = new MemoryCredentialStore
        {
            SaveExceptionAfterFirstCommit = new IOException("target save uncertain"),
        };

        var exception = await Assert.ThrowsAsync<MigrationTargetWriteUncertainException>(
            () => new CredentialStoreMigrator().MoveAsync(
                source,
                target,
                Reference,
                CancellationToken.None).AsTask());

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Null(target.ReadText());
        Assert.False(source.Deleted);
        Assert.True(target.RollbackUsedNonCancellableToken);
    }

    [Fact]
    public async Task Target_save_commit_then_throw_restores_previous_target_and_keeps_source()
    {
        var source = new MemoryCredentialStore("new-value-8a11");
        var target = new MemoryCredentialStore("old-value-4e21")
        {
            SaveExceptionAfterFirstCommit = new IOException("target save uncertain"),
        };

        var exception = await Assert.ThrowsAsync<MigrationTargetWriteUncertainException>(
            () => new CredentialStoreMigrator().MoveAsync(
                source,
                target,
                Reference,
                CancellationToken.None).AsTask());

        Assert.IsType<IOException>(exception.InnerException);
        AssertSecretText("old-value-4e21", target.ReadText());
        Assert.False(source.Deleted);
        Assert.True(target.RollbackUsedNonCancellableToken);
    }

    [Fact]
    public async Task Target_save_commit_then_cancel_still_rolls_back_without_caller_token()
    {
        var source = new MemoryCredentialStore("new-value-8a11");
        var target = new MemoryCredentialStore
        {
            SaveExceptionAfterFirstCommit = new OperationCanceledException(),
        };

        var exception = await Assert.ThrowsAsync<MigrationTargetWriteUncertainException>(
            () => new CredentialStoreMigrator().MoveAsync(
                source,
                target,
                Reference,
                new CancellationToken(canceled: false)).AsTask());

        Assert.IsType<OperationCanceledException>(exception.InnerException);
        Assert.Null(target.ReadText());
        Assert.False(source.Deleted);
        Assert.True(target.RollbackUsedNonCancellableToken);
    }

    [Fact]
    public async Task Target_save_uncertainty_aggregates_rollback_failure_and_keeps_source()
    {
        var source = new MemoryCredentialStore("new-value-8a11");
        var target = new MemoryCredentialStore
        {
            SaveExceptionAfterFirstCommit = new IOException("target save uncertain"),
            DeleteException = new IOException("rollback failed"),
        };

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => new CredentialStoreMigrator().MoveAsync(
                source,
                target,
                Reference,
                CancellationToken.None).AsTask());

        var uncertain = Assert.IsType<MigrationTargetWriteUncertainException>(
            aggregate.InnerExceptions[0]);
        Assert.IsType<IOException>(uncertain.InnerException);
        Assert.Equal("rollback failed", aggregate.InnerExceptions[1].Message);
        AssertSecretText("new-value-8a11", target.ReadText());
        Assert.False(source.Deleted);
    }

    [Fact]
    public async Task Target_save_uncertainty_aggregates_rollback_cancellation_and_keeps_source()
    {
        var source = new MemoryCredentialStore("new-value-8a11");
        var target = new MemoryCredentialStore
        {
            SaveExceptionAfterFirstCommit = new IOException("target save uncertain"),
            DeleteException = new OperationCanceledException(),
        };

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => new CredentialStoreMigrator().MoveAsync(
                source,
                target,
                Reference,
                CancellationToken.None).AsTask());

        Assert.IsType<MigrationTargetWriteUncertainException>(aggregate.InnerExceptions[0]);
        Assert.IsType<OperationCanceledException>(aggregate.InnerExceptions[1]);
        AssertSecretText("new-value-8a11", target.ReadText());
        Assert.False(source.Deleted);
    }

    private static void AssertSecretText(string expectedText, string? actualText)
    {
        Assert.NotNull(actualText);
        var expected = Encoding.UTF8.GetBytes(expectedText);
        var actual = Encoding.UTF8.GetBytes(actualText!);
        Span<byte> expectedDigest = stackalloc byte[32];
        Span<byte> actualDigest = stackalloc byte[32];
        try
        {
            SHA256.HashData(expected, expectedDigest);
            SHA256.HashData(actual, actualDigest);
            Assert.Equal(expected.Length, actual.Length);
            Assert.True(CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expectedDigest);
            CryptographicOperations.ZeroMemory(actualDigest);
        }
    }

    private sealed class MemoryCredentialStore(string? initial = null) : ICredentialStore
    {
        private byte[]? _value = initial is null ? null : Encoding.UTF8.GetBytes(initial);

        public bool CorruptReads { get; init; }

        public bool CorruptReadsAfterSave { get; init; }

        public Exception? DeleteException { get; init; }

        public Exception? SaveExceptionAfterFirstCommit { get; init; }

        public bool DeleteValueBeforeThrow { get; init; }

        private int _saveCount;

        public bool Deleted { get; private set; }

        public bool RollbackUsedNonCancellableToken { get; private set; }

        public ValueTask SaveAsync(
            CredentialReference reference,
            ISecret secret,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = new byte[secret.Length];
            secret.CopyTo(value);
            Replace(value);
            Deleted = false;
            _saveCount++;
            if (_saveCount == 1 && SaveExceptionAfterFirstCommit is not null)
            {
                return ValueTask.FromException(SaveExceptionAfterFirstCommit);
            }

            if (_saveCount > 1)
            {
                RollbackUsedNonCancellableToken = !cancellationToken.CanBeCanceled;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<ISecret?> ReadAsync(
            CredentialReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_value is null)
            {
                return ValueTask.FromResult<ISecret?>(null);
            }

            var copy = _value.ToArray();
            if (CorruptReads || (CorruptReadsAfterSave && _saveCount == 1))
            {
                copy[0] ^= 1;
            }

            return ValueTask.FromResult<ISecret?>(SecretBuffer.CopyFrom(copy));
        }

        public ValueTask DeleteAsync(
            CredentialReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RollbackUsedNonCancellableToken = !cancellationToken.CanBeCanceled;
            if (DeleteException is not null)
            {
                if (DeleteValueBeforeThrow)
                {
                    Replace(null);
                    Deleted = true;
                }

                return ValueTask.FromException(DeleteException);
            }

            Replace(null);
            Deleted = true;
            return ValueTask.CompletedTask;
        }

        public string? ReadText() => _value is null ? null : Encoding.UTF8.GetString(_value);

        private void Replace(byte[]? replacement)
        {
            if (_value is not null)
            {
                CryptographicOperations.ZeroMemory(_value);
            }

            _value = replacement;
        }
    }
}
