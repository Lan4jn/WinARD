using WinARD.Domain.Connections;

namespace WinARD.Desktop.Services;

public sealed record ConnectionEditorHostKeyPromptState(
    bool IsVisible,
    SshHostKeyEndpoint? Endpoint,
    string Algorithm,
    string? PreviousFingerprint,
    string NewFingerprint,
    bool IsChanged)
{
    public static ConnectionEditorHostKeyPromptState Hidden { get; } =
        new(false, null, string.Empty, null, string.Empty, false);
}

public sealed class ConnectionEditorHostKeyPrompt : ISshHostKeyPrompt, IDisposable
{
    private readonly object _gate = new();
    private PendingPrompt? _pending;
    private Preauthorization? _preauthorized;
    private ConnectionEditorHostKeyPromptState _state = ConnectionEditorHostKeyPromptState.Hidden;
    private bool _disposed;

    public event EventHandler? StateChanged;

    public ConnectionEditorHostKeyPromptState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public ValueTask<SshHostKeyPromptDecision> PromptAsync(
        SshHostKeyPromptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        PendingPrompt pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preauthorized is { } expected && expected.Request == request)
            {
                _preauthorized = null;
                expected.Detach();
                return ValueTask.FromResult(request.IsChanged
                    ? SshHostKeyPromptDecision.Replace
                    : SshHostKeyPromptDecision.Trust);
            }

            if (_pending is not null)
            {
                throw new InvalidOperationException("SSH 主机密钥确认已在等待处理。");
            }

            pending = new PendingPrompt(request.IsChanged);
            _pending = pending;
            _state = new ConnectionEditorHostKeyPromptState(
                true,
                request.Endpoint,
                request.Algorithm,
                request.PreviousFingerprint,
                request.NewFingerprint,
                request.IsChanged);
        }

        pending.SetCancellationRegistration(cancellationToken.Register(
            static state =>
            {
                var cancellation = (PromptCancellation)state!;
                cancellation.Owner.Complete(
                    SshHostKeyPromptDecision.Cancel,
                    expectedChanged: null,
                    cancellation.Pending);
            },
            new PromptCancellation(this, pending)));
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, pending))
            {
                return new ValueTask<SshHostKeyPromptDecision>(pending.Completion.Task);
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return new ValueTask<SshHostKeyPromptDecision>(pending.Completion.Task);
    }

    public void Trust() => Complete(SshHostKeyPromptDecision.Trust, expectedChanged: false);

    public void Replace() => Complete(SshHostKeyPromptDecision.Replace, expectedChanged: true);

    public void Cancel() => Complete(SshHostKeyPromptDecision.Cancel, expectedChanged: null);

    public IDisposable Preauthorize(SshHostKeyPromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending is not null)
            {
                throw new InvalidOperationException("SSH 主机密钥确认仍在等待处理。");
            }

            if (_preauthorized is not null)
            {
                throw new InvalidOperationException("SSH 主机密钥预授权仍在等待使用。");
            }

            var preauthorization = new Preauthorization(this, request);
            _preauthorized = preauthorization;
            return preauthorization;
        }
    }

    private void Remove(Preauthorization preauthorization)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_preauthorized, preauthorization))
            {
                _preauthorized = null;
            }
        }
    }

    private void Complete(
        SshHostKeyPromptDecision decision,
        bool? expectedChanged,
        PendingPrompt? expectedPending = null)
    {
        PendingPrompt? pending;
        lock (_gate)
        {
            pending = _pending;
            if (pending is null ||
                (expectedPending is not null && !ReferenceEquals(pending, expectedPending)) ||
                (expectedChanged is not null && pending.IsChanged != expectedChanged.Value))
            {
                return;
            }

            _pending = null;
            _state = ConnectionEditorHostKeyPromptState.Hidden;
        }

        pending.Complete(decision);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Preauthorization? preauthorization;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            preauthorization = _preauthorized;
            _preauthorized = null;
        }

        preauthorization?.Detach();
        Cancel();
        GC.SuppressFinalize(this);
    }

    private sealed class Preauthorization(
        ConnectionEditorHostKeyPrompt owner,
        SshHostKeyPromptRequest request) : IDisposable
    {
        private ConnectionEditorHostKeyPrompt? _owner = owner;

        public SshHostKeyPromptRequest Request { get; } = request;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(this);

        public void Detach() => Interlocked.Exchange(ref _owner, null);
    }

    private sealed record PromptCancellation(
        ConnectionEditorHostKeyPrompt Owner,
        PendingPrompt Pending);

    private sealed class PendingPrompt(bool isChanged)
    {
        private readonly object _gate = new();
        private CancellationTokenRegistration _cancellationRegistration;
        private bool _completed;

        public bool IsChanged { get; } = isChanged;

        public TaskCompletionSource<SshHostKeyPromptDecision> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetCancellationRegistration(CancellationTokenRegistration registration)
        {
            lock (_gate)
            {
                if (!_completed)
                {
                    _cancellationRegistration = registration;
                    return;
                }
            }

            _ = registration.Unregister();
        }

        public void Complete(SshHostKeyPromptDecision decision)
        {
            CancellationTokenRegistration registration;
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                registration = _cancellationRegistration;
                _cancellationRegistration = default;
            }

            _ = registration.Unregister();
            Completion.TrySetResult(decision);
        }
    }
}
