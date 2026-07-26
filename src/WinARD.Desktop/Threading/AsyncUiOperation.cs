using System.Collections.Concurrent;

namespace WinARD.Desktop.Threading;

public sealed class AsyncUiOperation
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly HashSet<Task> _active = [];

    public IReadOnlyList<Exception> Errors => _errors.ToArray();

    public Task RunAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var task = RunCoreAsync(operation, cancellationToken);
        lock (_gate)
        {
            _active.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    _active.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_active);
        }
    }

    public void Report(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _errors.Enqueue(exception);
    }

    private async Task RunCoreAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }
}
