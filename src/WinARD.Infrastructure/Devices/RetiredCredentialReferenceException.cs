using WinARD.Domain.Security;

namespace WinARD.Infrastructure.Devices;

public sealed class RetiredCredentialReferenceException : InvalidOperationException
{
    public RetiredCredentialReferenceException(CredentialReference reference)
        : base("A retired credential reference cannot be reused.") =>
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));

    public CredentialReference Reference { get; }
}
