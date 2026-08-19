using WinARD.Domain.Security;

namespace WinARD.Application.Ports;

public sealed record CredentialRetirementCandidate(
    CredentialReference Reference,
    CredentialStoreVersion? Version) : IDisposable
{
    public void Dispose() => Version?.Dispose();
}

public interface ICredentialReferenceRetirementService
{
    ValueTask<IReadOnlyList<CredentialRetirementCandidate>> CaptureAsync(
        IEnumerable<CredentialReference> references,
        CancellationToken cancellationToken);

    Task<int> RetireUnreferencedAsync(
        IEnumerable<CredentialRetirementCandidate> candidates,
        CancellationToken cancellationToken);
}
