using WinARD.Application.Ports;
using WinARD.Domain.Connections;
using WinARD.Infrastructure.Diagnostics;

namespace WinARD.Desktop.Services;

public sealed class CredentialStoreConnectionSecretProvider(
    ICredentialStore credentialStore,
    CredentialPromptService promptService,
    SecretRedactor redactor) : IConnectionSecretProvider
{
    private readonly ICredentialStore _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    private readonly CredentialPromptService _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
    private readonly SecretRedactor _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));

    public async ValueTask<ISecret> GetSecretAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var reference = profile.CredentialReference ??
            throw new InvalidOperationException("The selected device has no saved credential reference.");
        if (string.Equals(reference.Store, "ask", StringComparison.OrdinalIgnoreCase))
        {
            return new RegisteredSecret(await _promptService.PromptReferenceAsync(
                new CredentialPromptRequest(
                    reference,
                    CredentialPromptPurpose.MacPassword,
                    profile.DisplayName),
                cancellationToken).ConfigureAwait(false), _redactor);
        }

        var secret = await _credentialStore.ReadAsync(reference, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("The selected device credential is unavailable.");
        return secret is RegisteredSecret ? secret : new RegisteredSecret(secret, _redactor);
    }
}
