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
    private readonly object _disposeSync = new();
    private readonly IDeviceRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RemoteSession? _session;
    private ConnectionProfile? _connectedProfile;
    private ActiveSessionCoordinator.ActiveSessionLease? _lease;
    private ConnectedSessionOwnership? _ownership;
    private bool _disposed;
    private Task? _disposeTask;

    public ConnectionSessionController(
        ConnectionAttemptWorkflow attemptWorkflow,
        ActiveSessionCoordinator coordinator,
        IDeviceRepository repository)
    {
        _attemptWorkflow = attemptWorkflow ?? throw new ArgumentNullException(nameof(attemptWorkflow));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public bool IsConnected =>
        Volatile.Read(ref _session) is not null || Volatile.Read(ref _ownership) is not null;

    public IReadOnlyList<ConnectionTestStageResult> StageResults { get; private set; } = [];

    public string StatusMessage { get; private set; } = string.Empty;

    public event Action<ConnectionProfile>? ProfileUpdated;

    public async Task ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        await ConnectAsync(profile, hostKeyPrompt: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConnectAsync(
        ConnectionProfile profile,
        ISshHostKeyPrompt? hostKeyPrompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var profilesToPublish = new List<ConnectionProfile>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null || _ownership is not null)
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
                        profilesToPublish.Add(updated);
                    },
                    hostKeyPrompt,
                    cancellationToken).ConfigureAwait(false);
                profile = outcome.Profile;
                var result = outcome.Result;
                if (result.Session is null)
                {
                    var error = result.Error ?? throw new InvalidOperationException("连接失败但没有错误信息。");
                    completed.Add(new ConnectionTestStageResult(error.Stage, false, timer.Elapsed, error.UserMessage));
                    StageResults = completed;
                    StatusMessage = error.UserMessage;
                    throw new ConnectionFailedException(outcome);
                }

                completed.Add(new ConnectionTestStageResult(
                    ConnectionStage.Connected, true, timer.Elapsed, "已连接。"));
                _lease = lease;
                lease = null;
                _session = result.Session;
                _connectedProfile = profile;
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
            foreach (var updated in profilesToPublish)
            {
                PublishProfileUpdated(updated);
            }
        }
    }

    public ConnectedSessionOwnership TransferConnectedSession()
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ownership is not null)
            {
                throw new InvalidOperationException("The connected session has already been transferred.");
            }

            var session = Interlocked.Exchange(ref _session, null) ??
                throw new InvalidOperationException("There is no connected session to transfer.");
            var lease = Interlocked.Exchange(ref _lease, null) ??
                throw new InvalidOperationException("The connected session lease is missing.");
            var effectiveProfile = Interlocked.Exchange(ref _connectedProfile, null) ??
                throw new InvalidOperationException("The connected session profile is missing.");
            var ownership = new ConnectedSessionOwnership(
                effectiveProfile,
                session,
                lease,
                OnOwnershipDisposed);
            Volatile.Write(ref _ownership, ownership);
            return ownership;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ConnectionProfile> UpdateConnectedFrameRefreshPolicyAsync(
        FrameRefreshPolicy policy,
        CancellationToken cancellationToken)
        => await UpdateConnectedProfileAsync(
            profile => profile.WithFrameRefreshPolicy(policy),
            cancellationToken).ConfigureAwait(false);

    public async Task<ConnectionProfile> UpdateConnectedQualityProfileAsync(
        QualityProfile quality,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quality);
        return await UpdateConnectedProfileAsync(
            profile => profile.WithQualityProfile(quality),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConnectionProfile> UpdateConnectedProfileAsync(
        Func<ConnectionProfile, ConnectionProfile> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ConnectionProfile updated;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var ownership = _ownership ??
                throw new InvalidOperationException("There is no transferred connected session to update.");
            await ownership.EnterProfileUpdateAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                updated = update(ownership.Profile);
                await _repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                ownership.UpdateProfile(updated);
            }
            finally
            {
                ownership.ExitProfileUpdate();
            }
        }
        finally
        {
            _gate.Release();
        }

        PublishProfileUpdated(updated);
        return updated;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Interlocked.Exchange(ref _session, null);
            _ = Interlocked.Exchange(ref _connectedProfile, null);
            var lease = Interlocked.Exchange(ref _lease, null);
            var ownership = Interlocked.Exchange(ref _ownership, null);
            try
            {
                if (ownership is not null)
                {
                    await ownership.DisposeAsync().ConfigureAwait(false);
                }
                else if (session is not null)
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

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }

    private void OnOwnershipDisposed(ConnectedSessionOwnership ownership)
    {
        _ = Interlocked.CompareExchange(ref _ownership, null, ownership);
        StatusMessage = "已断开。";
    }

    private void PublishProfileUpdated(ConnectionProfile profile)
    {
        var subscribers = ProfileUpdated;
        if (subscribers is null)
        {
            return;
        }

        foreach (Action<ConnectionProfile> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(profile);
            }
            catch (Exception)
            {
                // Persistence already committed; subscriber failures are isolated.
            }
        }
    }
}

public sealed class ConnectedSessionOwnership : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _profileOperationGate = new(1, 1);
    private readonly RemoteSession _session;
    private readonly ActiveSessionCoordinator.ActiveSessionLease _lease;
    private readonly Action<ConnectedSessionOwnership> _disposedCallback;
    private ConnectionProfile _profile;
    private OwnershipState _state;
    private Task? _disposeTask;

    internal ConnectedSessionOwnership(
        ConnectionProfile profile,
        RemoteSession session,
        ActiveSessionCoordinator.ActiveSessionLease lease,
        Action<ConnectedSessionOwnership> disposedCallback)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _session = session;
        _lease = lease;
        _disposedCallback = disposedCallback;
    }

    public RemoteSession Session => _session;

    public ConnectionProfile Profile
    {
        get
        {
            lock (_sync)
            {
                return _profile;
            }
        }
    }

    internal void UpdateProfile(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_sync)
        {
            _profile = profile;
        }
    }

    internal async Task EnterProfileUpdateAsync(CancellationToken cancellationToken)
    {
        await _profileOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (_state != OwnershipState.Active)
            {
                _profileOperationGate.Release();
                throw new ObjectDisposedException(nameof(ConnectedSessionOwnership));
            }
        }
    }

    internal void ExitProfileUpdate() => _profileOperationGate.Release();

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _profileOperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _state = OwnershipState.Disposing;
            }
        }
        finally
        {
            _profileOperationGate.Release();
        }

        Exception? sessionFailure = null;
        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            sessionFailure = exception;
        }

        try
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception leaseFailure) when (sessionFailure is not null)
        {
            throw new AggregateException(sessionFailure, leaseFailure);
        }
        finally
        {
            lock (_sync)
            {
                _state = OwnershipState.Disposed;
            }

            _disposedCallback(this);
        }

        if (sessionFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(sessionFailure).Throw();
        }
    }

    private enum OwnershipState
    {
        Active,
        Disposing,
        Disposed,
    }
}

public sealed class ConnectionFailedException : InvalidOperationException
{
    public ConnectionFailedException(ConnectionAttemptOutcome outcome)
        : base((outcome ?? throw new ArgumentNullException(nameof(outcome))).Result.Error?.UserMessage ??
            "连接失败。") => Outcome = outcome;

    public ConnectionAttemptOutcome Outcome { get; }

    public ConnectResult Result => Outcome.Result;

    public SshHostKeyPromptRequest? HostKeyFailure => Outcome.HostKeyFailure;
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
