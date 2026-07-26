namespace WinARD.Desktop.Services;

public sealed class SshHostKeyPromptService : ISshHostKeyPrompt
{
    private readonly object _gate = new();
    private Func<SshHostKeyPromptRequest, CancellationToken, ValueTask<SshHostKeyPromptDecision>>? _handler;

    public void SetHandler(
        Func<SshHostKeyPromptRequest, CancellationToken, ValueTask<SshHostKeyPromptDecision>> handler)
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

    public ValueTask<SshHostKeyPromptDecision> PromptAsync(
        SshHostKeyPromptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Func<SshHostKeyPromptRequest, CancellationToken, ValueTask<SshHostKeyPromptDecision>>? handler;
        lock (_gate)
        {
            handler = _handler;
        }

        return handler is null
            ? ValueTask.FromResult(SshHostKeyPromptDecision.Cancel)
            : handler(request, cancellationToken);
    }
}
