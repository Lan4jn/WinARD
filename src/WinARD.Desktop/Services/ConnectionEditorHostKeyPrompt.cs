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
    private SshHostKeyPromptRequest? _preauthorized;
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
            if (Interlocked.Exchange(ref _preauthorized, null) is { } expected && expected == request)
            {
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

        pending.CancellationRegistration = cancellationToken.Register(
            static state => ((ConnectionEditorHostKeyPrompt)state!).Cancel(),
            this);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return new ValueTask<SshHostKeyPromptDecision>(pending.Completion.Task);
    }

    public void Trust() => Complete(SshHostKeyPromptDecision.Trust, expectedChanged: false);

    public void Replace() => Complete(SshHostKeyPromptDecision.Replace, expectedChanged: true);

    public void Cancel() => Complete(SshHostKeyPromptDecision.Cancel, expectedChanged: null);

    public void Preauthorize(SshHostKeyPromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending is not null)
            {
                throw new InvalidOperationException("SSH 主机密钥确认仍在等待处理。");
            }

            _preauthorized = request;
        }
    }

    private void Complete(SshHostKeyPromptDecision decision, bool? expectedChanged)
    {
        PendingPrompt? pending;
        lock (_gate)
        {
            pending = _pending;
            if (pending is null ||
                (expectedChanged is not null && pending.IsChanged != expectedChanged.Value))
            {
                return;
            }

            _pending = null;
            _state = ConnectionEditorHostKeyPromptState.Hidden;
        }

        _ = pending.CancellationRegistration.Unregister();
        pending.Completion.TrySetResult(decision);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _preauthorized = null;
        }

        Cancel();
        GC.SuppressFinalize(this);
    }

    private sealed class PendingPrompt(bool isChanged)
    {
        public bool IsChanged { get; } = isChanged;

        public TaskCompletionSource<SshHostKeyPromptDecision> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }
}
