using WinARD.Domain.Security;

namespace WinARD.Application.Ports;

public interface ISecret : IDisposable
{
    int Length { get; }

    void CopyTo(Span<byte> destination);

    ISecret Clone();
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

    ValueTask DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken);
}
