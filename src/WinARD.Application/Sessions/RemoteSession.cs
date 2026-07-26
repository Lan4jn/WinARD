using System.Runtime.ExceptionServices;
using WinARD.Application.Ports;
using WinARD.Domain.Sessions;

namespace WinARD.Application.Sessions;

public sealed class RemoteSession : IAsyncDisposable
{
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    internal RemoteSession(
        SessionStateMachine stateMachine,
        TransportConnection transport,
        IRfbClient client)
    {
        StateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public SessionStateMachine StateMachine { get; }

    public SessionState State => StateMachine.Current;

    public TransportConnection Transport { get; }

    public IRfbClient Client { get; }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        StateMachine.MoveTo(SessionState.Disconnecting);
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

        StateMachine.MoveTo(SessionState.Idle);
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
