using System.Runtime.ExceptionServices;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Domain.Sessions;

namespace WinARD.Application.Sessions;

public sealed class RemoteSession : IRemoteSessionRuntime, IAsyncDisposable
{
    private readonly object _disposeSync = new();
    private readonly IRfbClient _client;
    private readonly SessionStateMachine _stateMachine;
    private readonly TransportConnection _transport;
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly object _preloadedSync = new();
    private readonly Queue<RemoteServerMessage> _preloadedMessages;
    private readonly QualityBootstrapState _bootstrapState;
    private Task? _disposeTask;

    internal RemoteSession(
        SessionStateMachine stateMachine,
        TransportConnection transport,
        IRfbClient client)
        : this(stateMachine, transport, client, Array.Empty<RemoteServerMessage>())
    {
    }

    internal RemoteSession(
        SessionStateMachine stateMachine,
        TransportConnection transport,
        IRfbClient client,
        IEnumerable<RemoteServerMessage> preloadedMessages)
        : this(
            stateMachine,
            transport,
            client,
            new PreloadedMessageOwnership(
                preloadedMessages ?? throw new ArgumentNullException(nameof(preloadedMessages))))
    {
    }

    internal RemoteSession(
        SessionStateMachine stateMachine,
        TransportConnection transport,
        IRfbClient client,
        PreloadedMessageOwnership preloadedMessages,
        QualityBootstrapFailureReason? preferredFailureReason = null)
    {
        ArgumentNullException.ThrowIfNull(preloadedMessages);
        try
        {
            var snapshot = preloadedMessages.Snapshot();
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            var clientBootstrapState = client.BootstrapState;
            _bootstrapState = clientBootstrapState.Attempt is { } attempt
                ? new QualityBootstrapState(
                    attempt,
                    clientBootstrapState.ActualQuality,
                    preferredFailureReason)
                : QualityBootstrapState.LegacyBgra32;
            if (snapshot.Length > 8 || snapshot.Count(message => message is RemoteFramebufferMessage) > 1 ||
                snapshot.Any(message => message is not RemoteCursorMessage and not RemoteFramebufferMessage))
            {
                throw new ArgumentException("The preloaded message snapshot is invalid.", nameof(preloadedMessages));
            }

            _preloadedMessages = new Queue<RemoteServerMessage>(snapshot);
            preloadedMessages.Relinquish();
        }
        catch
        {
            preloadedMessages.Dispose();
            throw;
        }
    }

    public bool HasPreloadedFramebuffer
    {
        get
        {
            lock (_preloadedSync)
            {
                return _preloadedMessages.Any(message => message is RemoteFramebufferMessage);
            }
        }
    }

    public QualityBootstrapState BootstrapState => _bootstrapState;

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

    public ArdDisplayCapabilities QualityCapabilities
    {
        get
        {
            EnsureConnected();
            return _client.QualityCapabilities;
        }
    }

    public ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(
        RemoteQualitySettings settings,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        ArgumentNullException.ThrowIfNull(settings);
        return _client.ApplyQualityTransitionAsync(settings, cancellationToken);
    }

    public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            lock (_preloadedSync)
            {
                if (_preloadedMessages.Count > 0)
                {
                    return _preloadedMessages.Dequeue();
                }
            }

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
        RemoteServerMessage[] preloaded;
        lock (_preloadedSync)
        {
            preloaded = _preloadedMessages.ToArray();
            _preloadedMessages.Clear();
        }

        foreach (var message in preloaded)
        {
            if (message is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception exception)
                {
                    AddFailure(ref failures, exception);
                }
            }
        }

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
