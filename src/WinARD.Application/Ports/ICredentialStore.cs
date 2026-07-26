using System.Security.Cryptography;
using WinARD.Domain.Security;

namespace WinARD.Application.Ports;

public interface ISecret : IDisposable
{
    int Length { get; }

    void CopyTo(Span<byte> destination);

    ISecret Clone();
}

public sealed class CredentialStoreVersion : IDisposable
{
    private byte[]? _value;

    private CredentialStoreVersion(byte[] value) => _value = value;

    public int Length => _value?.Length ??
        throw new ObjectDisposedException(nameof(CredentialStoreVersion));

    public static CredentialStoreVersion CopyFrom(ReadOnlySpan<byte> value) =>
        new(value.ToArray());

    public CredentialStoreVersion Clone() =>
        CopyFrom(_value ?? throw new ObjectDisposedException(nameof(CredentialStoreVersion)));

    public bool FixedTimeEquals(ReadOnlySpan<byte> candidate)
    {
        var value = _value ?? throw new ObjectDisposedException(nameof(CredentialStoreVersion));
        return value.Length == candidate.Length &&
            CryptographicOperations.FixedTimeEquals(value, candidate);
    }

    public bool FixedTimeEquals(CredentialStoreVersion candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var value = _value ?? throw new ObjectDisposedException(nameof(CredentialStoreVersion));
        var candidateValue = candidate._value ??
            throw new ObjectDisposedException(nameof(CredentialStoreVersion));
        return value.Length == candidateValue.Length &&
            CryptographicOperations.FixedTimeEquals(value, candidateValue);
    }

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _value, null);
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}

public sealed class CredentialStoreSnapshot : IDisposable
{
    private ISecret? _secret;
    private CredentialStoreVersion? _version;

    public CredentialStoreSnapshot(ISecret secret, CredentialStoreVersion version)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(version);
        _secret = secret;
        _version = version;
    }

    public ISecret Secret => _secret ??
        throw new ObjectDisposedException(nameof(CredentialStoreSnapshot));

    public CredentialStoreVersion Version => _version ??
        throw new ObjectDisposedException(nameof(CredentialStoreSnapshot));

    public void Dispose()
    {
        Interlocked.Exchange(ref _secret, null)?.Dispose();
        Interlocked.Exchange(ref _version, null)?.Dispose();
    }
}

public enum CredentialStoreCompareExchangeResult
{
    Succeeded,
    Conflict,
}

public sealed class CredentialStoreWriteResult : IDisposable
{
    private CredentialStoreVersion? _writtenVersion;

    public CredentialStoreWriteResult(
        CredentialStoreCompareExchangeResult result,
        CredentialStoreVersion? writtenVersion)
    {
        if (result == CredentialStoreCompareExchangeResult.Conflict && writtenVersion is not null)
        {
            throw new ArgumentException("A conflicting write cannot have a written version.", nameof(writtenVersion));
        }

        Result = result;
        _writtenVersion = writtenVersion;
    }

    public CredentialStoreCompareExchangeResult Result { get; }

    public CredentialStoreVersion? WrittenVersion => _writtenVersion;

    public void Dispose() => Interlocked.Exchange(ref _writtenVersion, null)?.Dispose();
}

public interface ICredentialStore
{
    ValueTask SaveAsync(
        CredentialReference reference,
        ISecret secret,
        CancellationToken cancellationToken);

    ValueTask<ISecret?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken);

    ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(
        CredentialReference reference,
        CancellationToken cancellationToken);

    ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken);

    ValueTask<CredentialStoreWriteResult> CompareExchangeWithVersionAsync(
        CredentialReference reference,
        CredentialStoreVersion? expectedVersion,
        ISecret? replacement,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken);
}

public enum CredentialPromptPurpose
{
    MacPassword,
    SshPassword,
    PrivateKeyPassphrase,
    VaultMaster,
}

public sealed record CredentialPromptRequest(
    CredentialReference Reference,
    CredentialPromptPurpose Purpose,
    string? DisplayName = null);

public interface IPurposeAwareCredentialStore : ICredentialStore
{
    ValueTask<ISecret?> ReadForPromptAsync(
        CredentialPromptRequest request,
        CancellationToken cancellationToken);
}
