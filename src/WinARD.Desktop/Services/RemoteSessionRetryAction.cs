namespace WinARD.Desktop.Services;

public sealed class RemoteSessionRetryAction(
    Func<Task> closeSession,
    Func<CancellationToken, Task> retryRequested)
{
    private readonly Func<Task> _closeSession = closeSession ??
        throw new ArgumentNullException(nameof(closeSession));
    private readonly Func<CancellationToken, Task> _retryRequested = retryRequested ??
        throw new ArgumentNullException(nameof(retryRequested));

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _closeSession().ConfigureAwait(false);
        await _retryRequested(CancellationToken.None).ConfigureAwait(false);
    }
}
