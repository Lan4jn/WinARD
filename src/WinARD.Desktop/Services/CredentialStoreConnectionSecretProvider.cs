using WinARD.Application.Ports;
using WinARD.Domain.Connections;

namespace WinARD.Desktop.Services;

public sealed class CredentialStoreConnectionSecretProvider(
    ICredentialStore credentialStore,
    CredentialPromptService promptService) : IConnectionSecretProvider
{
    private readonly ICredentialStore _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    private readonly CredentialPromptService _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));

    public async ValueTask<ISecret> GetSecretAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var reference = profile.CredentialReference ??
            throw new InvalidOperationException("The selected device has no saved credential reference.");
        if (string.Equals(reference.Store, "ask", StringComparison.OrdinalIgnoreCase))
        {
            return await _promptService.PromptAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        return await _credentialStore.ReadAsync(reference, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("The selected device credential is unavailable.");
    }
}
