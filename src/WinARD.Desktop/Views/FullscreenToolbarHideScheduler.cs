namespace WinARD.Desktop.Views;

public sealed class FullscreenToolbarHideScheduler : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _delay;
    private readonly Func<Action, Task> _dispatch;
    private CancellationTokenSource? _pending;
    private Task _operation = Task.CompletedTask;

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
        Cancel();
        var cancellation = new CancellationTokenSource();
        _pending = cancellation;
        _operation = RunAsync(generation, tryHide, cancellation);
    }

    public void Cancel()
    {
        Interlocked.Exchange(ref _pending, null)?.Cancel();
    }

    public Task WhenIdleAsync() => _operation;

    private async Task RunAsync(
        long generation,
        Func<long, bool> tryHide,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _delay(cancellation.Token).ConfigureAwait(false);
            if (!cancellation.IsCancellationRequested)
            {
                await _dispatch(() => _ = tryHide(generation)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _pending, null, cancellation);
            cancellation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        await _operation.ConfigureAwait(false);
    }
}
