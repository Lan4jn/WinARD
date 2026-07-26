using WinARD.Desktop.Threading;

namespace WinARD.Desktop.Input;

internal sealed class RemoteInputOperationRunner(
    IUiDispatcher dispatcher,
    Func<Task> reportFailure,
    Func<Task> closeSession,
    Func<bool> isClosing,
    Action<Exception>? observeFailure = null)
{
    public async Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (isClosing())
        {
        }
        catch (Exception exception)
        {
            if (isClosing())
            {
                return;
            }

            try
            {
                observeFailure?.Invoke(exception);
            }
            catch (Exception)
            {
            }

            try
            {
                await reportFailure().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                Task close = Task.CompletedTask;
                await dispatcher.InvokeAsync(
                    () => close = closeSession(),
                    CancellationToken.None).ConfigureAwait(false);
                await close.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }
}

internal sealed class RemoteCursorVisibilityController(
    Action<bool> setHostCursorHidden,
    Action<bool> setOverlayVisible)
{
    private readonly Action<bool> _setHostCursorHidden =
        setHostCursorHidden ?? throw new ArgumentNullException(nameof(setHostCursorHidden));
    private readonly Action<bool> _setOverlayVisible =
        setOverlayVisible ?? throw new ArgumentNullException(nameof(setOverlayVisible));

    public void SetRemoteCursorVisible(bool visible)
    {
        _setHostCursorHidden(visible);
        _setOverlayVisible(visible);
    }

    public void Reset() => SetRemoteCursorVisible(false);
}

internal static class RemotePointerDispatch
{
    public static Task RunAsync(
        Action updateLocalState,
        RemoteInputOperationRunner runner,
        Func<Task> send)
    {
        ArgumentNullException.ThrowIfNull(updateLocalState);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(send);
        updateLocalState();
        return runner.RunAsync(send);
    }
}
