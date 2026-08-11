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
    public async Task Production_standard_capabilities_enable_a_q1_transition_without_test_injection()
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

        Assert.Equal(RemotePixelFormatKind.Rgb565, runtime.LastAppliedSettings?.PixelFormat);
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
        Assert.Equal(0, viewModel.QualityPresentationVersion);
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

    private sealed class TransitionRuntime(
        ConcurrentQueue<string> events,
        int frameCount,
        QualityTransitionStatus transitionStatus = QualityTransitionStatus.Applied,
        ArdDisplayCapabilities? qualityCapabilities = null)
        : IRemoteSessionRuntime
    {
        private int _receiveCount;
        private int _requestCount;
        public int NonIncrementalRepairCount { get; private set; }
        public RemoteQualitySettings? LastAppliedSettings { get; private set; }
        public ArdDisplayCapabilities QualityCapabilities { get; } =
            qualityCapabilities ?? ConservativeCapabilities();
        public TaskCompletionSource ThirdRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                events.Enqueue($"Receive:{count}");
                return new RemoteFramebufferMessage(
                    new RemoteFramebufferSize(1, 1),
                    [0, 0, 0, 255],
                    4,
                    [new RemoteRectangle(0, 0, 1, 1)]);
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
}
