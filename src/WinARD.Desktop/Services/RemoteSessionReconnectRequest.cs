using WinARD.Domain.Connections;
using WinARD.Application.Sessions;
using WinARD.Domain.Errors;
using WinARD.Infrastructure.Diagnostics;

namespace WinARD.Desktop.Services;

public interface IReconnectProfileCapture
{
    void Capture(ConnectionProfile profile);

    void CaptureReconnectDiagnostic(DiagnosticReconnectSummary summary) { }

    void ClearReconnectDiagnostic() { }
}

internal sealed class RemoteSessionReconnectRequest : IReconnectProfileCapture
{
    private ConnectionProfile _profile;
    private DiagnosticReconnectSummary? _pendingDiagnostic;
    private readonly Func<Guid, CancellationToken, Task<ConnectionProfile?>> _reloadProfile;
    private readonly Func<ConnectionProfile, DiagnosticReconnectSummary?, CancellationToken, Task> _reconnect;
    private readonly Func<ConnectionProfile, DiagnosticReconnectSummary?, CancellationToken, Task>
        _reconnectWithoutReservation;

    public RemoteSessionReconnectRequest(
        ConnectionProfile initialProfile,
        Func<Guid, CancellationToken, Task<ConnectionProfile?>> reloadProfile,
        Func<ConnectionProfile, CancellationToken, Task> reconnect,
        Func<ConnectionProfile, CancellationToken, Task>? reconnectWithoutReservation = null)
        : this(
            initialProfile,
            reloadProfile,
            (profile, _, token) => reconnect(profile, token),
            reconnectWithoutReservation is null
                ? null
                : (profile, _, token) => reconnectWithoutReservation(profile, token))
    {
    }

    public RemoteSessionReconnectRequest(
        ConnectionProfile initialProfile,
        Func<Guid, CancellationToken, Task<ConnectionProfile?>> reloadProfile,
        Func<ConnectionProfile, DiagnosticReconnectSummary?, CancellationToken, Task> reconnect,
        Func<ConnectionProfile, DiagnosticReconnectSummary?, CancellationToken, Task>?
            reconnectWithoutReservation = null)
    {
        _profile = initialProfile ?? throw new ArgumentNullException(nameof(initialProfile));
        _reloadProfile = reloadProfile ?? throw new ArgumentNullException(nameof(reloadProfile));
        _reconnect = reconnect ?? throw new ArgumentNullException(nameof(reconnect));
        _reconnectWithoutReservation = reconnectWithoutReservation ?? reconnect;
    }

    public void Capture(ConnectionProfile profile) =>
        Volatile.Write(ref _profile, profile ?? throw new ArgumentNullException(nameof(profile)));

    public void CaptureReconnectDiagnostic(DiagnosticReconnectSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.State is not ("Waiting" or "Connecting") ||
            summary.Attempt is < 1 or > 1_000 ||
            summary.DelaySeconds is < 0 or > 3_600)
        {
            throw new ArgumentOutOfRangeException(nameof(summary));
        }
        Volatile.Write(ref _pendingDiagnostic, summary);
    }

    public void ClearReconnectDiagnostic() => Volatile.Write(ref _pendingDiagnostic, null);

    internal DiagnosticReconnectSummary? PendingDiagnosticForTest =>
        Volatile.Read(ref _pendingDiagnostic);

    public Task InvokeAsync(CancellationToken token) => InvokeAsync(_reconnect, token);

    public Task InvokeWithoutReservationAsync(CancellationToken token) =>
        InvokeAsync(_reconnectWithoutReservation, token);

    private async Task InvokeAsync(
        Func<ConnectionProfile, DiagnosticReconnectSummary?, CancellationToken, Task> reconnect,
        CancellationToken cancellationToken)
    {
        var desired = Volatile.Read(ref _profile);
        ConnectionProfile? persisted;
        try
        {
            persisted = await _reloadProfile(desired.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw ProfileUnavailable();
        }
        if (persisted is null)
        {
            throw ProfileUnavailable();
        }

        var currentDiagnostic = Volatile.Read(ref _pendingDiagnostic);
        var succeeded = currentDiagnostic is null
            ? null
            : currentDiagnostic with { State = "Succeeded", DelaySeconds = 0 };
        await reconnect(persisted.WithQualityProfile(desired.Quality), succeeded, cancellationToken)
            .ConfigureAwait(false);
        if (currentDiagnostic is not null)
        {
            Interlocked.CompareExchange(ref _pendingDiagnostic, null, currentDiagnostic);
        }
    }

    private static ReconnectFailureException ProfileUnavailable() => new(WinArdError.Create(
        ConnectionStage.Connecting,
        "RECONNECT_PROFILE_UNAVAILABLE",
        "设备配置已删除或无法读取，已停止重新连接。",
        Guid.NewGuid().ToString("N")));
}
