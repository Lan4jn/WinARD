using WinARD.Application.Ports;
using WinARD.Domain.Connections;
using WinARD.Domain.Security;

namespace WinARD.Desktop.Services;

public sealed class CredentialPromptService
{
    private readonly object _gate = new();
    private Func<ConnectionProfile, CancellationToken, ValueTask<ISecret>>? _handler;
    private Func<CredentialReference, CancellationToken, ValueTask<ISecret>>? _referenceHandler;

    public void SetHandler(Func<ConnectionProfile, CancellationToken, ValueTask<ISecret>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handler = handler;
        }
    }

    public void ClearHandler()
    {
        lock (_gate)
        {
            _handler = null;
        }
    }

    public void SetReferenceHandler(
        Func<CredentialReference, CancellationToken, ValueTask<ISecret>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _referenceHandler = handler;
        }
    }

    public void ClearReferenceHandler()
    {
        lock (_gate)
        {
            _referenceHandler = null;
        }
    }

    public ValueTask<ISecret> PromptAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        Func<ConnectionProfile, CancellationToken, ValueTask<ISecret>>? handler;
        lock (_gate)
        {
            handler = _handler;
        }

        return handler is null
            ? ValueTask.FromException<ISecret>(new InvalidOperationException("凭据提示窗口当前不可用。"))
            : handler(profile, cancellationToken);
    }

    public ValueTask<ISecret> PromptReferenceAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        Func<CredentialReference, CancellationToken, ValueTask<ISecret>>? handler;
        lock (_gate)
        {
            handler = _referenceHandler;
        }

        return handler is null
            ? ValueTask.FromException<ISecret>(new InvalidOperationException("凭据提示窗口当前不可用。"))
            : handler(reference, cancellationToken);
    }
}
