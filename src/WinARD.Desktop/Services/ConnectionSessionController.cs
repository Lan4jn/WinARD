using System.Diagnostics;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Transport.Ssh;

namespace WinARD.Desktop.Services;

public sealed class ConnectionSessionController : IAsyncDisposable
{
    private readonly ConnectDeviceHandler _handler;
    private readonly ActiveSessionCoordinator _coordinator;
    private readonly IDeviceRepository _repository;
    private readonly ISshHostKeyPrompt _hostKeyPrompt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RemoteSession? _session;
    private ActiveSessionCoordinator.ActiveSessionLease? _lease;
    private bool _disposed;

    public ConnectionSessionController(
        ConnectDeviceHandler handler,
        ActiveSessionCoordinator coordinator,
        IDeviceRepository repository,
        ISshHostKeyPrompt hostKeyPrompt)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _hostKeyPrompt = hostKeyPrompt ?? throw new ArgumentNullException(nameof(hostKeyPrompt));
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
                if (active is { } previous)
                {
                    completed.Add(new ConnectionTestStageResult(previous, true, timer.Elapsed, "阶段完成。"));
                }

                active = stage;
                timer.Restart();
            }

            try
            {
                var hostKeyRetried = false;
                ConnectResult result;
                while (true)
                {
                    Exception? failure = null;
                    result = await _handler.HandleWithFailureObservationAsync(
                        profile,
                        StageChanged,
                        exception => failure = exception,
                        cancellationToken).ConfigureAwait(false);
                    if (result.Session is not null || hostKeyRetried ||
                        !TryGetHostKeyFailure(failure, profile, out var verification, out var request))
                    {
                        break;
                    }

                    var decision = await _hostKeyPrompt.PromptAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    var accepted = request.IsChanged
                        ? decision == SshHostKeyPromptDecision.Replace
                        : decision == SshHostKeyPromptDecision.Trust;
                    if (!accepted)
                    {
                        break;
                    }

                    var ssh = profile.SshProfile ??
                        throw new InvalidOperationException("SSH 主机密钥确认缺少 SSH 配置。");
                    profile = profile.WithSsh(ssh.WithHostKeyPin(verification.ToPin()));
                    await _repository.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
                    ProfileUpdated?.Invoke(profile);
                    hostKeyRetried = true;
                    active = null;
                    timer.Restart();
                }

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

    private static bool TryGetHostKeyFailure(
        Exception? exception,
        ConnectionProfile profile,
        out SshHostKeyVerification verification,
        out SshHostKeyPromptRequest request)
    {
        verification = exception switch
        {
            SshHostKeyUnknownException unknown => unknown.Verification,
            SshHostKeyChangedException { Verification: not null } changed => changed.Verification,
            _ => null!,
        };
        if (verification is null)
        {
            request = null!;
            return false;
        }

        var isChanged = verification.Status == SshHostKeyStatus.Changed;
        request = new SshHostKeyPromptRequest(
            verification.Endpoint,
            verification.Algorithm,
            verification.Fingerprint,
            isChanged ? profile.SshProfile?.HostKeyPin?.Fingerprint : null,
            isChanged);
        return true;
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
