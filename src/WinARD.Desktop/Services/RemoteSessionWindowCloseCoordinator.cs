namespace WinARD.Desktop.Services;

public sealed class RemoteSessionWindowCloseCoordinator
{
    private readonly object _sync = new();
    private readonly Action _cancelScheduler;
    private readonly Func<Task> _disposeScheduler;
    private readonly Action _cancelLifetime;
    private readonly Func<Task> _closeWindow;
    private Task? _closeTask;

    public RemoteSessionWindowCloseCoordinator(
        Action cancelScheduler,
        Func<Task> disposeScheduler,
        Action cancelLifetime,
        Func<Task> closeWindow)
    {
        _cancelScheduler = cancelScheduler ?? throw new ArgumentNullException(nameof(cancelScheduler));
        _disposeScheduler = disposeScheduler ?? throw new ArgumentNullException(nameof(disposeScheduler));
        _cancelLifetime = cancelLifetime ?? throw new ArgumentNullException(nameof(cancelLifetime));
        _closeWindow = closeWindow ?? throw new ArgumentNullException(nameof(closeWindow));
    }

    public Task CloseAsync()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_closeTask is not null)
            {
                return _closeTask;
            }

            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _closeTask = completion.Task;
        }

        _ = CompleteCloseAsync(completion);
        return completion.Task;
    }

    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try
        {
            await CleanupSequence.RunAsync(
                () =>
                {
                    _cancelScheduler();
                    return Task.CompletedTask;
                },
                _disposeScheduler,
                () =>
                {
                    _cancelLifetime();
                    return Task.CompletedTask;
                },
                _closeWindow).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}
