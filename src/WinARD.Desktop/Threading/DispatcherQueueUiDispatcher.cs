using Microsoft.UI.Dispatching;

namespace WinARD.Desktop.Threading;

public sealed class DispatcherQueueUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    private readonly DispatcherQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    public async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_queue.HasThreadAccess)
        {
            action();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        if (!_queue.TryEnqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }))
        {
            completion.TrySetException(new InvalidOperationException("The UI dispatcher is shutting down."));
        }

        await completion.Task.ConfigureAwait(false);
    }
}
