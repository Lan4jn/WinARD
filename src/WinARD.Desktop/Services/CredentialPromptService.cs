using WinARD.Application.Ports;

namespace WinARD.Desktop.Services;

public sealed class CredentialPromptService
{
    private readonly object _gate = new();
    private Func<CredentialPromptRequest, CancellationToken, ValueTask<ISecret>>? _referenceHandler;

    public void SetReferenceHandler(
        Func<CredentialPromptRequest, CancellationToken, ValueTask<ISecret>> handler)
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

    public ValueTask<ISecret> PromptReferenceAsync(
        CredentialPromptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Func<CredentialPromptRequest, CancellationToken, ValueTask<ISecret>>? handler;
        lock (_gate)
        {
            handler = _referenceHandler;
        }

        return handler is null
            ? ValueTask.FromException<ISecret>(new InvalidOperationException("凭据提示窗口当前不可用。"))
            : handler(request, cancellationToken);
    }
}
