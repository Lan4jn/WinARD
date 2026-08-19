using WinARD.Desktop.Services;
using WinARD.Application.Sessions;
using WinARD.Domain.Connections;
using WinARD.Domain.Security;
using WinARD.Infrastructure.Diagnostics;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Services;

public sealed class RemoteSessionReconnectRequestTests
{
    [Fact]
    public async Task Automatic_success_transfers_only_the_current_bounded_summary_to_the_new_session()
    {
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "private-host", 5900, "private-user");
        DiagnosticReconnectSummary? transferred = null;
        var request = new RemoteSessionReconnectRequest(
            profile,
            (_, _) => Task.FromResult<ConnectionProfile?>(profile),
            (_, summary, _) =>
            {
                transferred = summary;
                return Task.CompletedTask;
            });
        request.CaptureReconnectDiagnostic(new("Connecting", 3, 0));

        await request.InvokeAsync(default);

        Assert.Equal(new DiagnosticReconnectSummary("Succeeded", 3, 0), transferred);
        Assert.Null(request.PendingDiagnosticForTest);
    }

    [Fact]
    public async Task Manual_retry_clears_the_previous_automatic_summary()
    {
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user");
        DiagnosticReconnectSummary? transferred = new("Failed", 99, 0);
        var request = new RemoteSessionReconnectRequest(
            profile,
            (_, _) => Task.FromResult<ConnectionProfile?>(profile),
            (_, summary, _) =>
            {
                transferred = summary;
                return Task.CompletedTask;
            });
        request.CaptureReconnectDiagnostic(new("Connecting", 3, 0));

        request.ClearReconnectDiagnostic();
        await request.InvokeAsync(default);

        Assert.Null(transferred);
    }

    [Fact]
    public async Task Capture_is_atomic_and_invoke_uses_latest_desired_profile()
    {
        var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user");
        var desired = original.WithQualityProfile(QualityProfile.Smooth);
        ConnectionProfile? observed = null;
        var request = new RemoteSessionReconnectRequest(
            original,
            (_, _) => Task.FromResult<ConnectionProfile?>(desired),
            (profile, _) =>
            {
                observed = profile;
                return Task.CompletedTask;
            });

        request.Capture(desired);
        await request.InvokeAsync(default);

        Assert.Equal(desired, observed);
    }

    [Theory]
    [InlineData("ask", false)]
    [InlineData("vault", true)]
    public async Task Every_reconnect_path_reloads_credentials_and_keeps_session_quality(
        string latestStore,
        bool withoutReservation)
    {
        var id = Guid.NewGuid();
        var stale = ConnectionProfile.Create(id, "Mac", "host", 5900, "user")
            .WithCredential(CredentialReference.Create("windows", "profile/mac"));
        var desired = stale.WithQualityProfile(QualityProfile.Smooth)
            .WithFrameRefreshPolicy(FrameRefreshPolicy.Fixed(90));
        var persisted = ConnectionProfile.Create(id, "Renamed", "latest-host", 5901, "latest-user")
            .WithCredential(CredentialReference.Create(latestStore, "profile/mac"));
        ConnectionProfile? observed = null;
        var request = new RemoteSessionReconnectRequest(
            stale,
            (deviceId, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal(id, deviceId);
                return Task.FromResult<ConnectionProfile?>(persisted);
            },
            (profile, _) =>
            {
                observed = profile;
                return Task.CompletedTask;
            });
        request.Capture(desired);

        if (withoutReservation)
        {
            await request.InvokeWithoutReservationAsync(CancellationToken.None);
        }
        else
        {
            await request.InvokeAsync(CancellationToken.None);
        }

        Assert.NotNull(observed);
        Assert.Equal(latestStore, observed.CredentialReference!.Store);
        Assert.Equal("latest-host", observed.Host);
        Assert.Equal(desired.Quality, observed.Quality);
        Assert.Equal(FrameRefreshPolicy.Fixed(90), observed.FrameRefreshPolicy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_failed_profile_stops_reconnect_with_stable_error(bool failRead)
    {
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user");
        var reconnectCalls = 0;
        var request = new RemoteSessionReconnectRequest(
            profile,
            (_, _) => failRead
                ? Task.FromException<ConnectionProfile?>(new IOException("database unavailable"))
                : Task.FromResult<ConnectionProfile?>(null),
            (_, _) =>
            {
                reconnectCalls++;
                return Task.CompletedTask;
            });

        var failure = await Assert.ThrowsAsync<ReconnectFailureException>(() =>
            request.InvokeAsync(CancellationToken.None));

        Assert.Equal("RECONNECT_PROFILE_UNAVAILABLE", failure.Error.Code);
        Assert.Equal(0, reconnectCalls);
    }

    [Fact]
    public async Task Cancellation_during_profile_reload_is_propagated_without_reconnect()
    {
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var request = new RemoteSessionReconnectRequest(
            profile,
            (_, token) => Task.FromCanceled<ConnectionProfile?>(token),
            (_, _) => Task.FromException(new InvalidOperationException("must not reconnect")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            request.InvokeAsync(cancellation.Token));
    }
}
