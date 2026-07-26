namespace WinARD.Desktop.Services;

public sealed class PreauthorizedHostKeyPrompt(SshHostKeyPromptRequest expected) : ISshHostKeyPrompt
{
    private readonly SshHostKeyPromptRequest _expected = expected ??
        throw new ArgumentNullException(nameof(expected));
    private int _used;

    public ValueTask<SshHostKeyPromptDecision> PromptAsync(
        SshHostKeyPromptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _used, 1) != 0 || request != _expected)
        {
            return ValueTask.FromResult(SshHostKeyPromptDecision.Cancel);
        }

        return ValueTask.FromResult(request.IsChanged
            ? SshHostKeyPromptDecision.Replace
            : SshHostKeyPromptDecision.Trust);
    }
}
