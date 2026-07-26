using WinARD.Desktop.Threading;

namespace WinARD.Desktop.Input;

internal sealed class RemoteInputOperationRunner(
    IUiDispatcher dispatcher,
    Func<Task> reportFailure,
    Func<Task> closeSession,
    Func<bool> isClosing)
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
        catch (Exception)
        {
            if (isClosing())
            {
                return;
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
