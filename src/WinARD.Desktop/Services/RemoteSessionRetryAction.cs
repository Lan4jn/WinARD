namespace WinARD.Desktop.Services;

public sealed class RemoteSessionRetryAction(
    Func<Task> closeSession,
    Func<CancellationToken, Task> retryRequested)
{
    private readonly object _sync = new();
    private readonly Func<Task> _closeSession = closeSession ??
        throw new ArgumentNullException(nameof(closeSession));
    private readonly Func<CancellationToken, Task> _retryRequested = retryRequested ??
        throw new ArgumentNullException(nameof(retryRequested));
    private Task? _execution;

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return _execution ??= ExecuteCoreAsync();
        }
    }

    private async Task ExecuteCoreAsync()
    {
        await _closeSession().ConfigureAwait(false);
        await _retryRequested(CancellationToken.None).ConfigureAwait(false);
    }
}
