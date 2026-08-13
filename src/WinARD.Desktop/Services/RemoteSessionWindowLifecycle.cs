namespace WinARD.Desktop.Services;

public sealed class RemoteSessionWindowLifecycle : IDisposable
{
    private readonly object _sync = new();
    private readonly Task _sessionCompletion;
    private readonly Func<bool> _hasTerminalError;
    private readonly Func<Task> _stopSession;
    private readonly Func<Task> _closeWindow;
    private readonly Func<CancellationToken, Task>? _retryRequested;
    private readonly Action? _closingStarted;
    private readonly bool _retryOwnsWindowClose;
    private readonly CancellationTokenSource _retryCancellation = new();
    private Task? _stopTask;
    private Task? _closeWindowTask;
    private Task? _retryTask;
    private Task? _disconnectTask;
    private bool _retryStarted;
    private bool _disconnectStarted;
    private bool _closingNotified;
    private bool _disposed;

    public RemoteSessionWindowLifecycle(
        Task sessionCompletion,
        Func<bool> hasTerminalError,
        Func<Task> stopSession,
        Func<Task> closeWindow,
        Func<CancellationToken, Task>? retryRequested,
        Action? closingStarted = null,
        bool retryOwnsWindowClose = false)
    {
        _sessionCompletion = sessionCompletion ??
            throw new ArgumentNullException(nameof(sessionCompletion));
        _hasTerminalError = hasTerminalError ??
            throw new ArgumentNullException(nameof(hasTerminalError));
        _stopSession = stopSession ??
            throw new ArgumentNullException(nameof(stopSession));
        _closeWindow = closeWindow ??
            throw new ArgumentNullException(nameof(closeWindow));
        _retryRequested = retryRequested;
        _closingStarted = closingStarted;
        _retryOwnsWindowClose = retryOwnsWindowClose;
    }

    public bool IsSessionStopped
    {
        get
        {
            lock (_sync)
            {
                return _stopTask is not null;
            }
        }
    }

    public bool CanRetry
    {
        get
        {
            lock (_sync)
            {
                return _retryRequested is not null &&
                       !_retryStarted &&
                       !_disconnectStarted &&
                       !_disposed;
            }
        }
    }

    public bool CanDisconnect
    {
        get
        {
            lock (_sync)
            {
                return !_disconnectStarted && !_disposed;
            }
        }
    }

    public async Task ObserveCompletionAsync(CancellationToken windowLifetime)
    {
        await _sessionCompletion.ConfigureAwait(false);
        if (windowLifetime.IsCancellationRequested)
        {
            return;
        }

        if (_hasTerminalError())
        {
            await StopSessionAsync().ConfigureAwait(false);
        }
        else
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    public Task StopSessionAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _stopTask ??= _stopSession();
        }
    }

    public Task RetryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource completion;
        var notifyClosing = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_disconnectStarted)
            {
                return Task.FromCanceled(new CancellationToken(canceled: true));
            }

            if (_retryTask is not null)
            {
                return _retryTask;
            }

            if (_retryRequested is null)
            {
                return Task.FromException(
                    new InvalidOperationException("Remote session retry is not available."));
            }

            _retryStarted = true;
            notifyClosing = MarkClosingStartedNoLock();
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _retryTask = completion.Task;
        }

        NotifyClosingStarted(notifyClosing);
        _ = CompleteRetryAsync(completion);
        return completion.Task;
    }

    public Task DisconnectAsync()
    {
        TaskCompletionSource completion;
        var notifyClosing = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_disconnectTask is not null)
            {
                return _disconnectTask;
            }

            _disconnectStarted = true;
            notifyClosing = MarkClosingStartedNoLock();
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disconnectTask = completion.Task;
        }

        NotifyClosingStarted(notifyClosing);
        _retryCancellation.Cancel();
        _ = CompleteDisconnectAsync(completion);
        return completion.Task;
    }

    private bool MarkClosingStartedNoLock()
    {
        if (_closingNotified)
        {
            return false;
        }

        _closingNotified = true;
        return true;
    }

    private void NotifyClosingStarted(bool notify)
    {
        if (!notify || _closingStarted is null)
        {
            return;
        }

        try
        {
            _closingStarted();
        }
        catch (Exception)
        {
            // A window-state notification must not prevent terminal cleanup.
        }
    }

    public void Dispose()
    {
        Task? terminalAction;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            terminalAction = _retryTask ?? _disconnectTask;
        }

        if (terminalAction is null || terminalAction.IsCompleted)
        {
            _retryCancellation.Dispose();
            return;
        }

        _ = DisposeCancellationAfterAsync(terminalAction);
    }

    private async Task CompleteRetryAsync(TaskCompletionSource completion)
    {
        try
        {
            await StopSessionAsync().ConfigureAwait(false);
            _retryCancellation.Token.ThrowIfCancellationRequested();
            if (_retryOwnsWindowClose)
            {
                await _retryRequested!(_retryCancellation.Token).ConfigureAwait(false);
            }
            else
            {
                await CloseWindowOnceAsync().ConfigureAwait(false);
                await _retryRequested!(_retryCancellation.Token).ConfigureAwait(false);
            }
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

    private async Task CompleteDisconnectAsync(TaskCompletionSource completion)
    {
        try
        {
            await StopSessionAsync().ConfigureAwait(false);
            await CloseWindowOnceAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private Task CloseWindowOnceAsync()
    {
        lock (_sync)
        {
            return _closeWindowTask ??= _closeWindow();
        }
    }

    private async Task DisposeCancellationAfterAsync(Task terminalAction)
    {
        try
        {
            await terminalAction.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            _retryCancellation.Dispose();
        }
    }
}
