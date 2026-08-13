using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Application.Quality;
using WinARD.Desktop.Services;
using WinARD.Domain.Connections;
using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Services;

public sealed class ConnectionSessionControllerTests
{
    [Fact]
    public async Task HostKeyPromptServiceForwardsOnlySanitizedRequest()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var request = new SshHostKeyPromptRequest(
            endpoint, "ssh-ed25519", "SHA256:new", "SHA256:old", IsChanged: true);
        var sut = new SshHostKeyPromptService();
        SshHostKeyPromptRequest? received = null;
        sut.SetHandler((value, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            received = value;
            return ValueTask.FromResult(SshHostKeyPromptDecision.Replace);
        });

        var result = await sut.PromptAsync(request, CancellationToken.None);

        Assert.Equal(SshHostKeyPromptDecision.Replace, result);
        Assert.Equal(request, received);
    }

    [Fact]
    public async Task SuccessfulConnectOwnsSessionUntilDisconnectAndEnforcesSingleSession()
    {
        var transport = new TrackingTransport();
        var client = new TrackingClient();
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(client),
            new ErrorMapper());
        var coordinator = new ActiveSessionCoordinator();
        var prompt = new Prompt(SshHostKeyPromptDecision.Cancel);
        await using var sut = new ConnectionSessionController(
            new ConnectionAttemptWorkflow(handler, prompt),
            coordinator,
            new Repository());

        await sut.ConnectAsync(Profile(), CancellationToken.None);

        Assert.True(sut.IsConnected);
        Assert.False(client.Disposed);
        await Assert.ThrowsAsync<SessionAlreadyActiveException>(async () =>
        {
            await using var lease = await coordinator.AcquireAsync();
        });

        await sut.DisconnectAsync(CancellationToken.None);

        Assert.False(sut.IsConnected);
        Assert.True(client.Disposed);
        await using var released = await coordinator.AcquireAsync();
    }

    [Fact]
    public async Task Transfer_moves_session_and_lease_to_window_ownership_until_closed()
    {
        var client = new TrackingClient();
        var coordinator = new ActiveSessionCoordinator();
        await using var sut = Controller(client, coordinator);
        await sut.ConnectAsync(Profile(), CancellationToken.None);

        var ownership = sut.TransferConnectedSession();

        Assert.True(sut.IsConnected);
        Assert.NotNull(ownership.Session);
        Assert.Throws<InvalidOperationException>(() => sut.TransferConnectedSession());
        await Assert.ThrowsAsync<SessionAlreadyActiveException>(async () =>
        {
            await using var lease = await coordinator.AcquireAsync();
        });

        await ownership.DisposeAsync();

        Assert.False(sut.IsConnected);
        Assert.True(client.Disposed);
        await using var released = await coordinator.AcquireAsync();
    }

    [Fact]
    public async Task Connected_refresh_policy_update_persists_and_updates_transferred_ownership()
    {
        var repository = new RecordingRepository();
        await using var sut = Controller(
            new TrackingTransport(),
            new Prompt(SshHostKeyPromptDecision.Cancel),
            repository);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        await using var ownership = sut.TransferConnectedSession();
        ConnectionProfile? published = null;
        sut.ProfileUpdated += profile => published = profile;

        var updated = await sut.UpdateConnectedFrameRefreshPolicyAsync(
            FrameRefreshPolicy.Fixed(90),
            CancellationToken.None);

        Assert.Same(updated, ownership.Profile);
        Assert.Same(updated, published);
        Assert.Equal(FrameRefreshPolicy.Fixed(90), updated.FrameRefreshPolicy);
        Assert.Equal([updated], repository.Saved);
    }

    [Fact]
    public async Task Reconnect_reservation_holds_one_lease_between_sessions_and_transfers_it_on_success()
    {
        var coordinator = new ActiveSessionCoordinator();
        await using var first = Controller(new TrackingClient(), coordinator);
        await first.ConnectAsync(Profile(), CancellationToken.None);
        var ownership = first.TransferConnectedSession();
        await using var reservation = ownership.ReserveForReconnect();

        await ownership.DisposeAsync();
        await Assert.ThrowsAsync<SessionAlreadyActiveException>(async () =>
        {
            await using var competing = await coordinator.AcquireAsync();
        });

        await using var retry = Controller(new TrackingClient(), coordinator);
        await retry.ReconnectAsync(Profile(), reservation, cancellationToken: default);
        await using var replacement = retry.TransferConnectedSession();
        await reservation.DisposeAsync();
        await Assert.ThrowsAsync<SessionAlreadyActiveException>(async () =>
        {
            await using var competing = await coordinator.AcquireAsync();
        });

        await replacement.DisposeAsync();
        await using var released = await coordinator.AcquireAsync();
    }

    [Fact]
    public async Task Failed_reconnect_keeps_reservation_until_terminal_cancel_releases_it()
    {
        var coordinator = new ActiveSessionCoordinator();
        await using var first = Controller(new TrackingClient(), coordinator);
        await first.ConnectAsync(Profile(), CancellationToken.None);
        var ownership = first.TransferConnectedSession();
        await using var reservation = ownership.ReserveForReconnect();
        await ownership.DisposeAsync();

        await using var retry = Controller(new FailingClient(), coordinator);
        await Assert.ThrowsAsync<ConnectionFailedException>(
            () => retry.ReconnectAsync(Profile(), reservation, cancellationToken: default));
        await Assert.ThrowsAsync<SessionAlreadyActiveException>(async () =>
        {
            await using var competing = await coordinator.AcquireAsync();
        });

        await reservation.DisposeAsync();
        await using var released = await coordinator.AcquireAsync();
    }

    [Fact]
    public async Task Terminal_reconnect_failure_releases_reservation_for_a_fresh_manual_acquire()
    {
        var coordinator = new ActiveSessionCoordinator();
        await using var first = Controller(new TrackingClient(), coordinator);
        await first.ConnectAsync(Profile(), default);
        var ownership = first.TransferConnectedSession();
        var reservation = ownership.ReserveForReconnect();
        await ownership.DisposeAsync();

        await reservation.DisposeAsync();

        await using var manual = Controller(new TrackingClient(), coordinator);
        await manual.ConnectAsync(Profile(), default);
        await using var replacement = manual.TransferConnectedSession();
    }

    [Fact]
    public async Task Reservation_transfer_and_cancel_have_exactly_one_lease_winner()
    {
        var coordinator = new ActiveSessionCoordinator();
        await using var first = Controller(new TrackingClient(), coordinator);
        await first.ConnectAsync(Profile(), default);
        var ownership = first.TransferConnectedSession();
        var reservation = ownership.ReserveForReconnect();
        await ownership.DisposeAsync();

        ActiveSessionCoordinator.ActiveSessionLease? transferred = null;
        var dispose = Task.Run(async () => await reservation.DisposeAsync());
        var transfer = Task.Run(() =>
        {
            try { transferred = reservation.TransferLease(); }
            catch (ObjectDisposedException) { }
        });
        await Task.WhenAll(dispose, transfer);
        if (transferred is not null)
        {
            await transferred.DisposeAsync();
        }

        await using var released = await coordinator.AcquireAsync();
    }

    [Fact]
    public async Task Connected_quality_profile_update_persists_updates_ownership_and_publishes()
    {
        var repository = new RecordingRepository();
        await using var sut = Controller(
            new TrackingTransport(),
            new Prompt(SshHostKeyPromptDecision.Cancel),
            repository);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        await using var ownership = sut.TransferConnectedSession();
        ConnectionProfile? published = null;
        sut.ProfileUpdated += profile => published = profile;

        var updated = await sut.UpdateConnectedQualityProfileAsync(
            QualityProfile.Smooth,
            CancellationToken.None);

        Assert.Same(updated, ownership.Profile);
        Assert.Same(updated, published);
        Assert.Same(QualityProfile.Smooth, updated.Quality);
        Assert.Equal([updated], repository.Saved);
    }

    [Fact]
    public async Task Failed_connected_refresh_policy_persistence_keeps_transferred_ownership_profile()
    {
        var repository = new ThrowingRepository();
        await using var sut = Controller(
            new TrackingTransport(),
            new Prompt(SshHostKeyPromptDecision.Cancel),
            repository);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        await using var ownership = sut.TransferConnectedSession();
        var previous = ownership.Profile;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateConnectedFrameRefreshPolicyAsync(
                FrameRefreshPolicy.Fixed(90),
                CancellationToken.None));

        Assert.Same(previous, ownership.Profile);
        Assert.Equal(FrameRefreshPolicy.Automatic, ownership.Profile.FrameRefreshPolicy);
        Assert.Equal(FrameRefreshPolicy.Fixed(90), repository.Attempted!.FrameRefreshPolicy);
    }

    [Fact]
    public async Task Refresh_policy_update_ignores_throwing_subscriber_and_notifies_later_subscribers()
    {
        var repository = new RecordingRepository();
        await using var sut = Controller(
            new TrackingTransport(),
            new Prompt(SshHostKeyPromptDecision.Cancel),
            repository);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        await using var ownership = sut.TransferConnectedSession();
        var published = new List<ConnectionProfile>();
        sut.ProfileUpdated += _ => throw new InvalidOperationException("subscriber failure");
        sut.ProfileUpdated += published.Add;

        var updated = await sut.UpdateConnectedFrameRefreshPolicyAsync(
            FrameRefreshPolicy.Fixed(90),
            CancellationToken.None);

        Assert.Same(updated, ownership.Profile);
        Assert.Equal([updated], published);
    }

    [Fact]
    public async Task Refresh_policy_update_allows_profile_updated_subscriber_to_reenter_controller()
    {
        var repository = new RecordingRepository();
        await using var sut = Controller(
            new TrackingTransport(),
            new Prompt(SshHostKeyPromptDecision.Cancel),
            repository);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        await using var ownership = sut.TransferConnectedSession();
        var reentered = false;
        Task<ConnectionProfile>? nestedUpdate = null;
        sut.ProfileUpdated += _ =>
        {
            if (reentered)
            {
                return;
            }

            reentered = true;
            nestedUpdate = sut.UpdateConnectedFrameRefreshPolicyAsync(
                FrameRefreshPolicy.Fixed(105),
                CancellationToken.None);
            Assert.True(nestedUpdate.IsCompletedSuccessfully);
        };

        await sut.UpdateConnectedFrameRefreshPolicyAsync(
            FrameRefreshPolicy.Fixed(90),
            CancellationToken.None);
        await nestedUpdate!.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(FrameRefreshPolicy.Fixed(105), ownership.Profile.FrameRefreshPolicy);
        Assert.Equal(
            [FrameRefreshPolicy.Fixed(90), FrameRefreshPolicy.Fixed(105)],
            repository.Saved.Select(profile => profile.FrameRefreshPolicy));
    }

    [Fact]
    public async Task Refresh_policy_update_that_enters_save_before_dispose_commits_before_disposal()
    {
        var repository = new BlockingFirstSaveRepository();
        var client = new TrackingClient();
        await using var sut = Controller(client, new ActiveSessionCoordinator(), repository);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        var ownership = sut.TransferConnectedSession();
        var published = new List<ConnectionProfile>();
        sut.ProfileUpdated += published.Add;

        var update = sut.UpdateConnectedFrameRefreshPolicyAsync(
            FrameRefreshPolicy.Fixed(90),
            CancellationToken.None);
        await repository.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var dispose = ownership.DisposeAsync().AsTask();
        var disposeCompletedBeforeCommit = dispose.IsCompleted;

        repository.AllowFirstSave.TrySetResult();
        var updated = await update.WaitAsync(TimeSpan.FromSeconds(1));
        await dispose.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(disposeCompletedBeforeCommit);
        Assert.Same(updated, ownership.Profile);
        Assert.Equal([updated], repository.Saved);
        Assert.Equal([updated], published);
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task Closing_cancellation_unblocks_infinite_save_before_ownership_disposal()
    {
        var repository = new InfiniteCancelableSaveRepository();
        var client = new TrackingClient();
        await using var sut = Controller(client, new ActiveSessionCoordinator(), repository);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        var ownership = sut.TransferConnectedSession();
        var published = new List<ConnectionProfile>();
        sut.ProfileUpdated += published.Add;
        using var saveLifetime = new CancellationTokenSource();

        var update = sut.UpdateConnectedFrameRefreshPolicyAsync(
            FrameRefreshPolicy.Fixed(90),
            saveLifetime.Token);
        await repository.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        saveLifetime.Cancel();
        var dispose = ownership.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        await dispose.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(repository.SaveCanceled.Task.IsCompletedSuccessfully);
        Assert.Equal(1, repository.SaveCalls);
        Assert.Empty(published);
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task Dispose_that_starts_before_refresh_policy_update_rejects_without_saving_or_publishing()
    {
        var repository = new RecordingRepository();
        var client = new BlockingDisposeClient();
        await using var sut = Controller(client, new ActiveSessionCoordinator(), repository);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        var ownership = sut.TransferConnectedSession();
        var published = new List<ConnectionProfile>();
        sut.ProfileUpdated += published.Add;

        var dispose = ownership.DisposeAsync().AsTask();
        await client.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Exception? updateFailure = null;
        try
        {
            _ = await sut.UpdateConnectedFrameRefreshPolicyAsync(
                FrameRefreshPolicy.Fixed(90),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            updateFailure = exception;
        }

        client.AllowDispose.TrySetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<ObjectDisposedException>(updateFailure);
        Assert.Empty(repository.Saved);
        Assert.Empty(published);
    }

    [Fact]
    public async Task Concurrent_refresh_policy_updates_commit_in_gate_order()
    {
        var repository = new BlockingFirstSaveRepository();
        await using var sut = Controller(
            new TrackingTransport(),
            new Prompt(SshHostKeyPromptDecision.Cancel),
            repository);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        await using var ownership = sut.TransferConnectedSession();

        var first = sut.UpdateConnectedFrameRefreshPolicyAsync(
            FrameRefreshPolicy.Fixed(30),
            CancellationToken.None);
        await repository.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = sut.UpdateConnectedFrameRefreshPolicyAsync(
            FrameRefreshPolicy.Fixed(90),
            CancellationToken.None);

        repository.AllowFirstSave.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            [FrameRefreshPolicy.Fixed(30), FrameRefreshPolicy.Fixed(90)],
            repository.Saved.Select(profile => profile.FrameRefreshPolicy));
        Assert.Equal(FrameRefreshPolicy.Fixed(90), ownership.Profile.FrameRefreshPolicy);
    }

    [Fact]
    public async Task Canceled_refresh_policy_update_waiting_for_gate_does_not_save()
    {
        var repository = new BlockingFirstSaveRepository();
        await using var sut = Controller(
            new TrackingTransport(),
            new Prompt(SshHostKeyPromptDecision.Cancel),
            repository);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        await using var ownership = sut.TransferConnectedSession();

        var first = sut.UpdateConnectedFrameRefreshPolicyAsync(
            FrameRefreshPolicy.Fixed(30),
            CancellationToken.None);
        await repository.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        var canceled = sut.UpdateConnectedFrameRefreshPolicyAsync(
            FrameRefreshPolicy.Fixed(90),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        repository.AllowFirstSave.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Single(repository.Saved);
        Assert.Equal(FrameRefreshPolicy.Fixed(30), ownership.Profile.FrameRefreshPolicy);
    }

    [Fact]
    public async Task Controller_and_window_concurrent_close_dispose_session_once()
    {
        var client = new TrackingClient();
        var coordinator = new ActiveSessionCoordinator();
        var sut = Controller(client, coordinator);
        await sut.ConnectAsync(Profile(), CancellationToken.None);
        var ownership = sut.TransferConnectedSession();

        await Task.WhenAll(
            sut.DisposeAsync().AsTask(),
            ownership.DisposeAsync().AsTask());

        Assert.Equal(1, client.DisposeCount);
        await using var released = await coordinator.AcquireAsync();
    }

    [Fact]
    public async Task Concurrent_controller_dispose_calls_wait_for_the_same_disposal()
    {
        var client = new BlockingDisposeClient();
        var sut = Controller(client, new ActiveSessionCoordinator());
        await sut.ConnectAsync(Profile(), CancellationToken.None);

        var first = sut.DisposeAsync().AsTask();
        await client.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = sut.DisposeAsync().AsTask();

        Assert.False(second.IsCompleted);
        client.AllowDispose.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, client.DisposeCount);
    }

    [Theory]
    [InlineData(false, SshHostKeyPromptDecision.Trust)]
    [InlineData(true, SshHostKeyPromptDecision.Replace)]
    public async Task AcceptedHostKeyDecisionPersistsCandidateAndRetries(
        bool changed,
        SshHostKeyPromptDecision decision)
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "AQIDBA==");
        var profile = SshProfileFor(endpoint);
        if (changed)
        {
            var old = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "CQkJCQ==").ToPin();
            profile = profile.WithSsh(profile.SshProfile!.WithHostKeyPin(old));
        }

        var transport = new HostKeyThenSuccessTransport(candidate, changed);
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(new TrackingClient()),
            new ErrorMapper());
        var repository = new Repository();
        var prompt = new Prompt(decision);
        await using var sut = new ConnectionSessionController(
            new ConnectionAttemptWorkflow(handler, prompt),
            new ActiveSessionCoordinator(),
            repository);

        await sut.ConnectAsync(profile, CancellationToken.None);

        Assert.Equal(2, transport.Attempts);
        Assert.Equal(candidate.ToPin(), repository.Saved!.SshProfile!.HostKeyPin);
        Assert.Equal(endpoint, prompt.Request!.Endpoint);
        Assert.Equal(candidate.Algorithm, prompt.Request.Algorithm);
        Assert.Equal(candidate.Fingerprint, prompt.Request.NewFingerprint);
        Assert.Equal(changed, prompt.Request.IsChanged);

        await using var ownership = sut.TransferConnectedSession();
        Assert.Equal(candidate.ToPin(), ownership.Profile.SshProfile!.HostKeyPin);
    }

    [Fact]
    public async Task Accepted_host_key_profile_publication_is_reentrant_and_isolates_subscriber_failures()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "AQIDBA==");
        var repository = new RecordingRepository();
        await using var sut = Controller(
            new HostKeyUntilPinnedTransport(candidate),
            new Prompt(SshHostKeyPromptDecision.Trust),
            repository);
        Task? reentrantDisconnect = null;
        var published = new List<ConnectionProfile>();
        sut.ProfileUpdated += _ =>
        {
            reentrantDisconnect = sut.DisconnectAsync(CancellationToken.None);
            Assert.True(reentrantDisconnect.IsCompletedSuccessfully);
        };
        sut.ProfileUpdated += _ => throw new InvalidOperationException("subscriber failure");
        sut.ProfileUpdated += published.Add;

        await sut.ConnectAsync(SshProfileFor(endpoint), CancellationToken.None);
        await reentrantDisconnect!.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(sut.IsConnected);
        Assert.Single(repository.Saved);
        Assert.Equal(repository.Saved, published);
    }

    [Theory]
    [InlineData(false, SshHostKeyPromptDecision.Trust)]
    [InlineData(true, SshHostKeyPromptDecision.Replace)]
    public async Task TransferredEffectiveProfileRetriesWithoutPromptingForAcceptedHostKeyAgain(
        bool changed,
        SshHostKeyPromptDecision decision)
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "AQIDBA==");
        var profile = SshProfileFor(endpoint);
        if (changed)
        {
            var old = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "CQkJCQ==").ToPin();
            profile = profile.WithSsh(profile.SshProfile!.WithHostKeyPin(old));
        }

        var firstTransport = new HostKeyUntilPinnedTransport(candidate);
        var firstPrompt = new CountingPrompt(decision);
        var repository = new Repository();
        await using var first = Controller(firstTransport, firstPrompt, repository);

        await first.ConnectAsync(profile, CancellationToken.None);
        await using var ownership = first.TransferConnectedSession();
        var retryProfile = ownership.Profile;
        await ownership.DisposeAsync();

        var retryTransport = new HostKeyUntilPinnedTransport(candidate);
        var retryPrompt = new CountingPrompt(SshHostKeyPromptDecision.Cancel);
        await using var retry = Controller(retryTransport, retryPrompt, repository);
        await retry.ConnectAsync(retryProfile, CancellationToken.None);

        Assert.Equal(candidate.ToPin(), retryProfile.SshProfile!.HostKeyPin);
        Assert.Equal(1, firstPrompt.Count);
        Assert.Equal(1, retryTransport.Attempts);
        Assert.Equal(0, retryPrompt.Count);
    }

    [Fact]
    public async Task FailedAcceptedHostKeyPersistenceDoesNotPublishOrTransferUpdatedProfile()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "AQIDBA==");
        var profile = SshProfileFor(endpoint);
        var repository = new ThrowingRepository();
        await using var sut = Controller(
            new HostKeyUntilPinnedTransport(candidate),
            new CountingPrompt(SshHostKeyPromptDecision.Trust),
            repository);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ConnectAsync(profile, CancellationToken.None));

        Assert.False(sut.IsConnected);
        Assert.Throws<InvalidOperationException>(() => sut.TransferConnectedSession());
        Assert.Equal(candidate.ToPin(), repository.Attempted!.SshProfile!.HostKeyPin);
        Assert.Null(repository.Published);
    }

    [Theory]
    [InlineData(false, SshHostKeyPromptDecision.Trust)]
    [InlineData(true, SshHostKeyPromptDecision.Replace)]
    public async Task SharedAttemptWorkflowReturnsAcceptedPinAndRetriesExactlyOnce(
        bool changed,
        SshHostKeyPromptDecision decision)
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(
            endpoint,
            "ssh-ed25519",
            "AQIDBA==");
        var profile = SshProfileFor(endpoint);
        if (changed)
        {
            var old = SshHostKeyVerifier.CreateCandidate(
                endpoint,
                "ssh-ed25519",
                "CQkJCQ==").ToPin();
            profile = profile.WithSsh(profile.SshProfile!.WithHostKeyPin(old));
        }

        var transport = new HostKeyThenSuccessTransport(candidate, changed);
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(new TrackingClient()),
            new ErrorMapper());
        var prompt = new Prompt(decision);
        var acceptedProfiles = new List<ConnectionProfile>();
        var sut = new ConnectionAttemptWorkflow(handler, prompt);

        var outcome = await sut.AttemptAsync(
            profile,
            stageChanged: null,
            (updated, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                acceptedProfiles.Add(updated);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.NotNull(outcome.Result.Session);
        Assert.Equal(2, transport.Attempts);
        Assert.Single(acceptedProfiles);
        Assert.Equal(candidate.ToPin(), outcome.Profile.SshProfile!.HostKeyPin);
        Assert.Equal(candidate.ToPin(), acceptedProfiles[0].SshProfile!.HostKeyPin);
        await outcome.Result.Session.DisposeAsync();
    }

    [Theory]
    [InlineData(false, SshHostKeyPromptDecision.Cancel)]
    [InlineData(false, SshHostKeyPromptDecision.Replace)]
    [InlineData(true, SshHostKeyPromptDecision.Cancel)]
    [InlineData(true, SshHostKeyPromptDecision.Trust)]
    public async Task SharedAttemptWorkflowDoesNotRetryUnacceptedHostKeyDecision(
        bool changed,
        SshHostKeyPromptDecision decision)
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(
            endpoint,
            "ssh-ed25519",
            "AQIDBA==");
        var profile = SshProfileFor(endpoint);
        if (changed)
        {
            var old = SshHostKeyVerifier.CreateCandidate(
                endpoint,
                "ssh-ed25519",
                "CQkJCQ==").ToPin();
            profile = profile.WithSsh(profile.SshProfile!.WithHostKeyPin(old));
        }

        var transport = new HostKeyThenSuccessTransport(candidate, changed);
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(new TrackingClient()),
            new ErrorMapper());
        var accepted = 0;
        var sut = new ConnectionAttemptWorkflow(handler, new Prompt(decision));

        var outcome = await sut.AttemptAsync(
            profile,
            stageChanged: null,
            (_, _) =>
            {
                accepted++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Null(outcome.Result.Session);
        Assert.Equal(1, transport.Attempts);
        Assert.Equal(0, accepted);
        Assert.Equal(profile, outcome.Profile);
    }

    [Fact]
    public async Task SharedAttemptWorkflowRetriesAcceptedHostKeyOnlyOnce()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(
            endpoint,
            "ssh-ed25519",
            "AQIDBA==");
        var profile = SshProfileFor(endpoint);
        var transport = new AlwaysUnknownHostKeyTransport(candidate);
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(new TrackingClient()),
            new ErrorMapper());
        var sut = new ConnectionAttemptWorkflow(
            handler,
            new Prompt(SshHostKeyPromptDecision.Trust));

        var outcome = await sut.AttemptAsync(
            profile,
            stageChanged: null,
            acceptedHostKey: null,
            CancellationToken.None);

        Assert.Null(outcome.Result.Session);
        Assert.Equal(2, transport.Attempts);
        Assert.Equal(candidate.ToPin(), outcome.Profile.SshProfile!.HostKeyPin);
    }

    [Fact]
    public async Task ChangedHostKeyCancelReturnsSanitizedContextAndPreauthorizedReplaceRetriesOnce()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "AQIDBA==");
        var old = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "CQkJCQ==").ToPin();
        var profile = SshProfileFor(endpoint).WithSsh(
            SshProfileFor(endpoint).SshProfile!.WithHostKeyPin(old));
        var transport = new HostKeyUntilPinnedTransport(candidate);
        var handler = new ConnectDeviceHandler(
            transport,
            new FixedSecretProvider(),
            new FixedClientFactory(new TrackingClient()),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(
            handler,
            new Prompt(SshHostKeyPromptDecision.Cancel));

        var canceled = await workflow.AttemptAsync(
            profile,
            stageChanged: null,
            acceptedHostKey: null,
            CancellationToken.None);

        Assert.NotNull(canceled.HostKeyFailure);
        Assert.Equal(old.Fingerprint, canceled.HostKeyFailure.PreviousFingerprint);
        Assert.Equal(candidate.Fingerprint, canceled.HostKeyFailure.NewFingerprint);
        Assert.True(canceled.HostKeyFailure.IsChanged);
        Assert.Equal(1, transport.Attempts);

        var persisted = new List<ConnectionProfile>();
        var replaced = await workflow.AttemptAsync(
            profile,
            stageChanged: null,
            (updated, _) =>
            {
                persisted.Add(updated);
                return Task.CompletedTask;
            },
            new PreauthorizedHostKeyPrompt(canceled.HostKeyFailure),
            CancellationToken.None);

        Assert.NotNull(replaced.Result.Session);
        Assert.Equal(3, transport.Attempts);
        Assert.Single(persisted);
        Assert.Equal(candidate.ToPin(), persisted[0].SshProfile!.HostKeyPin);
        await replaced.Result.Session.DisposeAsync();
    }

    [Fact]
    public async Task PreauthorizedHostKeyDecisionCanOnlyBeConsumedOnce()
    {
        var request = new SshHostKeyPromptRequest(
            new SshHostKeyEndpoint("jump.local", 22),
            "ssh-ed25519",
            "SHA256:new",
            "SHA256:old",
            IsChanged: true);
        var prompt = new PreauthorizedHostKeyPrompt(request);

        Assert.Equal(
            SshHostKeyPromptDecision.Replace,
            await prompt.PromptAsync(request, CancellationToken.None));
        Assert.Equal(
            SshHostKeyPromptDecision.Cancel,
            await prompt.PromptAsync(request, CancellationToken.None));
    }

    private static ConnectionProfile Profile() => ConnectionProfile.Create(
        Guid.NewGuid(), "Studio", "studio.local", 5900, "operator");

    private static ConnectionSessionController Controller(
        IRfbClient client,
        ActiveSessionCoordinator coordinator) =>
        Controller(client, coordinator, new Repository());

    private static ConnectionSessionController Controller(
        IRfbClient client,
        ActiveSessionCoordinator coordinator,
        IDeviceRepository repository) =>
        new(
            new ConnectionAttemptWorkflow(
                new ConnectDeviceHandler(
                    new TrackingTransport(),
                    new FixedSecretProvider(),
                    new FixedClientFactory(client),
                    new ErrorMapper()),
                new Prompt(SshHostKeyPromptDecision.Cancel)),
            coordinator,
            repository);

    private static ConnectionSessionController Controller(
        IRemoteTransportFactory transport,
        ISshHostKeyPrompt prompt,
        IDeviceRepository repository) =>
        new(
            new ConnectionAttemptWorkflow(
                new ConnectDeviceHandler(
                    transport,
                    new FixedSecretProvider(),
                    new FixedClientFactory(new TrackingClient()),
                    new ErrorMapper()),
                prompt),
            new ActiveSessionCoordinator(),
            repository);

    private static ConnectionProfile SshProfileFor(SshHostKeyEndpoint endpoint) =>
        Profile().WithSsh(SshProfile.Create(
            endpoint.Host,
            endpoint.Port,
            "ssh-user",
            privateKeyPath: null,
            targetHost: "studio.local",
            targetPort: 5900,
            WinARD.Domain.Security.CredentialReference.Create("ask", "profile/id/ssh-password"),
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null));

    private sealed class TrackingTransport : IRemoteTransportFactory
    {
        public Task<TransportConnection> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult(new TransportConnection(new MemoryStream(), new EndPointDescription(profile.Host, profile.Port)));
    }

    private sealed class HostKeyThenSuccessTransport(
        SshHostKeyCandidate candidate,
        bool changed) : IRemoteTransportFactory
    {
        public int Attempts { get; private set; }

        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            if (Attempts == 1)
            {
                var verification = SshHostKeyVerifier.Verify(
                    candidate,
                    changed ? profile.SshProfile!.HostKeyPin : null);
                throw changed
                    ? new SshHostKeyChangedException(verification)
                    : new SshHostKeyUnknownException(verification);
            }

            return Task.FromResult(new TransportConnection(
                new MemoryStream(),
                new EndPointDescription(profile.Host, profile.Port)));
        }
    }

    private sealed class AlwaysUnknownHostKeyTransport(
        SshHostKeyCandidate candidate) : IRemoteTransportFactory
    {
        public int Attempts { get; private set; }

        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            var verification = SshHostKeyVerifier.Verify(candidate, null);
            throw new SshHostKeyUnknownException(verification);
        }
    }

    private sealed class HostKeyUntilPinnedTransport(
        SshHostKeyCandidate candidate) : IRemoteTransportFactory
    {
        public int Attempts { get; private set; }

        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            var verification = SshHostKeyVerifier.Verify(candidate, profile.SshProfile?.HostKeyPin);
            return verification.Status switch
            {
                SshHostKeyStatus.Trusted => Task.FromResult(new TransportConnection(
                    new MemoryStream(),
                    new EndPointDescription(profile.Host, profile.Port))),
                SshHostKeyStatus.Changed => Task.FromException<TransportConnection>(
                    new SshHostKeyChangedException(verification)),
                _ => Task.FromException<TransportConnection>(new SshHostKeyUnknownException(verification)),
            };
        }
    }

    private sealed class Repository : IDeviceRepository
    {
        public ConnectionProfile? Saved { get; private set; }

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved = profile;
            return Task.CompletedTask;
        }

        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Saved?.Id == id ? Saved : null);

        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(Saved is null ? [] : [Saved]);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRepository : IDeviceRepository
    {
        public List<ConnectionProfile> Saved { get; } = [];

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved.Add(profile);
            return Task.CompletedTask;
        }

        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Saved.LastOrDefault(profile => profile.Id == id));

        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(Saved);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingFirstSaveRepository : IDeviceRepository
    {
        public TaskCompletionSource SaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowFirstSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ConnectionProfile> Saved { get; } = [];

        public async Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            Saved.Add(profile);
            if (Saved.Count == 1)
            {
                SaveStarted.TrySetResult();
                await AllowFirstSave.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Saved.LastOrDefault(profile => profile.Id == id));

        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(Saved);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InfiniteCancelableSaveRepository : IDeviceRepository
    {
        private int _saveCalls;

        public TaskCompletionSource SaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SaveCalls => Volatile.Read(ref _saveCalls);

        public async Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _saveCalls);
            SaveStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SaveCanceled.TrySetResult();
                throw;
            }
        }

        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<ConnectionProfile?>(null);

        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>([]);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Prompt(SshHostKeyPromptDecision decision) : ISshHostKeyPrompt
    {
        public SshHostKeyPromptRequest? Request { get; private set; }

        public ValueTask<SshHostKeyPromptDecision> PromptAsync(
            SshHostKeyPromptRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class CountingPrompt(SshHostKeyPromptDecision decision) : ISshHostKeyPrompt
    {
        public int Count { get; private set; }

        public ValueTask<SshHostKeyPromptDecision> PromptAsync(
            SshHostKeyPromptRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class ThrowingRepository : IDeviceRepository
    {
        public ConnectionProfile? Attempted { get; private set; }

        public ConnectionProfile? Published { get; private set; }

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempted = profile;
            throw new InvalidOperationException("CAS conflict");
        }

        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Published?.Id == id ? Published : null);

        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(Published is null ? [] : [Published]);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedSecretProvider : IConnectionSecretProvider
    {
        public ValueTask<ISecret> GetSecretAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ISecret>(new Secret());
    }

    private sealed class FixedClientFactory(IRfbClient client) : IRfbClientFactory
    {
        public IRfbClient Create(Stream stream) => client;
    }

    private sealed class TrackingClient : IRfbClient
    {
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask ConfigureBootstrapAsync(QualityBootstrapSettings settings, QualityBootstrapAttempt attempt, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) => ValueTask.FromResult<RemoteServerMessage>(Frame());
        public void ConfirmBootstrap(RemoteFramebufferSize framebufferSize) { }
        public ValueTask DisposeAsync() { Disposed = true; DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class BlockingDisposeClient : IRfbClient
    {
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }

        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask ConfigureBootstrapAsync(QualityBootstrapSettings settings, QualityBootstrapAttempt attempt, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) => ValueTask.FromResult<RemoteServerMessage>(Frame());
        public void ConfirmBootstrap(RemoteFramebufferSize framebufferSize) { }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            await AllowDispose.Task;
        }
    }

    private sealed class FailingClient : IRfbClient
    {
        public Task NegotiateAsync(CancellationToken cancellationToken) =>
            Task.FromException(new IOException());
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static RemoteFramebufferMessage Frame() => new(
        new RemoteFramebufferSize(1, 1), [0, 0, 0, 255], 4,
        [new RemoteRectangle(0, 0, 1, 1)]);

    private sealed class Secret : ISecret
    {
        public int Length => 1;
        public void CopyTo(Span<byte> destination) => destination[0] = 1;
        public ISecret Clone() => new Secret();
        public void Dispose() { }
    }
}
