using WinARD.Application.Sessions;

namespace WinARD.Desktop.Services;

public sealed class ReconnectTransitionCoordinator(
    AutomaticReconnectCoordinator automaticReconnect,
    IAsyncDisposable reservation,
    Func<Task> closeWindow,
    Func<CancellationToken, Task> reconnect)
{
    private readonly AutomaticReconnectCoordinator _automaticReconnect = automaticReconnect;
    private readonly IAsyncDisposable _reservation = reservation;
    private readonly Func<Task> _closeWindow = closeWindow;
    private readonly Func<CancellationToken, Task> _reconnect = reconnect;
    private readonly object _sync = new();
    private Task? _transition;

    public Task RunAsync(CancellationToken token)
    {
        lock (_sync)
        {
            return _transition ??= RunCoreAsync(token);
        }
    }

    private async Task RunCoreAsync(CancellationToken token)
    {
        Exception? cleanupFailure = null;
        try
        {
            await _automaticReconnect.StopAsync().ConfigureAwait(false);
            await _automaticReconnect.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
        finally
        {
            await _reservation.DisposeAsync().ConfigureAwait(false);
        }
        if (cleanupFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
        await _closeWindow().ConfigureAwait(false);
        await _reconnect(token).ConfigureAwait(false);
    }
}
