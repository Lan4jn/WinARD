namespace WinARD.Desktop.Views;

public sealed class FullscreenToolbarHideScheduler : IAsyncDisposable
{
    private sealed class Operation(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly object _sync = new();
    private readonly Func<CancellationToken, Task> _delay;
    private readonly Func<Action, Task> _dispatch;
    private readonly HashSet<Operation> _active = [];
    private readonly List<Exception> _unobservedErrors = [];
    private TaskCompletionSource _stateChanged = NewStateChanged();
    private bool _disposed;

    public FullscreenToolbarHideScheduler(
        Func<CancellationToken, Task> delay,
        Func<Action, Task> dispatch)
    {
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public void Schedule(long generation, Func<long, bool> tryHide)
    {
        ArgumentNullException.ThrowIfNull(tryHide);
        Operation operation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelLocked();
            operation = new Operation(new CancellationTokenSource());
            _active.Add(operation);
            SignalStateChangedLocked();
            operation.Task = RunAsync(generation, tryHide, operation);
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            CancelLocked();
        }
    }

    public Task WhenIdleAsync() => WaitForIdleAsync(dispose: false);

    private async Task RunAsync(
        long generation,
        Func<long, bool> tryHide,
        Operation operation)
    {
        try
        {
            await _delay(operation.Cancellation.Token).ConfigureAwait(false);
            if (!operation.Cancellation.IsCancellationRequested)
            {
                await _dispatch(() => _ = tryHide(generation)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _unobservedErrors.Add(exception);
            }
        }
        finally
        {
            lock (_sync)
            {
                _active.Remove(operation);
                SignalStateChangedLocked();
            }
            operation.Cancellation.Dispose();
        }
    }

    private async Task WaitForIdleAsync(bool dispose)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (dispose)
                {
                    _disposed = true;
                    CancelLocked();
                }

                if (_active.Count == 0)
                {
                    ThrowAndClearErrorsLocked();
                    return;
                }

                changed = _stateChanged.Task;
            }

            await changed.ConfigureAwait(false);
        }
    }

    private void CancelLocked()
    {
        foreach (var operation in _active)
        {
            operation.Cancellation.Cancel();
        }
    }

    private void SignalStateChangedLocked()
    {
        var previous = _stateChanged;
        _stateChanged = NewStateChanged();
        previous.TrySetResult();
    }

    private void ThrowAndClearErrorsLocked()
    {
        if (_unobservedErrors.Count == 0)
        {
            return;
        }

        var errors = _unobservedErrors.ToArray();
        _unobservedErrors.Clear();
        throw new AggregateException(errors);
    }

    private static TaskCompletionSource NewStateChanged() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask DisposeAsync() => new(WaitForIdleAsync(dispose: true));
}
