using System.Collections.Concurrent;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Services;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class AdaptiveQualitySessionIntegrationTests
{
    [Fact]
    public void Runtime_default_bootstrap_state_preserves_legacy_bgra32_compatibility()
    {
        IRemoteSessionRuntime runtime = new BlockingRuntime();

        Assert.Same(QualityBootstrapState.LegacyBgra32, runtime.BootstrapState);
    }

    [Fact]
    public async Task Snapshot_uses_bootstrap_pixel_format_and_each_frames_observed_primary_encoding()
    {
        var events = new ConcurrentQueue<string>();
        var bootstrap = new QualityBootstrapState(
            QualityBootstrapAttempt.Preferred,
            new QualityBootstrapSettings(
                RemotePixelFormatKind.Rgb565,
                [6, 16, 0, 1, -239, -223],
                QualityBootstrapReason.UserColor16));
        var runtime = new TransitionRuntime(
            events,
            frameCount: 2,
            bootstrapState: bootstrap,
            gateSecondFrame: true,
            frameEncodings: [16, 6]);
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Fixed(30),
            allowAutomaticGrayscale: false,
            colorLocked: true);
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(events),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            profile);

        await viewModel.StartAsync(default);
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(profile, viewModel.QualityPresentationSnapshot.Profile);
        Assert.Equal(RemotePixelFormatKind.Rgb565, viewModel.QualityPresentationSnapshot.Actual?.PixelFormat);
        Assert.Equal(QualityActualEncoding.Zrle, viewModel.QualityPresentationSnapshot.Actual?.Encoding);

        runtime.ReleaseSecondFrame.TrySetResult();
        await runtime.ThirdRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(QualityActualEncoding.Zlib, viewModel.QualityPresentationSnapshot.Actual?.Encoding);
    }

    [Fact]
    public async Task Metadata_only_frame_preserves_the_last_observed_primary_encoding()
    {
        var events = new ConcurrentQueue<string>();
        var runtime = new TransitionRuntime(
            events,
            frameCount: 2,
            gateSecondFrame: true,
            frameEncodingCounts:
            [
                new Dictionary<int, int> { [16] = 3 },
                new Dictionary<int, int>
                {
                    [1] = 4,
                    [-239] = 3,
                    [-223] = 2,
                    [1101] = 1,
                    [1103] = 1,
                    [1105] = 1,
                },
            ]);
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(events),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Original);

        await viewModel.StartAsync(default);
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(QualityActualEncoding.Zrle, viewModel.QualityPresentationSnapshot.Actual?.Encoding);

        runtime.ReleaseSecondFrame.TrySetResult();
        await runtime.ThirdRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(QualityActualEncoding.Zrle, viewModel.QualityPresentationSnapshot.Actual?.Encoding);
    }

    [Fact]
    public async Task Profile_snapshot_change_notification_allows_reentrant_read_and_set_without_lock_deadlock()
    {
        var runtime = new BlockingRuntime();
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Original);
        await viewModel.StartAsync(default);
        var initialVersion = viewModel.QualityPresentationVersion;
        var notificationCount = 0;
        var reentered = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(RemoteSessionViewModel.QualityPresentationVersion))
            {
                return;
            }

            Interlocked.Increment(ref notificationCount);
            _ = viewModel.QualityPresentationSnapshot;
            if (Interlocked.CompareExchange(ref reentered, 1, 0) == 0)
            {
                var change = Task.Run(() => viewModel.SetQualityProfile(QualityProfile.Smooth));
                Assert.True(change.Wait(TimeSpan.FromSeconds(2)));
            }
        };

        viewModel.SetQualityProfile(QualityProfile.Balanced);

        Assert.Same(QualityProfile.Smooth, viewModel.QualityPresentationSnapshot.Profile);
        Assert.Equal(initialVersion + 2, viewModel.QualityPresentationVersion);
        Assert.Equal(2, Volatile.Read(ref notificationCount));

        viewModel.SetQualityProfile(QualityProfile.Smooth);

        Assert.Equal(initialVersion + 2, viewModel.QualityPresentationVersion);
        Assert.Equal(2, Volatile.Read(ref notificationCount));
    }

    [Fact]
    public async Task Profile_snapshot_change_notification_is_queued_once_through_the_dispatcher()
    {
        var dispatcher = new QueuedPresentationDispatcher();
        await using var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            dispatcher,
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Original);
        var notifications = 0;
        var notificationThread = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteSessionViewModel.QualityPresentationVersion))
            {
                notifications++;
                notificationThread = Environment.CurrentManagedThreadId;
            }
        };

        viewModel.SetQualityProfile(QualityProfile.Balanced);

        var notificationsBeforeDrain = notifications;
        var queuedBeforeDrain = dispatcher.QueuedCount;

        var drainThread = Environment.CurrentManagedThreadId;
        dispatcher.Drain();

        Assert.Equal(0, notificationsBeforeDrain);
        Assert.Equal(1, queuedBeforeDrain);
        Assert.Equal(1, notifications);
        Assert.Equal(drainThread, notificationThread);
        viewModel.SetQualityProfile(QualityProfile.Balanced);
        Assert.Equal(0, dispatcher.QueuedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_notification_dispatcher_failures_are_observed_without_escaping_sync_setter(
        bool throwSynchronously)
    {
        var dispatcher = new SwitchableFailingPresentationDispatcher(throwSynchronously);
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            dispatcher,
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Original);

        var exception = Record.Exception(() =>
            viewModel.SetQualityProfile(QualityProfile.Balanced));

        Assert.Null(exception);
        Assert.True(SpinWait.SpinUntil(
            () => viewModel.ActiveBestEffortUiObserverCount == 0,
            TimeSpan.FromSeconds(2)));
        dispatcher.Fail = false;
        await viewModel.DisposeAsync();
    }

    [Theory]
    [InlineData(QualityColor.Automatic, QualityColor.Color16, RemotePixelFormatKind.Bgra32, true)]
    [InlineData(QualityColor.Grayscale, QualityColor.Color16, RemotePixelFormatKind.Bgra32, true)]
    [InlineData(QualityColor.Grayscale, QualityColor.Full32, RemotePixelFormatKind.Bgra32, false)]
    [InlineData(QualityColor.Full32, QualityColor.Color16, RemotePixelFormatKind.Bgra32, true)]
    [InlineData(QualityColor.Full32, QualityColor.Automatic, RemotePixelFormatKind.Bgra32, false)]
    public async Task Active_explicit_color_change_sets_pending_only_when_next_differs_from_actual(
        QualityColor oldColor,
        QualityColor newColor,
        RemotePixelFormatKind actual,
        bool expectedPending)
    {
        var bootstrap = new QualityBootstrapState(
            QualityBootstrapAttempt.Preferred,
            new QualityBootstrapSettings(actual, [6, 16, 0, 1, -239, -223], QualityBootstrapReason.UserFull32));
        var runtime = new TransitionRuntime(
            new ConcurrentQueue<string>(),
            frameCount: 0,
            bootstrapState: bootstrap);
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            Profile(oldColor));
        await viewModel.StartAsync(default);

        viewModel.SetQualityProfile(Profile(newColor));

        Assert.Equal(expectedPending, viewModel.QualityPresentationSnapshot.PendingReconnect);
    }

    [Theory]
    [InlineData(QualityColor.Full32)]
    [InlineData(QualityColor.Automatic)]
    public async Task Pending_color_reconnect_clears_when_user_returns_to_actual_or_automatic(
        QualityColor finalColor)
    {
        var bootstrap = new QualityBootstrapState(
            QualityBootstrapAttempt.Preferred,
            new QualityBootstrapSettings(
                RemotePixelFormatKind.Bgra32,
                [6, 16, 0, 1, -239, -223],
                QualityBootstrapReason.UserFull32));
        await using var viewModel = new RemoteSessionViewModel(
            new TransitionRuntime(
                new ConcurrentQueue<string>(),
                frameCount: 0,
                bootstrapState: bootstrap),
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            Profile(QualityColor.Full32));
        await viewModel.StartAsync(default);

        viewModel.SetQualityProfile(Profile(QualityColor.Color16));
        Assert.True(viewModel.QualityPresentationSnapshot.PendingReconnect);
        Assert.Equal(
            QualityPresentationStatus.ReconnectRequired,
            QualityPresentation.StatusFor(viewModel.QualityPresentationSnapshot));

        viewModel.SetQualityProfile(Profile(finalColor));

        Assert.False(viewModel.QualityPresentationSnapshot.PendingReconnect);
        Assert.NotEqual(
            QualityPresentationStatus.ReconnectRequired,
            QualityPresentation.StatusFor(viewModel.QualityPresentationSnapshot));
    }

    [Fact]
    public async Task Concurrent_profile_snapshot_reads_observe_only_complete_old_or_new_combinations()
    {
        await using var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Original);
        var old = viewModel.QualityPresentationSnapshot;
        var observed = new ConcurrentBag<QualityPresentationSnapshot>();
        using var start = new Barrier(participantCount: 2);

        var reader = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(2)));
            for (var index = 0; index < 10_000; index++)
            {
                observed.Add(viewModel.QualityPresentationSnapshot);
            }
        });
        var writer = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(2)));
            viewModel.SetQualityProfile(QualityProfile.Balanced);
        });

        await Task.WhenAll(reader, writer);
        var current = viewModel.QualityPresentationSnapshot;

        Assert.All(observed, snapshot =>
        {
            Assert.True(ReferenceEquals(snapshot, old) || ReferenceEquals(snapshot, current));
            Assert.Same(snapshot.Actual, ReferenceEquals(snapshot, old) ? old.Actual : current.Actual);
            Assert.Same(snapshot.Profile, ReferenceEquals(snapshot, old) ? old.Profile : current.Profile);
        });
    }

    [Fact]
    public void Presentation_snapshot_publishers_use_release_writes_and_shared_locals()
    {
        var source = File.ReadAllText(RepositoryFile(
                "src", "WinARD.Desktop", "ViewModels", "RemoteSessionViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(
            1,
            source.Split("_qualityPresentationSnapshot = new(", StringSplitOptions.None).Length - 1);
        Assert.True(
            source.Split("Volatile.Write(ref _qualityPresentationSnapshot,", StringSplitOptions.None)
                .Length - 1 == 2);
        Assert.Contains(
            "CreateDiagnosticQualitySnapshotNoLock(\n                    _performanceTracker.CreateDiagnosticQualityMeasurements(),\n                    presentation)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("var diagnosticPresentation = presentationSnapshot with", source, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ObservedEncodingCases))]
    public async Task Actual_primary_encoding_selects_the_observed_winner_before_closed_set_mapping(
        IReadOnlyDictionary<int, int> encodingCounts,
        QualityActualEncoding expected)
    {
        var events = new ConcurrentQueue<string>();
        var runtime = new TransitionRuntime(
            events,
            frameCount: 1,
            frameEncodingCounts: [encodingCounts]);
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(events),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Original);

        await viewModel.StartAsync(default);
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(expected, viewModel.QualityPresentationSnapshot.Actual?.Encoding);
    }

    public static TheoryData<IReadOnlyDictionary<int, int>, QualityActualEncoding> ObservedEncodingCases => new()
    {
        { new Dictionary<int, int> { [777] = 9, [16] = 2 }, QualityActualEncoding.Unknown },
        { new Dictionary<int, int> { [1] = 99, [-239] = 98, [-223] = 97, [6] = 2 }, QualityActualEncoding.Zlib },
        { new Dictionary<int, int> { [16] = 4, [6] = 4 }, QualityActualEncoding.Zlib },
        { new Dictionary<int, int> { [16] = 5, [6] = 4 }, QualityActualEncoding.Zrle },
    };

    [Theory]
    [InlineData(QualityBootstrapAttempt.Preferred, false)]
    [InlineData(QualityBootstrapAttempt.Fallback, true)]
    public async Task Fallback_flag_comes_only_from_bootstrap_attempt(
        QualityBootstrapAttempt attempt,
        bool expectedFallback)
    {
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Bgra32,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.SafeFallback);
        await using var viewModel = new RemoteSessionViewModel(
            new TransitionRuntime(
                new ConcurrentQueue<string>(),
                frameCount: 0,
                bootstrapState: new QualityBootstrapState(attempt, settings)),
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Automatic);

        Assert.Equal(expectedFallback, viewModel.QualityPresentationSnapshot.Actual?.FallbackUsed);
    }

    [Fact]
    public async Task Bootstrap_fallback_controller_reconnect_stays_safe_fallback_until_user_changes_color()
    {
        var events = new ConcurrentQueue<string>();
        var bootstrap = new QualityBootstrapState(
            QualityBootstrapAttempt.Fallback,
            new QualityBootstrapSettings(
                RemotePixelFormatKind.Bgra32,
                [6, 16, 0, 1, -239, -223],
                QualityBootstrapReason.SafeFallback));
        var color16 = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var runtime = new TransitionRuntime(
            events,
            frameCount: 1,
            qualityCapabilities: RfbClient.ConfirmedStandardQualityCapabilities,
            bootstrapState: bootstrap);
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(events),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            color16);

        await viewModel.StartAsync(default);
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(QualityTransitionStatus.ReconnectRequired, viewModel.QualityPresentationSnapshot.TransitionStatus);
        Assert.Equal(QualityPresentationStatus.SafeFallback, QualityPresentation.StatusFor(viewModel.QualityPresentationSnapshot));

        viewModel.SetQualityProfile(QualityPresentation.WithBandwidth(color16, 4L << 20));
        Assert.Equal(QualityPresentationStatus.SafeFallback, QualityPresentation.StatusFor(viewModel.QualityPresentationSnapshot));

        viewModel.SetQualityProfile(QualityProfile.Original);
        viewModel.SetQualityProfile(color16);

        Assert.Equal(QualityPresentationStatus.ReconnectRequired, QualityPresentation.StatusFor(viewModel.QualityPresentationSnapshot));
        Assert.Null(runtime.LastAppliedSettings);
    }

    [Fact]
    public async Task Active_color_change_keeps_wire_state_and_reports_next_connection()
    {
        var events = new ConcurrentQueue<string>();
        var runtime = new TransitionRuntime(
            events,
            frameCount: 2,
            qualityCapabilities: RfbClient.ConfirmedStandardQualityCapabilities,
            gateSecondFrame: true);
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(events),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Original);
        await viewModel.StartAsync(default);
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.SetQualityProfile(QualityPresentation.WithColor(
            QualityProfile.Original,
            QualityColor.Color16));

        Assert.Null(runtime.LastAppliedSettings);
        Assert.Equal(
            QualityTransitionStatus.ReconnectRequired,
            viewModel.QualityPresentationSnapshot.TransitionStatus);
        Assert.Equal(
            "下次连接生效",
            QualityPresentation.StatusText(QualityPresentation.StatusFor(
                viewModel.QualityPresentationSnapshot)));

        runtime.ReleaseSecondFrame.TrySetResult();
        await runtime.ThirdRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(runtime.LastAppliedSettings);
        Assert.Equal(
            QualityTransitionStatus.ReconnectRequired,
            viewModel.QualityPresentationSnapshot.TransitionStatus);
        Assert.Equal(
            "下次连接生效",
            QualityPresentation.StatusText(QualityPresentationStatus.ReconnectRequired));
    }

    [Fact]
    public async Task Applied_transition_repairs_once_then_resumes_incremental_requests()
    {
        var events = new ConcurrentQueue<string>();
        var runtime = new TransitionRuntime(events, frameCount: 2);
        var capabilities = FullCapabilities();
        var profile = QualityProfile.CreateCustom(
            targetBytesPerSecond: null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var controller = new AdaptiveQualityController(profile, capabilities);
        using var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities,
            new QualityDecoderGates());
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(events),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            profile,
            adaptiveQualityController: controller,
            qualityTransitionCoordinator: coordinator);

        await viewModel.StartAsync(default);
        await runtime.ThirdRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            ["Request:False", "Receive:1", "Present", "ApplyTransition", "Request:False", "Receive:2", "Present", "Request:True"],
            events);
        Assert.Equal(1, runtime.NonIncrementalRepairCount);
    }

    [Fact]
    public async Task Production_standard_capabilities_keep_locked_color16_but_fail_closed_without_wire_transition()
    {
        var events = new ConcurrentQueue<string>();
        var runtime = new TransitionRuntime(
            events,
            frameCount: 1,
            qualityCapabilities: RfbClient.ConfirmedStandardQualityCapabilities);
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(events),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            profile);

        await viewModel.StartAsync(default);
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(profile, viewModel.QualityProfile);
        Assert.Null(runtime.LastAppliedSettings);
        Assert.DoesNotContain("ApplyTransition", events);
        Assert.False(runtime.QualityCapabilities.SafeOnlinePixelFormatSwitch);
        Assert.Equal(CapabilitySupport.Unknown, runtime.QualityCapabilities.ServerScaling);
        Assert.Equal(CapabilitySupport.Unknown, runtime.QualityCapabilities.AppleColor1002);
        Assert.Equal(CapabilitySupport.Unknown, runtime.QualityCapabilities.AppleGrayscale1001);
    }

    [Fact]
    public async Task Changing_profile_rebuilds_constraints_and_updates_target_immediately()
    {
        await using var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Automatic);

        viewModel.SetQualityProfile(QualityProfile.Original);

        Assert.Equal(FrameRefreshMode.Unlimited, viewModel.Performance.Mode);
        Assert.Null(viewModel.TargetFramesPerSecond);
    }

    [Fact]
    public async Task Unknown_capabilities_fail_closed_instead_of_rejecting_a_locked_profile()
    {
        var events = new ConcurrentQueue<string>();
        var runtime = new TransitionRuntime(events, frameCount: 1);
        var locked = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Percent75,
            FrameRefreshPolicy.Fixed(30),
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(events),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            locked);

        viewModel.SetQualityProfile(locked);
        await viewModel.StartAsync(default);
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(locked, viewModel.QualityProfile);
        Assert.Equal(30, viewModel.TargetFramesPerSecond);
        Assert.DoesNotContain("ApplyTransition", events);
    }

    [Fact]
    public async Task Profile_change_invalidates_a_decision_waiting_to_apply()
    {
        var events = new ConcurrentQueue<string>();
        var runtime = new TransitionRuntime(events, frameCount: 1);
        var dispatcher = new GateSecondDispatcher();
        var capabilities = FullCapabilities();
        var color16 = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities,
            new QualityDecoderGates());
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(events),
            dispatcher,
            clipboardBridge: null,
            diagnosticSink: null,
            color16,
            adaptiveQualityCapabilities: capabilities,
            adaptiveQualityController: new AdaptiveQualityController(color16, capabilities),
            qualityTransitionCoordinator: coordinator);

        await viewModel.StartAsync(default);
        await dispatcher.SecondInvocationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SetQualityProfile(QualityProfile.Original);
        dispatcher.ReleaseSecond.TrySetResult();
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.DoesNotContain("ApplyTransition", events);
        Assert.Equal(["Request:False", "Request:True"], events.Where(item => item.StartsWith("Request", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Non_frame_messages_do_not_create_quality_decisions()
    {
        var runtime = new BellRuntime();
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Automatic);

        await viewModel.StartAsync(default);
        await runtime.BellDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, viewModel.QualityDecisionGeneration);
    }

    [Fact]
    public async Task Profile_rebuild_keeps_session_decision_generation_monotonic()
    {
        var runtime = new GatedFrameRuntime();
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Automatic);

        await viewModel.StartAsync(default);
        await runtime.SecondReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var before = viewModel.QualityDecisionGeneration;
        viewModel.SetQualityProfile(QualityProfile.Original);
        runtime.ReleaseSecond.TrySetResult();
        await runtime.ThirdRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.QualityDecisionGeneration > before);
    }

    [Fact]
    public async Task Profile_rebuild_reuses_the_coordinators_current_remote_settings()
    {
        var runtime = new ProfileTransitionRuntime();
        var capabilities = FullCapabilities();
        var color16 = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities,
            new QualityDecoderGates());
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            color16,
            adaptiveQualityCapabilities: capabilities,
            adaptiveQualityController: new AdaptiveQualityController(color16, capabilities),
            qualityTransitionCoordinator: coordinator);

        await viewModel.StartAsync(default);
        await runtime.SecondReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SetQualityProfile(QualityProfile.Original);
        runtime.ReleaseSecond.TrySetResult();
        await runtime.SecondTransitionApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            [RemotePixelFormatKind.Rgb565, RemotePixelFormatKind.Bgra32],
            runtime.AppliedSettings.Select(settings => settings.PixelFormat));
    }

    [Fact]
    public async Task Closing_cancels_an_inflight_quality_transition()
    {
        var runtime = new CancelingTransitionRuntime();
        var capabilities = FullCapabilities();
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities,
            new QualityDecoderGates());
        var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            profile,
            adaptiveQualityCapabilities: capabilities,
            adaptiveQualityController: new AdaptiveQualityController(profile, capabilities),
            qualityTransitionCoordinator: coordinator);

        await viewModel.StartAsync(default);
        await runtime.TransitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync();

        Assert.True(runtime.TransitionCanceled);
    }

    [Fact]
    public async Task Profile_change_does_not_cancel_an_inflight_protocol_transition()
    {
        var runtime = new CancelingTransitionRuntime();
        var capabilities = FullCapabilities();
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities,
            new QualityDecoderGates());
        var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            profile,
            adaptiveQualityCapabilities: capabilities,
            adaptiveQualityController: new AdaptiveQualityController(profile, capabilities),
            qualityTransitionCoordinator: coordinator);
        await viewModel.StartAsync(default);
        await runtime.TransitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var change = Task.Run(() => viewModel.SetQualityProfile(QualityProfile.Original));
        await change.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(runtime.TransitionCanceled);
        await viewModel.DisposeAsync();
        await runtime.TransitionCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(runtime.TransitionCanceled);
    }

    [Fact]
    public async Task Completed_transition_from_old_profile_does_not_publish_stale_presentation()
    {
        var runtime = new StaleCompletingTransitionRuntime();
        var capabilities = FullCapabilities();
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities,
            new QualityDecoderGates());
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            profile,
            adaptiveQualityCapabilities: capabilities,
            adaptiveQualityController: new AdaptiveQualityController(profile, capabilities),
            qualityTransitionCoordinator: coordinator);

        await viewModel.StartAsync(default);
        await runtime.TransitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SetQualityProfile(QualityProfile.Original);
        runtime.ReleaseTransition.TrySetResult();
        await runtime.NextReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(viewModel.LatestQualityDecision);
        Assert.Equal(1, viewModel.QualityPresentationVersion);
    }

    [Fact]
    public async Task Diagnostic_snapshot_changes_only_when_the_matching_decision_commits()
    {
        var runtime = new StaleCompletingTransitionRuntime();
        var capabilities = FullCapabilities();
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities,
            new QualityDecoderGates());
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            profile,
            adaptiveQualityCapabilities: capabilities,
            adaptiveQualityController: new AdaptiveQualityController(profile, capabilities),
            qualityTransitionCoordinator: coordinator);
        var initial = viewModel.CreateDiagnosticQualitySnapshot();

        await viewModel.StartAsync(default);
        await runtime.TransitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var whileTransitionPending = viewModel.CreateDiagnosticQualitySnapshot();

        runtime.ReleaseTransition.TrySetResult();
        await runtime.NextReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var committed = viewModel.CreateDiagnosticQualitySnapshot();

        Assert.Same(initial, whileTransitionPending);
        Assert.NotSame(initial, committed);
        Assert.NotNull(committed.QualityPresentation.Decision);
        Assert.Equal(
            committed.QualityPresentation.Performance,
            committed.Performance.Performance);
        Assert.True(committed.Performance.Performance.SampleSequence > 0);
        Assert.Equal(capabilities, committed.QualityCapabilities);
    }

    [Fact]
    public async Task Diagnostic_snapshot_read_during_profile_commit_is_entirely_old_or_new()
    {
        var runtime = new StaleCompletingTransitionRuntime();
        var capabilities = FullCapabilities();
        var oldProfile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities,
            new QualityDecoderGates());
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            oldProfile,
            adaptiveQualityCapabilities: capabilities,
            adaptiveQualityController: new AdaptiveQualityController(oldProfile, capabilities),
            qualityTransitionCoordinator: coordinator);
        var beforeTransition = viewModel.CreateDiagnosticQualitySnapshot();
        await viewModel.StartAsync(default);
        await runtime.TransitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var whileTransitionPending = viewModel.CreateDiagnosticQualitySnapshot();
        Assert.Same(beforeTransition, whileTransitionPending);
        runtime.ReleaseTransition.TrySetResult();
        await runtime.NextReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var old = viewModel.CreateDiagnosticQualitySnapshot();
        Assert.NotSame(whileTransitionPending, old);
        var observed = new ConcurrentBag<RemoteSessionDiagnosticQualitySnapshot>();
        using var start = new Barrier(participantCount: 2);

        var reader = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(2)));
            for (var index = 0; index < 10_000; index++)
                observed.Add(viewModel.CreateDiagnosticQualitySnapshot());
        });
        var writer = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(2)));
            viewModel.SetQualityProfile(QualityProfile.Original);
        });
        await Task.WhenAll(reader, writer);
        var current = viewModel.CreateDiagnosticQualitySnapshot();
        Assert.NotSame(old, current);

        Assert.All(observed, snapshot =>
        {
            var presentation = snapshot.QualityPresentation;
            Assert.True(ReferenceEquals(snapshot, old) || ReferenceEquals(snapshot, current));
            Assert.Equal(presentation.Performance, snapshot.Performance.Performance);
            Assert.Equal(
                snapshot.Performance.Performance.ActualFramesPerSecond,
                snapshot.QualityObservation.ActualFramesPerSecond);
            Assert.Equal(capabilities, snapshot.QualityCapabilities);
        });
        Assert.Same(oldProfile, old.QualityPresentation.Profile);
        Assert.NotNull(old.QualityPresentation.Decision);
        Assert.Same(QualityProfile.Original, current.QualityPresentation.Profile);
        Assert.Null(current.QualityPresentation.Decision);
    }

    [Fact]
    public async Task Profile_change_does_not_hide_an_operation_canceled_after_wire_start()
    {
        var runtime = new WireCanceledTransitionRuntime();
        var capabilities = FullCapabilities();
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities,
            new QualityDecoderGates());
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            profile,
            adaptiveQualityCapabilities: capabilities,
            adaptiveQualityController: new AdaptiveQualityController(profile, capabilities),
            qualityTransitionCoordinator: coordinator);
        await viewModel.StartAsync(default);
        await runtime.WireStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.SetQualityProfile(QualityProfile.Original);
        runtime.ReleaseFailure.TrySetResult();
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, runtime.RequestCount);
    }

    [Fact]
    public async Task Input_activity_contains_no_payload_and_tracks_drag_scroll_and_state_only_close()
    {
        var runtime = new BlockingRuntime();
        var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(new ConcurrentQueue<string>()),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            QualityProfile.Automatic);

        await viewModel.SendPointerAsync(1, new RemotePoint(123, 456), default);
        Assert.True(viewModel.QualityActivity.PointerDragActive);
        viewModel.RecordScrollInput();
        Assert.True(viewModel.QualityActivity.ScrollActive);
        var lastInput = viewModel.QualityActivity.LastInputTimestamp;

        await viewModel.DisposeAsync();

        Assert.False(viewModel.QualityActivity.PointerDragActive);
        Assert.Equal(lastInput, viewModel.QualityActivity.LastInputTimestamp);
        Assert.DoesNotContain(
            viewModel.QualityActivity.GetType().GetProperties(),
            property => property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Text", StringComparison.OrdinalIgnoreCase) ||
                property.Name is "X" or "Y" or "Buttons" or "Coordinates");
    }

    [Theory]
    [InlineData(QualityTransitionStatus.ReconnectRequired)]
    [InlineData(QualityTransitionStatus.CapabilityUnavailable)]
    public async Task Non_applied_transition_statuses_keep_the_incremental_request_chain(
        QualityTransitionStatus status)
    {
        var events = new ConcurrentQueue<string>();
        var runtime = new TransitionRuntime(events, frameCount: 2, transitionStatus: status);
        var capabilities = FullCapabilities();
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities,
            new QualityDecoderGates());
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(events),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            profile,
            adaptiveQualityCapabilities: capabilities,
            adaptiveQualityController: new AdaptiveQualityController(profile, capabilities),
            qualityTransitionCoordinator: coordinator);

        await viewModel.StartAsync(default);
        await runtime.ThirdRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["Request:False", "Request:True", "Request:True"], events.Where(item => item.StartsWith("Request", StringComparison.Ordinal)));
        Assert.Equal(0, runtime.NonIncrementalRepairCount);
    }

    [Fact]
    public async Task Faulted_transition_terminates_the_session_without_another_request()
    {
        var events = new ConcurrentQueue<string>();
        var runtime = new TransitionRuntime(
            events,
            frameCount: 1,
            transitionStatus: QualityTransitionStatus.Faulted);
        var capabilities = FullCapabilities();
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true,
            scaleLocked: true);
        var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities,
            new QualityDecoderGates());
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new AsyncLifetime(),
            new EventPresenter(events),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            profile,
            adaptiveQualityCapabilities: capabilities,
            adaptiveQualityController: new AdaptiveQualityController(profile, capabilities),
            qualityTransitionCoordinator: coordinator);

        await viewModel.StartAsync(default);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(viewModel.Error);
        Assert.Equal(["Request:False"], events.Where(item => item.StartsWith("Request", StringComparison.Ordinal)));
    }

    private static ArdDisplayCapabilities FullCapabilities() => new(
        CapabilitySupport.Observed,
        CapabilitySupport.Observed,
        CapabilitySupport.Observed,
        CapabilitySupport.Unsupported,
        CapabilitySupport.Unsupported,
        SafeOnlinePixelFormatSwitch: true,
        SafeOnlineScaleSwitch: false,
        MaximumRefreshRate: 60);

    private static QualityProfile Profile(QualityColor color) => QualityProfile.CreateCustom(
        2L << 20,
        color,
        QualityScale.Native,
        FrameRefreshPolicy.Automatic,
        allowAutomaticGrayscale: color is QualityColor.Automatic or QualityColor.Grayscale);

    private sealed class TransitionRuntime(
        ConcurrentQueue<string> events,
        int frameCount,
        QualityTransitionStatus transitionStatus = QualityTransitionStatus.Applied,
        ArdDisplayCapabilities? qualityCapabilities = null,
        QualityBootstrapState? bootstrapState = null,
        bool gateSecondFrame = false,
        int[]? frameEncodings = null,
        IReadOnlyDictionary<int, int>[]? frameEncodingCounts = null)
        : IRemoteSessionRuntime
    {
        private int _receiveCount;
        private int _requestCount;
        public int NonIncrementalRepairCount { get; private set; }
        public RemoteQualitySettings? LastAppliedSettings { get; private set; }
        public ArdDisplayCapabilities QualityCapabilities { get; } =
            qualityCapabilities ?? ConservativeCapabilities();
        public QualityBootstrapState BootstrapState { get; } =
            bootstrapState ?? QualityBootstrapState.LegacyBgra32;
        public TaskCompletionSource ThirdRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            events.Enqueue($"Request:{incremental}");
            var count = Interlocked.Increment(ref _requestCount);
            if (count == 2)
            {
                SecondRequest.TrySetResult();
            }
            if (count == 3)
            {
                ThirdRequest.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }

        public async ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(
            RemoteQualitySettings settings,
            CancellationToken cancellationToken)
        {
            events.Enqueue("ApplyTransition");
            LastAppliedSettings = settings;
            if (transitionStatus == QualityTransitionStatus.Applied)
            {
                NonIncrementalRepairCount++;
                await RequestFramebufferUpdateAsync(false, cancellationToken);
            }
            return transitionStatus;
        }

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _receiveCount);
            if (count <= frameCount)
            {
                if (count == 2 && gateSecondFrame)
                {
                    await ReleaseSecondFrame.Task.WaitAsync(cancellationToken);
                }
                events.Enqueue($"Receive:{count}");
                var counts = frameEncodingCounts is not null && count <= frameEncodingCounts.Length
                    ? frameEncodingCounts[count - 1]
                    : new Dictionary<int, int>
                    {
                        [frameEncodings is not null && count <= frameEncodings.Length
                            ? frameEncodings[count - 1]
                            : 0] = 1,
                    };
                return new RemoteFramebufferMessage(
                    new RemoteFramebufferSize(1, 1),
                    [0, 0, 0, 255],
                    4,
                    [new RemoteRectangle(0, 0, 1, 1)],
                    cursor: null,
                    new RemoteUpdateStatistics(4, counts));
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;

        private static ArdDisplayCapabilities ConservativeCapabilities() => new(
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            false,
            false,
            null);
    }

    private sealed class BlockingRuntime : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class BellRuntime : IRemoteSessionRuntime
    {
        private int _received;
        public TaskCompletionSource BellDelivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _received) == 1)
            {
                BellDelivered.TrySetResult();
                return new RemoteBellMessage();
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedFrameRuntime : IRemoteSessionRuntime
    {
        private int _receives;
        private int _requests;
        public TaskCompletionSource SecondReceiveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 3) ThirdRequest.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _receives);
            if (count == 2)
            {
                SecondReceiveEntered.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }
            if (count <= 2)
            {
                return new RemoteFramebufferMessage(new(1, 1), [0, 0, 0, 255], 4, [new(0, 0, 1, 1)]);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancelingTransitionRuntime : IRemoteSessionRuntime
    {
        private int _received;
        public TaskCompletionSource TransitionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TransitionCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TransitionCanceled { get; private set; }
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(RemoteQualitySettings settings, CancellationToken cancellationToken)
        {
            TransitionEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TransitionCanceled = true;
                TransitionCancellationObserved.TrySetResult();
                throw;
            }
            return QualityTransitionStatus.Faulted;
        }
        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _received) == 1)
                return new RemoteFramebufferMessage(new(1, 1), [0, 0, 0, 255], 4, [new(0, 0, 1, 1)]);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaleCompletingTransitionRuntime : IRemoteSessionRuntime
    {
        private int _received;
        public TaskCompletionSource TransitionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseTransition { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NextReceiveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(RemoteQualitySettings settings, CancellationToken cancellationToken)
        {
            TransitionEntered.TrySetResult();
            await ReleaseTransition.Task;
            return QualityTransitionStatus.Applied;
        }
        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _received) == 1)
                return new RemoteFramebufferMessage(new(1, 1), [0, 0, 0, 255], 4, [new(0, 0, 1, 1)]);
            NextReceiveEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProfileTransitionRuntime : IRemoteSessionRuntime
    {
        private int _receives;
        public List<RemoteQualitySettings> AppliedSettings { get; } = [];
        public TaskCompletionSource SecondReceiveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondTransitionApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(RemoteQualitySettings settings, CancellationToken cancellationToken)
        {
            AppliedSettings.Add(settings);
            await RequestFramebufferUpdateAsync(false, cancellationToken);
            if (AppliedSettings.Count == 2) SecondTransitionApplied.TrySetResult();
            return QualityTransitionStatus.Applied;
        }
        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _receives);
            if (count == 2)
            {
                SecondReceiveEntered.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }
            if (count <= 2)
                return new RemoteFramebufferMessage(new(1, 1), [0, 0, 0, 255], 4, [new(0, 0, 1, 1)]);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class WireCanceledTransitionRuntime : IRemoteSessionRuntime
    {
        private int _received;
        public int RequestCount { get; private set; }
        public TaskCompletionSource WireStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            RequestCount++;
            return ValueTask.CompletedTask;
        }
        public async ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(RemoteQualitySettings settings, CancellationToken cancellationToken)
        {
            WireStarted.TrySetResult();
            await ReleaseFailure.Task;
            throw new OperationCanceledException("wire transition interrupted");
        }
        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _received) == 1)
                return new RemoteFramebufferMessage(new(1, 1), [0, 0, 0, 255], 4, [new(0, 0, 1, 1)]);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class EventPresenter(ConcurrentQueue<string> events) : IFramePresenter
    {
        public void Resize(int width, int height) { }
        public void Present(ReadOnlySpan<byte> bgra32, int stride, IReadOnlyList<RemoteRectangle> dirtyRectangles) =>
            events.Enqueue("Present");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class QueuedPresentationDispatcher : IUiDispatcher
    {
        private readonly Queue<(Action Action, TaskCompletionSource Completion)> _queued = new();
        private bool _releaseImmediately;

        public int QueuedCount => _queued.Count;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_releaseImmediately)
            {
                action();
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queued.Enqueue((action, completion));
            return completion.Task;
        }

        public void Drain()
        {
            _releaseImmediately = true;
            while (_queued.TryDequeue(out var invocation))
            {
                invocation.Action();
                invocation.Completion.TrySetResult();
            }
        }
    }

    private sealed class SwitchableFailingPresentationDispatcher(bool throwSynchronously) : IUiDispatcher
    {
        public bool Fail { get; set; } = true;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Fail)
            {
                action();
                return Task.CompletedTask;
            }

            return throwSynchronously
                ? throw new InvalidOperationException("dispatcher unavailable")
                : Task.FromException(new InvalidOperationException("dispatcher unavailable"));
        }
    }

    private sealed class GateSecondDispatcher : IUiDispatcher
    {
        private int _invocations;
        public TaskCompletionSource SecondInvocationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _invocations) == 2)
            {
                SecondInvocationEntered.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            action();
        }
    }

    private sealed class AsyncLifetime : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException("Could not locate repository root.")
            : Path.Combine([directory.FullName, .. segments]);
    }
}
