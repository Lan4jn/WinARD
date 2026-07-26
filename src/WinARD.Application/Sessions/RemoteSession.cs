using System.Runtime.ExceptionServices;
using WinARD.Application.Ports;
using WinARD.Domain.Sessions;

namespace WinARD.Application.Sessions;

public sealed class RemoteSession : IAsyncDisposable
{
    private readonly object _disposeSync = new();
    private readonly SessionStateMachine _stateMachine;
    private Task? _disposeTask;

    internal RemoteSession(
        SessionStateMachine stateMachine,
        TransportConnection transport,
        IRfbClient client)
    {
        _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Client = client ?? throw new ArgumentNullException(nameof(client));
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

    public TransportConnection Transport { get; }

    public IRfbClient Client { get; }

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
        Task disposeTask;
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                if (_stateMachine.Current == SessionState.Connected)
                {
                    _stateMachine.MoveTo(SessionState.Disconnecting);
                }

                _disposeTask = DisposeCoreAsync();
            }

            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? failures = null;
        try
        {
            await Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddFailure(ref failures, exception);
        }

        try
        {
            await Transport.DisposeAsync().ConfigureAwait(false);
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
}
