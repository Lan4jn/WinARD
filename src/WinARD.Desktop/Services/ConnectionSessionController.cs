using System.Diagnostics;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;

namespace WinARD.Desktop.Services;

public sealed class ConnectionSessionController : IAsyncDisposable
{
    private readonly ConnectionAttemptWorkflow _attemptWorkflow;
    private readonly ActiveSessionCoordinator _coordinator;
    private readonly IDeviceRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RemoteSession? _session;
    private ActiveSessionCoordinator.ActiveSessionLease? _lease;
    private bool _disposed;

    public ConnectionSessionController(
        ConnectionAttemptWorkflow attemptWorkflow,
        ActiveSessionCoordinator coordinator,
        IDeviceRepository repository)
    {
        _attemptWorkflow = attemptWorkflow ?? throw new ArgumentNullException(nameof(attemptWorkflow));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public bool IsConnected => Volatile.Read(ref _session) is not null;

    public IReadOnlyList<ConnectionTestStageResult> StageResults { get; private set; } = [];

    public string StatusMessage { get; private set; } = string.Empty;

    public event Action<ConnectionProfile>? ProfileUpdated;

    public async Task ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                throw new SessionAlreadyActiveException();
            }

            var lease = await _coordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var completed = new List<ConnectionTestStageResult>();
            var timer = Stopwatch.StartNew();
            ConnectionStage? active = null;
            void StageChanged(ConnectionStage stage)
            {
                if (stage == ConnectionStage.Resolving && active is not null)
                {
                    active = null;
                    timer.Restart();
                }

                if (active is { } previous)
                {
                    completed.Add(new ConnectionTestStageResult(previous, true, timer.Elapsed, "阶段完成。"));
                }

                active = stage;
                timer.Restart();
            }

            try
            {
                var outcome = await _attemptWorkflow.AttemptAsync(
                    profile,
                    StageChanged,
                    async (updated, token) =>
                    {
                        await _repository.SaveAsync(updated, token).ConfigureAwait(false);
                        ProfileUpdated?.Invoke(updated);
                    },
                    cancellationToken).ConfigureAwait(false);
                profile = outcome.Profile;
                var result = outcome.Result;
                if (result.Session is null)
                {
                    var error = result.Error ?? throw new InvalidOperationException("连接失败但没有错误信息。");
                    completed.Add(new ConnectionTestStageResult(error.Stage, false, timer.Elapsed, error.UserMessage));
                    StageResults = completed;
                    StatusMessage = error.UserMessage;
                    throw new ConnectionFailedException(result);
                }

                completed.Add(new ConnectionTestStageResult(
                    ConnectionStage.Connected, true, timer.Elapsed, "已连接。"));
                _lease = lease;
                lease = null;
                _session = result.Session;
                StageResults = completed;
                StatusMessage = "已连接。";
            }
            finally
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Interlocked.Exchange(ref _session, null);
            var lease = Interlocked.Exchange(ref _lease, null);
            try
            {
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }

                StatusMessage = "已断开。";
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }
}

public sealed class ConnectionFailedException : InvalidOperationException
{
    public ConnectionFailedException(ConnectResult result)
        : base(result.Error?.UserMessage ?? "连接失败。") => Result = result;

    public ConnectResult Result { get; }
}

public enum SshHostKeyPromptDecision
{
    Cancel,
    Trust,
    Replace,
}

public sealed record SshHostKeyPromptRequest(
    SshHostKeyEndpoint Endpoint,
    string Algorithm,
    string NewFingerprint,
    string? PreviousFingerprint,
    bool IsChanged);

public interface ISshHostKeyPrompt
{
    ValueTask<SshHostKeyPromptDecision> PromptAsync(
        SshHostKeyPromptRequest request,
        CancellationToken cancellationToken);
}
