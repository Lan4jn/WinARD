using System.Runtime.ExceptionServices;
using WinARD.Application.Ports;
using WinARD.Domain.Sessions;

namespace WinARD.Application.Sessions;

public sealed class RemoteSession : IRemoteSessionRuntime, IAsyncDisposable
{
    private readonly object _disposeSync = new();
    private readonly IRfbClient _client;
    private readonly SessionStateMachine _stateMachine;
    private readonly TransportConnection _transport;
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private Task? _disposeTask;

    internal RemoteSession(
        SessionStateMachine stateMachine,
        TransportConnection transport,
        IRfbClient client)
    {
        _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public SessionState State
    {
        get
        {
            lock (_disposeSync)
            {
                return _stateMachine.Current;
            }
        }
    }

    public RemoteFramebufferSize FramebufferSize
    {
        get
        {
            EnsureConnected();
            return _client.FramebufferSize;
        }
    }

    public RemoteDisplayCapabilities DisplayCapabilities
    {
        get
        {
            EnsureConnected();
            return _client.DisplayCapabilities;
        }
    }

    public RemoteRuntimePerformanceSnapshot PerformanceSnapshot
    {
        get
        {
            EnsureConnected();
            return _client.PerformanceSnapshot;
        }
    }

    public ValueTask RequestFramebufferUpdateAsync(
        bool incremental,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        return _client.RequestFramebufferUpdateAsync(incremental, cancellationToken);
    }

    public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            return await _client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    public ValueTask SendPointerAsync(
        byte buttons,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        return _client.SendPointerAsync(buttons, x, y, cancellationToken);
    }

    public ValueTask SendKeyAsync(
        uint keysym,
        bool down,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        return _client.SendKeyAsync(keysym, down, cancellationToken);
    }

    public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return _client.SendClipboardTextAsync(text, cancellationToken);
    }

    public ValueTask DisconnectAsync() => DisposeAsync();

    public void MarkFailed()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is not null || _stateMachine.Current == SessionState.Failed)
            {
                return;
            }

            _stateMachine.MoveTo(SessionState.Failed);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        Task disposeTask;
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
                if (_stateMachine.Current == SessionState.Connected)
                {
                    _stateMachine.MoveTo(SessionState.Disconnecting);
                }
            }

            disposeTask = _disposeTask;
        }

        if (completion is not null)
        {
            _ = DisposeAndCompleteAsync(completion);
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeAndCompleteAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? failures = null;
        try
        {
            _client.BeginShutdown();
        }
        catch (Exception exception)
        {
            AddFailure(ref failures, exception);
        }

        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddFailure(ref failures, exception);
        }

        try
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddFailure(ref failures, exception);
        }

        lock (_disposeSync)
        {
            if (_stateMachine.Current is SessionState.Disconnecting or SessionState.Failed)
            {
                _stateMachine.MoveTo(SessionState.Idle);
            }
        }

        if (failures is null)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failures);
    }

    private static void AddFailure(ref List<Exception>? failures, Exception exception) =>
        (failures ??= []).Add(exception);

    private void EnsureConnected()
    {
        lock (_disposeSync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_stateMachine.Current != SessionState.Connected)
            {
                throw new InvalidOperationException("The remote session is not connected.");
            }
        }
    }
}
