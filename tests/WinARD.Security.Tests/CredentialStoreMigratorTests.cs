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
        Assert.Equal("migrate-cf18", target.ReadText());
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

        Assert.Equal("old-value-4e21", target.ReadText());
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

    private sealed class MemoryCredentialStore(string? initial = null) : ICredentialStore
    {
        private byte[]? _value = initial is null ? null : Encoding.UTF8.GetBytes(initial);

        public bool CorruptReads { get; init; }

        public bool CorruptReadsAfterSave { get; init; }

        public Exception? DeleteException { get; init; }

        private bool _wasSaved;

        public bool Deleted { get; private set; }

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
            _wasSaved = true;
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
            if (CorruptReads || (CorruptReadsAfterSave && _wasSaved))
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
            if (DeleteException is not null)
            {
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
