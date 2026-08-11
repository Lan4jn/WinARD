using System.Runtime.InteropServices;
using System.Text.Json;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Services;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using Xunit;

#pragma warning disable CA1707, CA2201

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class RemoteSessionViewModelTests
{
    [Fact]
    public async Task Session_frame_mailbox_does_not_merge_a_frame_already_taken_for_presentation()
    {
        await using var mailbox = new SessionFrameMailbox();
        mailbox.Publish(CreateSessionFrame(sequence: 1, receivedBytes: 10, encoding: 0, x: 0));

        using var taken = await mailbox.ReadLatestAsync(CancellationToken.None);
        mailbox.Publish(CreateSessionFrame(sequence: 2, receivedBytes: 20, encoding: 16, x: 1));
        using var next = await mailbox.ReadLatestAsync(CancellationToken.None);

        Assert.Equal(10, taken.Performance.Update.ReceivedSessionBytes);
        Assert.Equal(20, next.Performance.Update.ReceivedSessionBytes);
        Assert.Equal(new Dictionary<int, int> { [0] = 1 }, taken.Performance.Update.EncodingCounts);
        Assert.Equal(new Dictionary<int, int> { [16] = 1 }, next.Performance.Update.EncodingCounts);
    }

    [Fact]
    public async Task Session_frame_mailbox_merges_replaced_frame_statistics_encodings_and_dirty_area()
    {
        await using var mailbox = new SessionFrameMailbox();
        mailbox.Publish(CreateSessionFrame(sequence: 1, receivedBytes: 10, encoding: 0, x: 0));
        mailbox.Publish(CreateSessionFrame(sequence: 2, receivedBytes: 20, encoding: 16, x: 1));

        using var latest = await mailbox.ReadLatestAsync(CancellationToken.None);

        Assert.Equal(30, latest.Performance.Update.ReceivedSessionBytes);
        Assert.Equal(
            new Dictionary<int, int> { [0] = 1, [16] = 1 },
            latest.Performance.Update.EncodingCounts);
        Assert.Equal(
            [new RemoteRectangle(0, 0, 1, 1), new RemoteRectangle(1, 0, 1, 1)],
            latest.Performance.Dirty.OrderBy(rectangle => rectangle.X));
    }

    [Fact]
    public async Task Session_frame_mailbox_dispose_is_idempotent_and_releases_pending_take()
    {
        var mailbox = new SessionFrameMailbox();
        var take = mailbox.ReadLatestAsync(CancellationToken.None).AsTask();

        await Task.WhenAll(mailbox.DisposeAsync().AsTask(), mailbox.DisposeAsync().AsTask());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => take);
        Assert.True(mailbox.ResourcesDisposed);
    }

    [Fact]
    public async Task Session_frame_mailbox_dispose_waits_for_taken_reader_to_exit_after_cancel()
    {
        var coordinator = new SessionFrameMailboxTestCoordinator(
            SessionFrameMailboxTestPause.AfterTake);
        var mailbox = new SessionFrameMailbox(coordinator);
        mailbox.Publish(CreateSessionFrame(1, 10, 0, 0));
        var read = Task.Run(async () => await mailbox.ReadLatestAsync(CancellationToken.None));
        await coordinator.Paused.WaitAsync(TimeSpan.FromSeconds(2));

        var dispose = Task.Run(async () => await mailbox.DisposeAsync());
        await coordinator.DisposeCancellationIssued.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(dispose.IsCompleted);
        coordinator.Release();
        using var frame = await read;
        await dispose;
        Assert.True(mailbox.ResourcesDisposed);
    }

    [Fact]
    public async Task Session_frame_mailbox_dispose_cannot_release_resources_before_publish_signals()
    {
        var coordinator = new SessionFrameMailboxTestCoordinator(
            SessionFrameMailboxTestPause.BeforeSignal);
        var mailbox = new SessionFrameMailbox(coordinator);
        var publish = Task.Run(() => mailbox.Publish(CreateSessionFrame(1, 10, 0, 0)));
        await coordinator.Paused.WaitAsync(TimeSpan.FromSeconds(2));

        var dispose = Task.Run(async () => await mailbox.DisposeAsync());
        await coordinator.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(dispose.IsCompleted);
        coordinator.Release();
        await publish;
        await dispose;
        Assert.True(mailbox.ResourcesDisposed);
    }

    private static SessionFrameEnvelope CreateSessionFrame(
        long sequence,
        long receivedBytes,
        int encoding,
        int x)
    {
        var dirty = new RemoteRectangle(x, 0, 1, 1);
        return new SessionFrameEnvelope(
            new FramePacket(
                sequence,
                width: 2,
                height: 1,
                stride: 8,
                length: 8,
                [dirty],
                new TrackingMemoryOwner(new byte[8])),
            new SessionFramePerformance(
                new RemoteUpdateStatistics(
                    receivedBytes,
                    new Dictionary<int, int> { [encoding] = 1 }),
                [dirty],
                new RemoteFramebufferSize(2, 1),
                TimeSpan.Zero));
    }

    [Fact]
    public async Task Fixed_30_waits_for_pacer_before_requesting_the_next_frame()
    {
        var runtime = new RefreshPolicyRuntime(frameCount: 1);
        var delayEntered = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new ManualTimestampProvider();
        var pacer = new FramebufferRequestPacer(
            time,
            (delay, _) =>
            {
                delayEntered.TrySetResult(delay);
                return releaseDelay.Task;
            });
        await using var viewModel = CreateRefreshViewModel(
            runtime,
            FrameRefreshPolicy.Fixed(30),
            time,
            pacer);

        await viewModel.StartAsync(CancellationToken.None);
        var requestedDelay = await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.InRange(requestedDelay.TotalMilliseconds, 33.2, 33.4);
        Assert.Equal(1, runtime.RequestCount);

        releaseDelay.TrySetResult();
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, runtime.RequestCount);
    }

    [Fact]
    public async Task Unlimited_requests_the_next_frame_without_pacer_delay()
    {
        var runtime = new RefreshPolicyRuntime(frameCount: 1);
        var delayCount = 0;
        var time = new ManualTimestampProvider();
        var pacer = new FramebufferRequestPacer(
            time,
            (_, _) =>
            {
                Interlocked.Increment(ref delayCount);
                return Task.CompletedTask;
            });
        await using var viewModel = CreateRefreshViewModel(
            runtime,
            FrameRefreshPolicy.Unlimited,
            time,
            pacer);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, delayCount);
        Assert.Null(viewModel.TargetFramesPerSecond);
    }

    [Theory]
    [InlineData(30, 33.3)]
    [InlineData(60, 16.6)]
    public async Task Fixed_rate_spaces_actual_writes_after_background_queue_delay(
        int framesPerSecond,
        double minimumSpacingMilliseconds)
    {
        var time = new ManualTimestampProvider();
        var runtime = new QueuedWriteTimingRuntime(time, TimeSpan.FromMilliseconds(100));
        var pacer = new FramebufferRequestPacer(
            time,
            (delay, _) =>
            {
                time.Advance(TimeSpan.FromMilliseconds(Math.Ceiling(delay.TotalMilliseconds)));
                return Task.CompletedTask;
            });
        await using var viewModel = CreateRefreshViewModel(
            runtime,
            FrameRefreshPolicy.Fixed(framesPerSecond),
            time,
            pacer);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.SecondWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, runtime.WriteCompletedAt.Count);
        var spacing = time.GetElapsedTime(
            runtime.WriteCompletedAt[0],
            runtime.WriteCompletedAt[1]);
        Assert.True(spacing.TotalMilliseconds >= minimumSpacingMilliseconds);
    }

    [Fact]
    public async Task Automatic_bad_samples_lower_target_and_update_the_pacer()
    {
        using var presentationGate = new SemaphoreSlim(0);
        var runtime = new RefreshPolicyRuntime(
            frameCount: 3,
            performance: new RemoteRuntimePerformanceSnapshot(100, 1, 1),
            presentationGate: presentationGate);
        var time = new ManualTimestampProvider();
        var pacer = new FramebufferRequestPacer(time, (_, _) => Task.CompletedTask);
        await using var viewModel = CreateRefreshViewModel(
            runtime,
            FrameRefreshPolicy.Automatic,
            time,
            pacer,
            new SignalingPresenter(presentationGate));
        var targetChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteSessionViewModel.TargetFramesPerSecond) &&
                viewModel.TargetFramesPerSecond == 45)
            {
                targetChanged.TrySetResult();
            }
        };

        await viewModel.StartAsync(CancellationToken.None);
        await targetChanged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.FourthRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(FrameRefreshMode.Automatic, viewModel.Performance.Mode);
        Assert.Equal(45, viewModel.TargetFramesPerSecond);
    }

    [Fact]
    public async Task Set_refresh_policy_updates_target_immediately()
    {
        await using var viewModel = CreateRefreshViewModel(
            new BlockingRuntime(),
            FrameRefreshPolicy.Automatic,
            new ManualTimestampProvider(),
            pacer: null);

        viewModel.SetFrameRefreshPolicy(FrameRefreshPolicy.Fixed(30));

        Assert.Equal(FrameRefreshMode.Fixed, viewModel.Performance.Mode);
        Assert.Equal(30, viewModel.TargetFramesPerSecond);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(241)]
    public async Task Invalid_remote_refresh_maximum_is_ignored_with_safe_category(int maximum)
    {
        var sink = new RecordingDiagnosticSink();
        await using var viewModel = new RemoteSessionViewModel(
            new DisplayCapabilityRuntime(maximum),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: sink,
            initialRefreshPolicy: FrameRefreshPolicy.Automatic);

        Assert.Equal(60, viewModel.TargetFramesPerSecond);
        var diagnostic = Assert.Single(
            sink.Events,
            item => item.Code == "REMOTE_DISPLAY_CAPABILITIES_INVALID");
        Assert.Equal("InvalidRefreshRateRange", Assert.Single(diagnostic.Fields!).Value);
        Assert.DoesNotContain(
            maximum.ToString(System.Globalization.CultureInfo.InvariantCulture),
            diagnostic.Fields!.Select(field => field.Value));
    }

    [Fact]
    public async Task Fixed_policy_target_is_capped_without_changing_selected_policy()
    {
        await using var viewModel = new RemoteSessionViewModel(
            new DisplayCapabilityRuntime(60),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Fixed(120));

        Assert.Equal(FrameRefreshMode.Fixed, viewModel.Performance.Mode);
        Assert.Equal(60, viewModel.TargetFramesPerSecond);

        viewModel.SetFrameRefreshPolicy(FrameRefreshPolicy.Fixed(105));
        Assert.Equal(60, viewModel.TargetFramesPerSecond);

        viewModel.SetFrameRefreshPolicy(FrameRefreshPolicy.Unlimited);
        Assert.Null(viewModel.TargetFramesPerSecond);
    }

    [Fact]
    public async Task Unknown_remote_maximum_keeps_every_refresh_option_enabled()
    {
        await using var viewModel = CreateRefreshViewModel(
            new BlockingRuntime(),
            FrameRefreshPolicy.Automatic,
            new ManualTimestampProvider(),
            pacer: null);

        Assert.Equal(9, viewModel.FrameRefreshOptions.Count);
        Assert.All(viewModel.FrameRefreshOptions, option => Assert.True(option.IsEnabled));
        Assert.Equal(FrameRefreshPolicy.Automatic, viewModel.SelectedFrameRefreshOption.Policy);
    }

    [Fact]
    public async Task Reliable_remote_maximum_disables_only_higher_fixed_options()
    {
        await using var viewModel = new RemoteSessionViewModel(
            new DisplayCapabilityRuntime(59),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Fixed(45));

        Assert.True(FindOption(viewModel, FrameRefreshPolicy.Automatic).IsEnabled);
        Assert.True(FindOption(viewModel, FrameRefreshPolicy.Fixed(45)).IsEnabled);
        Assert.False(FindOption(viewModel, FrameRefreshPolicy.Fixed(60)).IsEnabled);
        Assert.False(FindOption(viewModel, FrameRefreshPolicy.Fixed(120)).IsEnabled);
        Assert.True(FindOption(viewModel, FrameRefreshPolicy.Unlimited).IsEnabled);
    }

    [Fact]
    public async Task Saved_fixed_policy_above_remote_maximum_remains_selected_with_effective_constraint()
    {
        await using var viewModel = new RemoteSessionViewModel(
            new DisplayCapabilityRuntime(59),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Fixed(120));

        Assert.Equal(FrameRefreshPolicy.Fixed(120), viewModel.SelectedFrameRefreshOption.Policy);
        Assert.False(viewModel.SelectedFrameRefreshOption.IsEnabled);
        Assert.Equal("当前上限 59，有效 59", viewModel.SelectedFrameRefreshOption.ConstraintText);
    }

    [Fact]
    public async Task Quality_capability_only_maximum_disables_higher_options_preserves_saved_value_and_clips_target()
    {
        await using var viewModel = new RemoteSessionViewModel(
            new QualityCapabilityRuntime(60),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Fixed(120));

        Assert.Equal(FrameRefreshPolicy.Fixed(120), viewModel.SelectedFrameRefreshOption.Policy);
        Assert.False(viewModel.SelectedFrameRefreshOption.IsEnabled);
        Assert.Equal("当前上限 60，有效 60", viewModel.SelectedFrameRefreshOption.ConstraintText);
        Assert.False(FindOption(viewModel, FrameRefreshPolicy.Fixed(75)).IsEnabled);
        Assert.Equal(60, viewModel.TargetFramesPerSecond);
    }

    [Fact]
    public async Task Multiple_reliable_maximum_sources_use_the_lower_safe_limit()
    {
        await using var viewModel = new RemoteSessionViewModel(
            new QualityCapabilityRuntime(90, displayMaximum: 60),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Fixed(90));

        Assert.False(FindOption(viewModel, FrameRefreshPolicy.Fixed(75)).IsEnabled);
        Assert.Equal(60, viewModel.TargetFramesPerSecond);
    }

    [Fact]
    public void Performance_text_formats_mode_measurements_rate_encoding_and_response()
    {
        var snapshot = new SessionPerformanceSnapshot(
            FrameRefreshMode.Automatic,
            45,
            38,
            13_002_342,
            16,
            86,
            0,
            0,
            0,
            1);

        Assert.Equal(
            "自动 45 FPS · 实际 38 FPS · 12.4 MiB/s · ZRLE · 86 ms",
            RemoteSessionViewModel.FormatSessionPerformance(snapshot));
    }

    [Fact]
    public void Fixed_performance_text_includes_the_mode_name()
    {
        var snapshot = new SessionPerformanceSnapshot(
            FrameRefreshMode.Fixed,
            60,
            38,
            13_002_342,
            16,
            86,
            0,
            0,
            0,
            1);

        Assert.Equal(
            "固定 60 FPS · 实际 38 FPS · 12.4 MiB/s · ZRLE · 86 ms",
            RemoteSessionViewModel.FormatSessionPerformance(snapshot));
    }

    [Fact]
    public void Performance_text_uses_the_stable_zlib_encoding_name()
    {
        var snapshot = new SessionPerformanceSnapshot(
            FrameRefreshMode.Automatic,
            45,
            38,
            13_002_342,
            (int)RfbEncodingType.Zlib,
            86,
            0,
            0,
            0,
            1);

        Assert.Contains(" · Zlib · ", RemoteSessionViewModel.FormatSessionPerformance(snapshot));
    }

    [Fact]
    public void Performance_text_uses_dashes_for_unmeasured_values_and_stable_signed_encoding_ids()
    {
        var unknown = new SessionPerformanceSnapshot(
            FrameRefreshMode.Unlimited, null, 0, 0, null, 0, 0, 0, 0, 0);
        var signedEncoding = unknown with
        {
            Mode = FrameRefreshMode.Fixed,
            TargetFramesPerSecond = 60,
            ActualFramesPerSecond = 1,
            ReceiveBytesPerSecond = 1,
            PrimaryFramebufferEncoding = -314,
            ResponseMilliseconds = 1,
            SampleSequence = 1,
        };

        Assert.Equal("无限 · 实际 — · — · — · —", RemoteSessionViewModel.FormatSessionPerformance(unknown));
        Assert.Contains("编码 -314", RemoteSessionViewModel.FormatSessionPerformance(signedEncoding));
    }

    [Fact]
    public async Task Performance_publication_is_bounded_and_publishes_latest_snapshot_after_one_second()
    {
        var time = new ManualTimestampProvider();
        var dispatcher = new CountingDispatcher();
        await using var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new TrackingLifetime(),
            new TrackingPresenter(),
            dispatcher,
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Automatic,
            timeProvider: time);
        var notifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteSessionViewModel.SessionPerformance))
            {
                notifications++;
            }
        };

        for (var sequence = 1; sequence <= 20; sequence++)
        {
            await viewModel.PublishSessionPerformanceAsync(
                CreatePerformanceSnapshot(sequence),
                force: false,
                CancellationToken.None);
        }

        Assert.Equal(1, dispatcher.InvocationCount);
        Assert.Equal(1, notifications);
        time.Advance(TimeSpan.FromSeconds(1));
        await viewModel.PublishSessionPerformanceAsync(
            CreatePerformanceSnapshot(21),
            force: false,
            CancellationToken.None);
        Assert.Equal(2, dispatcher.InvocationCount);
        Assert.Equal(2, notifications);
        Assert.Equal(21, viewModel.Performance.SampleSequence);
    }

    [Fact]
    public async Task Performance_policy_change_publishes_immediately_without_delayed_work_after_close()
    {
        var time = new ManualTimestampProvider();
        var dispatcher = new CountingDispatcher();
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new TrackingLifetime(),
            new TrackingPresenter(),
            dispatcher,
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Automatic,
            timeProvider: time);
        var publications = Enumerable.Range(1, 20)
            .Select(sequence => viewModel.PublishSessionPerformanceAsync(
                CreatePerformanceSnapshot(sequence),
                force: false,
                CancellationToken.None).AsTask())
            .ToArray();

        viewModel.SetFrameRefreshPolicy(FrameRefreshPolicy.Fixed(60));

        Assert.StartsWith("固定 60 FPS", viewModel.SessionPerformance, StringComparison.Ordinal);
        Assert.All(publications, publication => Assert.True(publication.IsCompletedSuccessfully));
        await viewModel.DisposeAsync();
        Assert.False(viewModel.HasPendingPerformancePublication);
    }

    [Fact]
    public async Task Queued_automatic_snapshot_cannot_overwrite_immediate_fixed_policy()
    {
        var dispatcher = new QueuedDispatcher();
        await using var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new TrackingLifetime(),
            new TrackingPresenter(),
            dispatcher,
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Automatic,
            timeProvider: new ManualTimestampProvider());
        var oldAutomatic = viewModel.PublishSessionPerformanceAsync(
            CreatePerformanceSnapshot(1),
            force: true,
            CancellationToken.None).AsTask();

        await dispatcher.InvocationQueued;
        viewModel.SetFrameRefreshPolicy(FrameRefreshPolicy.Fixed(60));
        Assert.StartsWith("固定 60 FPS", viewModel.SessionPerformance, StringComparison.Ordinal);
        dispatcher.ReleaseAll();
        await oldAutomatic;

        Assert.Equal(FrameRefreshMode.Fixed, viewModel.Performance.Mode);
        Assert.StartsWith("固定 60 FPS", viewModel.SessionPerformance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_old_publications_are_dropped_and_new_generation_is_not_lost()
    {
        var dispatcher = new QueuedDispatcher();
        await using var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new TrackingLifetime(),
            new TrackingPresenter(),
            dispatcher,
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Automatic,
            timeProvider: new ManualTimestampProvider());
        var first = viewModel.PublishSessionPerformanceAsync(
            CreatePerformanceSnapshot(1), force: true, CancellationToken.None).AsTask();
        var second = viewModel.PublishSessionPerformanceAsync(
            CreatePerformanceSnapshot(2), force: true, CancellationToken.None).AsTask();
        await dispatcher.InvocationQueued;

        viewModel.SetFrameRefreshPolicy(FrameRefreshPolicy.Unlimited);
        dispatcher.ReleaseAll();
        await Task.WhenAll(first, second);
        await viewModel.PublishSessionPerformanceAsync(
            CreatePerformanceSnapshot(3) with
            {
                Mode = FrameRefreshMode.Unlimited,
                TargetFramesPerSecond = null,
            },
            force: true,
            CancellationToken.None);

        Assert.Equal(FrameRefreshMode.Unlimited, viewModel.Performance.Mode);
        Assert.Equal(3, viewModel.Performance.SampleSequence);
    }

    [Fact]
    public async Task Dispose_invalidates_queued_performance_publication()
    {
        var dispatcher = new QueuedDispatcher();
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new TrackingLifetime(),
            new TrackingPresenter(),
            dispatcher,
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Automatic,
            timeProvider: new ManualTimestampProvider());
        var publication = viewModel.PublishSessionPerformanceAsync(
            CreatePerformanceSnapshot(99),
            force: true,
            CancellationToken.None).AsTask();
        await dispatcher.InvocationQueued;

        var disposal = viewModel.DisposeAsync().AsTask();
        dispatcher.ReleaseAll();
        await Task.WhenAll(publication, disposal);

        Assert.Equal(0, viewModel.Performance.SampleSequence);
        Assert.False(viewModel.HasPendingPerformancePublication);
    }

    private static SessionPerformanceSnapshot CreatePerformanceSnapshot(long sequence) =>
        new(
            FrameRefreshMode.Automatic,
            60,
            (int)sequence,
            sequence * 1024,
            16,
            (int)sequence,
            0,
            0,
            0,
            sequence);

    private static FrameRefreshOption FindOption(
        RemoteSessionViewModel viewModel,
        FrameRefreshPolicy policy) =>
        Assert.Single(viewModel.FrameRefreshOptions, option => option.Policy == policy);

    private static RemoteSessionViewModel CreateRefreshViewModel(
        IRemoteSessionRuntime runtime,
        FrameRefreshPolicy policy,
        TimeProvider time,
        FramebufferRequestPacer? pacer,
        IFramePresenter? presenter = null) =>
        new(
            runtime,
            new TrackingLifetime(),
            presenter ?? new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: policy,
            timeProvider: time,
            pacer: pacer);

    [Fact]
    public async Task Dispose_before_start_reports_disconnected_quality()
    {
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.DisposeAsync();

        Assert.Equal(ConnectionQualityLevel.Disconnected, viewModel.ConnectionQuality.Level);
    }

    [Fact]
    public async Task Dispose_cancels_single_receive_loop_and_releases_resources_once()
    {
        var runtime = new BlockingRuntime();
        var lifetime = new TrackingLifetime();
        var presenter = new TrackingPresenter();
        var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            presenter,
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.ReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(viewModel.DisposeAsync().AsTask(), viewModel.DisposeAsync().AsTask());

        Assert.Equal([false], runtime.UpdateRequests);
        Assert.Equal(1, runtime.MaximumConcurrentReceives);
        Assert.True(runtime.ReceiveCancelled);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(1, presenter.DisposeCount);
    }

    [Fact]
    public async Task Dispose_releases_ownership_before_waiting_for_receive_loop()
    {
        var receiveReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new OwnershipBoundRuntime(receiveReleased.Task);
        var lifetime = new SignalingLifetime(receiveReleased);
        var presenter = new TrackingPresenter();
        var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            presenter,
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.ReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, lifetime.DisposeCount);
        Assert.True(runtime.ReceiveExited);
        Assert.Equal(1, presenter.DisposeCount);
    }

    [Fact]
    public async Task Network_failure_is_observed_sanitized_and_releases_ownership()
    {
        var runtime = new FailingRuntime();
        var lifetime = new TrackingLifetime();
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("连接已中断。", viewModel.StatusMessage);
        Assert.Equal(ConnectionQualityLevel.Disconnected, viewModel.ConnectionQuality.Level);
        Assert.DoesNotContain("sensitive", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Framebuffer_response_updates_connection_quality_from_tracked_request()
    {
        var time = new ManualTimestampProvider();
        var runtime = new TimedFrameRuntime(time, TimeSpan.FromMilliseconds(80));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            timeProvider: time);
        var qualityUpdated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteSessionViewModel.ConnectionQuality) &&
                viewModel.ConnectionQuality.Level == ConnectionQualityLevel.Good)
            {
                qualityUpdated.TrySetResult();
            }
        };

        await viewModel.StartAsync(CancellationToken.None);
        await qualityUpdated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(80, viewModel.ConnectionQuality.ResponseMilliseconds);
        Assert.Equal("良好 · 80 ms", viewModel.ConnectionQuality.DisplayText);
    }

    [Fact]
    public async Task Framebuffer_response_excludes_request_queue_wait_before_protocol_write_completion()
    {
        var time = new ManualTimestampProvider();
        var runtime = new TimedFrameRuntime(
            time,
            responseTime: TimeSpan.FromMilliseconds(80),
            requestQueueTime: TimeSpan.FromMilliseconds(300));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            timeProvider: time);
        var qualityUpdated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteSessionViewModel.ConnectionQuality) &&
                viewModel.ConnectionQuality.ResponseMilliseconds is not null)
            {
                qualityUpdated.TrySetResult();
            }
        };

        await viewModel.StartAsync(CancellationToken.None);
        await qualityUpdated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(80, viewModel.ConnectionQuality.ResponseMilliseconds);
    }

    [Fact]
    public async Task Failed_framebuffer_request_does_not_mark_pacer_baseline()
    {
        var time = new ManualTimestampProvider();
        var pacer = new FramebufferRequestPacer(time);
        await using var viewModel = CreateRefreshViewModel(
            new FailedRequestRuntime(),
            FrameRefreshPolicy.Fixed(60),
            time,
            pacer);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(pacer.HasRequestBaseline);
    }

    [Fact]
    public async Task Canceled_framebuffer_request_does_not_mark_pacer_baseline()
    {
        var time = new ManualTimestampProvider();
        var pacer = new FramebufferRequestPacer(time);
        var runtime = new BlockingRequestRuntime();
        var viewModel = CreateRefreshViewModel(
            runtime,
            FrameRefreshPolicy.Fixed(60),
            time,
            pacer);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(pacer.HasRequestBaseline);
    }

    [Fact]
    public async Task Receive_failure_diagnostic_preserves_nested_aggregate_base_exception()
    {
        var runtime = new FailingWithExceptionRuntime(
            new AggregateException(
                new AggregateException(
                    new IOException("sensitive endpoint"))));
        var diagnosticSink = new RecordingDiagnosticSink();
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        Assert.IsType<IOException>(diagnostic.Exception);
        Assert.DoesNotContain(
            diagnostic.Fields ?? [],
            item => item.Name == "PresentationStage");
    }

    [Fact]
    public async Task Receive_protocol_failure_exports_only_safe_public_fingerprint_fields()
    {
        const string secret = "decoder leaked-secret-payload clipboard=private-clipboard host=private-host frame=17,34,51,68";
        var failure = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.DecoderFailure,
            RfbProtocolReadStage.FramebufferRectanglePayload,
            0xFA,
            -239,
            7);
        var protocolException = RfbProtocolException.Create(secret, failure);
        using var redactor = new SecretRedactor();
        var safeDiagnosticSink = new InMemorySafeDiagnosticSink(redactor);
        var diagnosticSink = new RecordingDiagnosticSink(safeDiagnosticSink);
        await using var viewModel = new RemoteSessionViewModel(
            new FailingWithExceptionRuntime(protocolException),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        Assert.Same(protocolException, diagnostic.Exception);
        var fields = Assert.IsAssignableFrom<IReadOnlyList<DiagnosticField>>(diagnostic.Fields);
        Assert.Equal(
            [
                ("ProtocolFailureKind", "DecoderFailure"),
                ("ProtocolReadStage", "FramebufferRectanglePayload"),
                ("ServerMessageType", "0xFA"),
                ("EncodingName", "Cursor"),
                ("RectangleIndex", "7"),
            ],
            fields.Select(field => (field.Name, field.Value)).ToArray());
        Assert.All(fields, field => Assert.Equal(DiagnosticFieldCategory.Public, field.Category));

        var stored = Assert.Single(
            safeDiagnosticSink.Snapshot(),
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        Assert.Equal(nameof(RfbProtocolException), stored.Exception?.Type);
        Assert.Equal($"0x{protocolException.HResult:X8}", stored.Exception?.HResult);
        var storedJson = JsonSerializer.Serialize(stored);
        Assert.DoesNotContain("leaked-secret-payload", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-clipboard", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-host", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("17,34,51,68", storedJson, StringComparison.Ordinal);
        Assert.Equal(new RemoteFramebufferSize(1, 1), viewModel.FramebufferSize);
        Assert.Equal("连接已中断。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Remote_session_closed_failure_reports_specific_user_message_and_diagnostics()
    {
        var protocolException = RfbProtocolException.Create(
            "remote closed",
            new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.RemoteSessionClosed,
                RfbProtocolReadStage.ArdStateChangePayload,
                0x14));
        var diagnosticSink = new RecordingDiagnosticSink();
        await using var viewModel = new RemoteSessionViewModel(
            new FailingWithExceptionRuntime(protocolException),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("远程主机已结束会话。", viewModel.StatusMessage);
        Assert.Equal("远程主机已结束共享会话。", viewModel.Error?.UserMessage);
        Assert.Equal("REMOTE_SESSION_INTERRUPTED", viewModel.Error?.Code);
        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        Assert.Equal(
            [
                ("ProtocolFailureKind", "RemoteSessionClosed"),
                ("ProtocolReadStage", "ArdStateChangePayload"),
                ("ServerMessageType", "0x14"),
            ],
            diagnostic.Fields!.Select(field => (field.Name, field.Value)).ToArray());
    }

    [Fact]
    public async Task Receive_protocol_failure_exports_only_present_optional_fields()
    {
        var diagnosticSink = new RecordingDiagnosticSink();
        await using var viewModel = new RemoteSessionViewModel(
            new FailingWithExceptionRuntime(RfbProtocolException.Create(
                "decoder failure",
                new RfbProtocolFailureInfo(
                    RfbProtocolFailureKind.UnsupportedEncoding,
                    EncodingId: 16))),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        var fields = Assert.IsAssignableFrom<IReadOnlyList<DiagnosticField>>(diagnostic.Fields);
        Assert.Equal(
            [("ProtocolFailureKind", "UnsupportedEncoding"), ("EncodingName", "ZRLE")],
            fields.Select(field => (field.Name, field.Value)).ToArray());
        Assert.All(fields, field => Assert.Equal(DiagnosticFieldCategory.Public, field.Category));
    }

    [Theory]
    [MemberData(nameof(NonProtocolReceiveFailures))]
    public async Task Non_protocol_receive_failures_do_not_export_protocol_fields(Exception exception)
    {
        var diagnosticSink = new RecordingDiagnosticSink();
        await using var viewModel = new RemoteSessionViewModel(
            new FailingWithExceptionRuntime(exception),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        Assert.Empty(diagnostic.Fields ?? []);
    }

    [Fact]
    public async Task Receive_protocol_failure_uses_outermost_failure_before_eof_base_exception()
    {
        const string secret = "decoder leaked-secret-payload inner-chain-secret";
        var protocolException = new RfbProtocolException(
                secret,
                new EndOfStreamException("eof inner-chain-secret"))
            .WithContext(new RfbProtocolFailureInfo(RfbProtocolFailureKind.TruncatedRead))
            .WithContext(new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.DecoderFailure,
                RfbProtocolReadStage.FramebufferRectanglePayload,
                0,
                7,
                3));
        Assert.IsType<EndOfStreamException>(protocolException.GetBaseException());
        using var redactor = new SecretRedactor();
        var safeDiagnosticSink = new InMemorySafeDiagnosticSink(redactor);
        var diagnosticSink = new RecordingDiagnosticSink(safeDiagnosticSink);
        await using var viewModel = new RemoteSessionViewModel(
            new FailingWithExceptionRuntime(new IOException(
                "transport wrapper inner-chain-secret",
                protocolException)),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        Assert.Same(protocolException, diagnostic.Exception);
        Assert.Equal(
            [
                ("ProtocolFailureKind", "TruncatedRead"),
                ("ProtocolReadStage", "FramebufferRectanglePayload"),
                ("ServerMessageType", "0x00"),
                ("EncodingName", "Other"),
                ("RectangleIndex", "3"),
            ],
            diagnostic.Fields!.Select(field => (field.Name, field.Value)).ToArray());
        var storedJson = JsonSerializer.Serialize(Assert.Single(safeDiagnosticSink.Snapshot()));
        Assert.DoesNotContain("leaked-secret-payload", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("inner-chain-secret", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(EndOfStreamException), storedJson, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> NonProtocolReceiveFailures =>
    [
        [new IOException("network details")],
        [new AggregateException(new OperationCanceledException("receive canceled"))],
    ];

    [Fact]
    public async Task Receive_failure_with_ten_thousand_exception_nodes_completes_safely()
    {
        var diagnostic = await RecordReceiveFailureAsync(
            WrapExceptionChain(new IOException("leaf"), 10_000));

        Assert.Empty(diagnostic.Fields ?? []);
    }

    [Fact]
    public async Task Receive_failure_repeated_aggregate_reference_is_visited_once()
    {
        var sharedBranch = WrapExceptionChain(new IOException("shared leaf"), 200);
        var firstFailure = RfbProtocolException.Create(
            "first failure",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.MalformedClipboard));
        var diagnostic = await RecordReceiveFailureAsync(new AggregateException(
            sharedBranch,
            sharedBranch,
            firstFailure));

        var field = Assert.Single(diagnostic.Fields!);
        Assert.Equal("ProtocolFailureKind", field.Name);
        Assert.Equal("MalformedClipboard", field.Value);
    }

    [Fact]
    public async Task Receive_failure_aggregate_branches_use_original_depth_first_order()
    {
        var firstFailure = RfbProtocolException.Create(
            "first failure",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.UnsupportedEncoding));
        var secondFailure = RfbProtocolException.Create(
            "second failure",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.DecoderFailure));
        var diagnostic = await RecordReceiveFailureAsync(new AggregateException(
            new IOException("first wrapper", firstFailure),
            secondFailure));

        var field = Assert.Single(diagnostic.Fields!);
        Assert.Equal("ProtocolFailureKind", field.Name);
        Assert.Equal("UnsupportedEncoding", field.Value);
    }

    [Fact]
    public async Task Receive_failure_aggregate_cross_reference_follows_first_subtree_before_siblings()
    {
        var sharedFailure = RfbProtocolException.Create(
            "shared failure",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.MalformedClipboard));
        var firstBranch = new IOException("first branch", sharedFailure);
        var siblingFailure = RfbProtocolException.Create(
            "sibling failure",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.UnsupportedEncoding));
        var diagnostic = await RecordReceiveFailureAsync(new AggregateException(
            firstBranch,
            siblingFailure,
            sharedFailure));

        var field = Assert.Single(diagnostic.Fields!);
        Assert.Equal("ProtocolFailureKind", field.Name);
        Assert.Equal("MalformedClipboard", field.Value);
    }

    [Fact]
    public async Task Receive_failure_prefers_outer_protocol_failure()
    {
        var protocolException = RfbProtocolException.Create(
                "outer failure",
                new RfbProtocolFailureInfo(RfbProtocolFailureKind.TruncatedRead))
            .WithContext(new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.DecoderFailure,
                RfbProtocolReadStage.ClipboardPayload));
        var diagnostic = await RecordReceiveFailureAsync(protocolException);

        Assert.Equal(
            [("ProtocolFailureKind", "TruncatedRead"), ("ProtocolReadStage", "ClipboardPayload")],
            diagnostic.Fields!.Select(field => (field.Name, field.Value)).ToArray());
    }

    [Fact]
    public async Task Receive_failure_does_not_scan_protocol_failure_beyond_node_limit()
    {
        var protocolException = RfbProtocolException.Create(
            "too deep",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.DecoderFailure));
        var diagnostic = await RecordReceiveFailureAsync(
            WrapExceptionChain(protocolException, 1_000));

        Assert.Empty(diagnostic.Fields ?? []);
    }

    [Fact]
    public async Task Receive_failure_wide_aggregate_does_not_find_failure_beyond_node_budget()
    {
        var children = new Exception[10_000];
        for (var index = 0; index < children.Length - 1; index++)
        {
            children[index] = new IOException($"branch-{index}");
        }

        children[^1] = RfbProtocolException.Create(
            "too wide",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.DecoderFailure));
        var diagnostic = await RecordReceiveFailureAsync(new AggregateException(children));

        Assert.Empty(diagnostic.Fields ?? []);
    }

    [Fact]
    public async Task Receive_failure_wide_aggregate_finds_first_child_failure()
    {
        var children = new Exception[10_000];
        children[0] = RfbProtocolException.Create(
            "first child",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.UnsupportedEncoding));
        for (var index = 1; index < children.Length; index++)
        {
            children[index] = new IOException($"branch-{index}");
        }

        var diagnostic = await RecordReceiveFailureAsync(new AggregateException(children));

        var field = Assert.Single(diagnostic.Fields!);
        Assert.Equal("ProtocolFailureKind", field.Name);
        Assert.Equal("UnsupportedEncoding", field.Value);
    }

    [Fact]
    public async Task Receive_failure_repeated_references_do_not_consume_unique_node_budget()
    {
        var shared = new IOException("shared");
        var children = new Exception[501];
        Array.Fill(children, shared, 0, 500);
        children[^1] = RfbProtocolException.Create(
            "after repeated references",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.MalformedClipboard));
        var diagnostic = await RecordReceiveFailureAsync(new AggregateException(children));

        var field = Assert.Single(diagnostic.Fields!);
        Assert.Equal("ProtocolFailureKind", field.Name);
        Assert.Equal("MalformedClipboard", field.Value);
    }

    [Fact]
    public async Task Receive_failure_does_not_inspect_unbounded_repeated_aggregate_edges()
    {
        var shared = new IOException("shared");
        var children = new Exception[10_000];
        Array.Fill(children, shared, 0, children.Length - 1);
        children[^1] = RfbProtocolException.Create(
            "beyond edge budget",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.DecoderFailure));
        var diagnostic = await RecordReceiveFailureAsync(new AggregateException(children));

        Assert.Empty(diagnostic.Fields ?? []);
    }

    [Fact]
    public async Task ThrowingDiagnosticSinkCannotSuppressTerminalErrorOrOwnershipRelease()
    {
        var lifetime = new TrackingLifetime();
        await using var viewModel = new RemoteSessionViewModel(
            new FailingRuntime(),
            lifetime,
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: new ThrowingSink());

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("连接已中断。", viewModel.StatusMessage);
        Assert.Equal("REMOTE_SESSION_INTERRUPTED", viewModel.Error?.Code);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task InterruptedErrorFlowsThroughCardRetryToCloseAndOneNewConnection()
    {
        var lifetime = new TrackingLifetime();
        var viewModel = new RemoteSessionViewModel(
            new FailingRuntime(),
            lifetime,
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);
        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        var card = ConnectionErrorViewModel.FromError(viewModel.Error!);
        var sequence = new List<string>();
        var connectCalls = 0;
        var openCalls = 0;
        var retry = new RemoteSessionRetryAction(
            async () =>
            {
                sequence.Add("close");
                await viewModel.DisposeAsync();
            },
            _ =>
            {
                sequence.Add("connect");
                connectCalls++;
                openCalls++;
                return Task.CompletedTask;
            });
        var handler = new ConnectionErrorActionHandler(
            [new(ConnectionErrorActionKind.Retry, retry.ExecuteAsync)]);

        await handler.HandleAsync(
            card.Actions.Single(action => action.Kind == ConnectionErrorActionKind.Retry).Kind,
            CancellationToken.None);

        Assert.Equal(["close", "connect"], sequence);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(1, connectCalls);
        Assert.Equal(1, openCalls);
    }

    [Fact]
    public void ThrowingDiagnosticSinkCannotSuppressInputErrorPresentation()
    {
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: new ThrowingSink());

        viewModel.ObserveInputFailure(new InvalidOperationException("raw"));

        Assert.Equal("REMOTE_INPUT_FAILED", viewModel.Error?.Code);
    }

    [Fact]
    public async Task Receive_failure_releases_ownership_when_status_dispatcher_fails()
    {
        var lifetime = new TrackingLifetime();
        var viewModel = new RemoteSessionViewModel(
            new FailingRuntime(),
            lifetime,
            new TrackingPresenter(),
            new FailingDispatcher(),
            clipboardBridge: null);

        try
        {
            await viewModel.StartAsync(CancellationToken.None);
            await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, lifetime.DisposeCount);
        }
        finally
        {
            _ = await Assert.ThrowsAsync<AggregateException>(
                () => viewModel.DisposeAsync().AsTask());
        }

        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Dispose_releases_presenter_through_ui_dispatcher()
    {
        var dispatcher = new TrackingDispatcher();
        var presenter = new DispatcherBoundPresenter(() => dispatcher.IsDispatching);
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new TrackingLifetime(),
            presenter,
            dispatcher,
            clipboardBridge: null);

        await viewModel.DisposeAsync();

        Assert.True(presenter.WasDisposed);
    }

    [Fact]
    public async Task Dispose_releases_ownership_when_presenter_cleanup_fails()
    {
        var lifetime = new TrackingLifetime();
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            lifetime,
            new ThrowingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        _ = await Assert.ThrowsAsync<AggregateException>(() => viewModel.DisposeAsync().AsTask());

        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Dispose_aggregates_synchronous_ownership_failure_and_disposes_ownership_once()
    {
        var lifetime = new SynchronouslyThrowingLifetime();
        var presenter = new TrackingPresenter();
        var viewModel = new RemoteSessionViewModel(
            new FailingRuntime(),
            lifetime,
            presenter,
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.DisposeAsync().AsTask());

        Assert.Contains(
            failure.InnerExceptions,
            exception => exception is InvalidOperationException
                && exception.Message == "ownership cleanup failed");
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(1, presenter.DisposeCount);
    }

    [Fact]
    public async Task Incremental_update_is_requested_only_after_framebuffer_messages()
    {
        var runtime = new ScriptedRuntime(
            new RemoteClipboardMessage("clipboard"),
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(1, 1),
                [0, 0, 0, 255],
                4,
                [new RemoteRectangle(0, 0, 1, 1)]));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Unlimited);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync();

        Assert.Equal([(false, 0), (true, 2)], runtime.UpdateRequests);
    }

    [Fact]
    public async Task Next_pull_request_waits_until_frame_presentation_completes()
    {
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(1, 1),
                [0, 0, 0, 255],
                4,
                [new RemoteRectangle(0, 0, 1, 1)]));
        var dispatcher = new QueuedDispatcher();
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            dispatcher,
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Unlimited);

        await viewModel.StartAsync(CancellationToken.None);
        await dispatcher.InvocationQueued.WaitAsync(TimeSpan.FromSeconds(2));

        var requestsWhilePresentationBlocked = runtime.UpdateRequests.ToArray();
        dispatcher.ReleaseAll();
        await runtime.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([(false, 0)], requestsWhilePresentationBlocked);
        Assert.Equal([(false, 0), (true, 1)], runtime.UpdateRequests);
    }

    [Fact]
    public async Task Framebuffer_size_tracks_the_latest_frame()
    {
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(2, 3),
                new byte[24],
                8,
                [new RemoteRectangle(0, 0, 2, 3)]));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new RemoteFramebufferSize(2, 3), viewModel.FramebufferSize);
    }

    [Fact]
    public async Task Metadata_resize_requests_full_new_size_then_resumes_incremental_updates()
    {
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(2, 1),
                new byte[8],
                8,
                []),
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(2, 1),
                new byte[8],
                8,
                [new RemoteRectangle(0, 0, 2, 1)]));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Unlimited);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.ThirdRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync();

        Assert.Equal([(false, 0), (false, 1), (true, 2)], runtime.UpdateRequests);
    }

    [Fact]
    public async Task Cursor_only_update_is_published_and_requests_the_next_incremental_update()
    {
        var owner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var runtime = new ScriptedRuntime(
            new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 1, 1, owner, 4)));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Unlimited);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(viewModel.RemoteCursor);
        Assert.Equal([1, 2, 3, 4], viewModel.RemoteCursor.Bgra32.ToArray());
        Assert.False(owner.IsDisposed);
        Assert.Equal([(false, 0), (true, 1)], runtime.UpdateRequests);
    }

    [Fact]
    public async Task Cursor_only_update_contributes_bytes_without_counting_a_frame_or_primary_encoding()
    {
        var time = new ManualTimestampProvider();
        var runtime = new CursorStatisticsRuntime(time);
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Unlimited,
            timeProvider: time);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteSessionViewModel.Performance) &&
                viewModel.Performance.ReceiveBytesPerSecond == 1024)
            {
                published.TrySetResult();
            }
        };

        await viewModel.StartAsync(CancellationToken.None);
        await published.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, viewModel.Performance.ActualFramesPerSecond);
        Assert.Null(viewModel.Performance.PrimaryFramebufferEncoding);
    }

    [Fact]
    public async Task Hidden_cursor_releases_the_previous_cursor_and_clears_ui_state()
    {
        var visibleOwner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var hiddenOwner = new TrackingMemoryOwner([]);
        var runtime = new ScriptedRuntime(
            new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 1, 1, visibleOwner, 4)),
            new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 0, 0, hiddenOwner, 0)));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(viewModel.RemoteCursor);
        Assert.True(visibleOwner.IsDisposed);
        Assert.True(hiddenOwner.IsDisposed);
    }

    [Fact]
    public async Task Dispose_releases_the_current_cursor_once()
    {
        var owner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var runtime = new ScriptedRuntime(
            new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 1, 1, owner, 4)));
        var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(viewModel.DisposeAsync().AsTask(), viewModel.DisposeAsync().AsTask());

        Assert.True(owner.IsDisposed);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task Permanent_presenter_failure_cancels_receive_releases_transport_and_completes()
    {
        var frameOwner = new TrackingMemoryOwner([0, 0, 0, 255]);
        var runtime = new SingleFrameThenBlockingRuntime(frameOwner);
        var lifetime = new TrackingLifetime();
        var presenter = new PermanentlyFailingPresenter();
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            presenter,
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Unlimited);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.Completion.IsCompletedSuccessfully);
        Assert.Equal("画面呈现失败，会话正在关闭。", viewModel.StatusMessage);
        Assert.Equal(1, presenter.PresentCount);
        Assert.Equal(1, runtime.ReceiveCount);
        Assert.False(runtime.ReceiveCancelled);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.True(frameOwner.IsDisposed);
        Assert.Equal(1, frameOwner.DisposeCount);
    }

    [Fact]
    public async Task Presentation_failure_diagnostic_preserves_stage_and_hresult()
    {
        var frameOwner = new TrackingMemoryOwner([17, 34, 51, 68]);
        var runtime = new SingleFrameThenBlockingRuntime(frameOwner);
        var presenter = new FailingWithExceptionPresenter(
            new D3DPresentationException(
                D3DPresentationStage.Present1,
                new COMException(
                    "sensitive native details",
                    unchecked((int)0x887A0001))));
        using var redactor = new SecretRedactor();
        var safeDiagnosticSink = new InMemorySafeDiagnosticSink(redactor);
        var diagnosticSink = new RecordingDiagnosticSink(safeDiagnosticSink);
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            presenter,
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_PRESENTATION_FAILED");
        Assert.Equal("Remote session loop failed.", diagnostic.Message);
        var exception = Assert.IsType<D3DPresentationException>(diagnostic.Exception);
        Assert.Equal(unchecked((int)0x887A0001), exception.HResult);
        var fields = Assert.IsAssignableFrom<IReadOnlyList<DiagnosticField>>(diagnostic.Fields);
        var field = Assert.Single(fields);
        Assert.Equal("PresentationStage", field.Name);
        Assert.Equal("Present1", field.Value);
        Assert.Equal(DiagnosticFieldCategory.Public, field.Category);
        Assert.DoesNotContain(
            "sensitive native details",
            diagnostic.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            fields,
            item => item.Value?.Contains("sensitive native details", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(
            fields,
            item => item.Value?.Contains("17, 34, 51, 68", StringComparison.Ordinal) is true);

        var storedEvent = Assert.Single(
            safeDiagnosticSink.Snapshot(),
            item => item.Code == "REMOTE_PRESENTATION_FAILED");
        var storedField = Assert.Single(storedEvent.Fields);
        Assert.Equal("PresentationStage", storedField.Name);
        Assert.Equal("Present1", storedField.Value);
        Assert.Equal(DiagnosticFieldCategory.Public, storedField.Category);
        Assert.Equal(nameof(D3DPresentationException), storedEvent.Exception?.Type);
        Assert.Equal("0x887A0001", storedEvent.Exception?.HResult);
        var storedJson = JsonSerializer.Serialize(storedEvent);
        Assert.DoesNotContain(
            "sensitive native details",
            storedJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[17,34,51,68]", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("17, 34, 51, 68", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ESIzRA==", storedJson, StringComparison.Ordinal);
        Assert.Equal(1, presenter.PresentCount);
        Assert.True(frameOwner.IsDisposed);
        Assert.Equal(1, frameOwner.DisposeCount);
    }

    [Fact]
    public async Task Presenter_failure_still_terminates_when_status_dispatcher_fails()
    {
        var frameOwner = new TrackingMemoryOwner([0, 0, 0, 255]);
        var runtime = new SingleFrameThenBlockingRuntime(frameOwner);
        var lifetime = new TrackingLifetime();
        var presenter = new PermanentlyFailingPresenter();
        var dispatcher = new PresentationThenFailingDispatcher();
        var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            presenter,
            dispatcher,
            clipboardBridge: null,
            diagnosticSink: null,
            initialRefreshPolicy: FrameRefreshPolicy.Unlimited);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.Completion.IsCompletedSuccessfully);
        Assert.False(runtime.ReceiveCancelled);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(1, presenter.PresentCount);
        Assert.True(frameOwner.IsDisposed);

        _ = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.DisposeAsync().AsTask());
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Hidden_cursor_owner_is_released_once_when_ui_dispatch_fails()
    {
        var cursorOwner = new TrackingMemoryOwner([]);
        var lifetime = new TrackingLifetime();
        var viewModel = new RemoteSessionViewModel(
            new ScriptedRuntime(
                new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 0, 0, cursorOwner, 0))),
            lifetime,
            new TrackingPresenter(),
            new FailingDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, cursorOwner.DisposeCount);
        Assert.Equal(1, lifetime.DisposeCount);

        _ = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.DisposeAsync().AsTask());
        Assert.Equal(1, cursorOwner.DisposeCount);
    }

    [Fact]
    public async Task Mixed_update_releases_cursor_when_framebuffer_size_dispatch_fails()
    {
        var frameOwner = new TrackingMemoryOwner(new byte[8]);
        var cursorOwner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(2, 1),
                frameOwner,
                8,
                8,
                [new RemoteRectangle(0, 0, 2, 1)],
                new RemoteCursorUpdate(0, 0, 1, 1, cursorOwner, 4)));
        var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new FailingDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, cursorOwner.DisposeCount);
        Assert.Equal(1, frameOwner.DisposeCount);

        _ = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Mixed_update_releases_cursor_when_frame_packet_validation_fails()
    {
        var frameOwner = new TrackingMemoryOwner(new byte[4]);
        var cursorOwner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(1, 1),
                frameOwner,
                4,
                1,
                [new RemoteRectangle(0, 0, 1, 1)],
                new RemoteCursorUpdate(0, 0, 1, 1, cursorOwner, 4)));
        var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, cursorOwner.DisposeCount);
        Assert.Equal(1, frameOwner.DisposeCount);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task Mixed_update_keeps_cursor_owner_when_dispatch_action_transfers_before_task_cancels()
    {
        var frameOwner = new TrackingMemoryOwner([0, 0, 0, 255]);
        var cursorOwner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(1, 1),
                frameOwner,
                4,
                4,
                [new RemoteRectangle(0, 0, 1, 1)],
                new RemoteCursorUpdate(0, 0, 1, 1, cursorOwner, 4)));
        var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new ActionThenCanceledDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(viewModel.RemoteCursor);
        Assert.False(cursorOwner.IsDisposed);

        _ = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.DisposeAsync().AsTask());
        Assert.Equal(1, cursorOwner.DisposeCount);
    }

    private static async Task<SafeDiagnosticEventInput> RecordReceiveFailureAsync(
        Exception exception)
    {
        var diagnosticSink = new RecordingDiagnosticSink();
        await using var viewModel = new RemoteSessionViewModel(
            new FailingWithExceptionRuntime(exception),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        return Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
    }

    [Theory]
    [InlineData(RfbProtocolFailureKind.ArdEncryptionNegotiation)]
    [InlineData(RfbProtocolFailureKind.ArdEncryptionPacket)]
    [InlineData(RfbProtocolFailureKind.ArdEncryptionIntegrity)]
    public async Task Encryption_failures_emit_only_safe_protocol_metadata(
        RfbProtocolFailureKind kind)
    {
        var diagnostic = await RecordReceiveFailureAsync(
            RfbProtocolException.Create(
                "ARD encryption failed.",
                new RfbProtocolFailureInfo(kind)));

        var field = Assert.Single(diagnostic.Fields!);
        Assert.Equal("ProtocolFailureKind", field.Name);
        Assert.Equal(kind.ToString(), field.Value);
        Assert.DoesNotContain(
            diagnostic.Fields!,
            candidate =>
                candidate.Name.Contains("Key", StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.Contains("Iv", StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Encryption_failures_with_packet_context_export_only_safe_ard_packet_metadata()
    {
        const string secret = "ard-encryption-secret-marker";
        var diagnostic = await RecordReceiveFailureAsync(
            RfbProtocolException.Create(
                $"ARD encryption failed: {secret}",
                new RfbProtocolFailureInfo(
                    RfbProtocolFailureKind.ArdEncryptionPacket,
                    RfbProtocolReadStage.ServerMessageType,
                    ArdEncryptionStage: ArdEncryptedPacketFailureStage.Padding,
                    ArdEncryptionDirection: ArdEncryptedPacketDirection.Receive,
                    ArdEncryptionSequence: 1,
                    ArdCiphertextLength: 48)));

        var fields = diagnostic.Fields!;
        Assert.Collection(
            fields,
            field =>
            {
                Assert.Equal("ProtocolFailureKind", field.Name);
                Assert.Equal("ArdEncryptionPacket", field.Value);
                Assert.Equal(DiagnosticFieldCategory.Public, field.Category);
            },
            field =>
            {
                Assert.Equal("ProtocolReadStage", field.Name);
                Assert.Equal("ServerMessageType", field.Value);
                Assert.Equal(DiagnosticFieldCategory.Public, field.Category);
            },
            field =>
            {
                Assert.Equal("ArdEncryptionStage", field.Name);
                Assert.Equal("Padding", field.Value);
                Assert.Equal(DiagnosticFieldCategory.Public, field.Category);
            },
            field =>
            {
                Assert.Equal("ArdEncryptionDirection", field.Name);
                Assert.Equal("Receive", field.Value);
                Assert.Equal(DiagnosticFieldCategory.Public, field.Category);
            },
            field =>
            {
                Assert.Equal("ArdEncryptionSequence", field.Name);
                Assert.Equal("1", field.Value);
                Assert.Equal(DiagnosticFieldCategory.Public, field.Category);
            },
            field =>
            {
                Assert.Equal("ArdCiphertextLength", field.Name);
                Assert.Equal("48", field.Value);
                Assert.Equal(DiagnosticFieldCategory.Public, field.Category);
            });
        Assert.DoesNotContain(
            fields,
            candidate =>
                candidate.Name.StartsWith("Key", StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.StartsWith("Iv", StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.StartsWith("Payload", StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.StartsWith("CiphertextBytes", StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.StartsWith("Plaintext", StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.StartsWith("Digest", StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.StartsWith("Hash", StringComparison.OrdinalIgnoreCase) ||
                candidate.Value?.Contains(secret, StringComparison.Ordinal) == true);
    }

    private static Exception WrapExceptionChain(Exception innermost, int wrapperCount)
    {
        var exception = innermost;
        for (var index = 0; index < wrapperCount; index++)
        {
            exception = new IOException($"wrapper-{index}", exception);
        }

        return exception;
    }

    private sealed class BlockingRuntime : IRemoteSessionRuntime
    {
        private int _receives;
        private int _activeReceives;
        public TaskCompletionSource ReceiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<bool> UpdateRequests { get; } = [];
        public int MaximumConcurrentReceives { get; private set; }
        public bool ReceiveCancelled { get; private set; }
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            UpdateRequests.Add(incremental);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _receives);
            var active = Interlocked.Increment(ref _activeReceives);
            MaximumConcurrentReceives = Math.Max(MaximumConcurrentReceives, active);
            ReceiveEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
            catch (OperationCanceledException)
            {
                ReceiveCancelled = true;
                throw;
            }
            finally
            {
                _ = Interlocked.Decrement(ref _activeReceives);
            }
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailedRequestRuntime : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("request write failed"));
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<RemoteServerMessage>(new InvalidOperationException());
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingRequestRuntime : IRemoteSessionRuntime
    {
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public async ValueTask RequestFramebufferUpdateAsync(
            bool incremental,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<RemoteServerMessage>(new InvalidOperationException());
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class RefreshPolicyRuntime(
        int frameCount,
        RemoteRuntimePerformanceSnapshot? performance = null,
        SemaphoreSlim? presentationGate = null) : IRemoteSessionRuntime
    {
        private int _received;
        private int _requests;

        public TaskCompletionSource SecondRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FourthRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount => Volatile.Read(ref _requests);

        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public RemoteRuntimePerformanceSnapshot PerformanceSnapshot =>
            performance ?? RemoteRuntimePerformanceSnapshot.Empty;

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            var requestCount = Interlocked.Increment(ref _requests);
            if (requestCount == 2)
            {
                SecondRequest.TrySetResult();
            }
            else if (requestCount == 4)
            {
                FourthRequest.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            var received = Interlocked.Increment(ref _received);
            if (received <= frameCount)
            {
                if (received > 1 && presentationGate is not null)
                {
                    await presentationGate.WaitAsync(cancellationToken);
                }

                return new RemoteFramebufferMessage(
                    new RemoteFramebufferSize(1, 1),
                    [0, 0, 0, 255],
                    4,
                    [new RemoteRectangle(0, 0, 1, 1)],
                    cursor: null,
                    new RemoteUpdateStatistics(1024, new Dictionary<int, int> { [0] = 1 }));
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class QueuedWriteTimingRuntime(
        ManualTimestampProvider timeProvider,
        TimeSpan firstQueueDelay) : IRemoteSessionRuntime
    {
        private int _requests;
        private int _receives;

        public List<long> WriteCompletedAt { get; } = [];
        public TaskCompletionSource SecondWriteCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _requests) == 1)
            {
                timeProvider.Advance(firstQueueDelay);
            }

            WriteCompletedAt.Add(timeProvider.GetTimestamp());
            if (WriteCompletedAt.Count == 2)
            {
                SecondWriteCompleted.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _receives) == 1)
            {
                return new RemoteFramebufferMessage(
                    new RemoteFramebufferSize(1, 1),
                    [0, 0, 0, 255],
                    4,
                    [new RemoteRectangle(0, 0, 1, 1)]);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class SignalingPresenter(SemaphoreSlim presented) : IFramePresenter
    {
        public void Resize(int width, int height) { }

        public void Present(
            ReadOnlySpan<byte> bgra32,
            int stride,
            IReadOnlyList<RemoteRectangle> dirtyRectangles) => presented.Release();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisplayCapabilityRuntime(int maximum) : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public RemoteDisplayCapabilities DisplayCapabilities { get; } = new(maximum);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) => ValueTask.FromResult<RemoteServerMessage>(new RemoteBellMessage());
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class QualityCapabilityRuntime(int maximum, int? displayMaximum = null) : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public RemoteDisplayCapabilities DisplayCapabilities { get; } = new(displayMaximum);
        public ArdDisplayCapabilities QualityCapabilities { get; } = new(
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            false,
            false,
            maximum);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) => ValueTask.FromResult<RemoteServerMessage>(new RemoteBellMessage());
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class CursorStatisticsRuntime(ManualTimestampProvider time) : IRemoteSessionRuntime
    {
        private int _receiveCount;
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _receiveCount) == 1)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                return new RemoteCursorMessage(
                    new RemoteCursorUpdate(0, 0, 0, 0, []),
                    new RemoteUpdateStatistics(
                        1024,
                        new Dictionary<int, int> { [(int)WinARD.Remote.Protocol.Encodings.RfbEncodingType.Cursor] = 1 }));
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingRuntime : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<RemoteServerMessage>(new IOException("sensitive endpoint"));
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingWithExceptionRuntime(Exception exception) : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<RemoteServerMessage>(exception);
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class OwnershipBoundRuntime(Task receiveReleased) : IRemoteSessionRuntime
    {
        public TaskCompletionSource ReceiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReceiveExited { get; private set; }
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveEntered.TrySetResult();
            await receiveReleased;
            ReceiveExited = true;
            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedRuntime(params RemoteServerMessage[] messages) : IRemoteSessionRuntime
    {
        private readonly Queue<RemoteServerMessage> _messages = new(messages);
        private int _receiveCount;
        public TaskCompletionSource MessagesConsumed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(bool Incremental, int ReceiveCount)> UpdateRequests { get; } = [];
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            UpdateRequests.Add((incremental, _receiveCount));
            if (UpdateRequests.Count == 2)
            {
                SecondRequest.TrySetResult();
            }
            else if (UpdateRequests.Count == 3)
            {
                ThirdRequest.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_messages.TryDequeue(out var message))
            {
                _receiveCount++;
                if (_messages.Count == 0)
                {
                    MessagesConsumed.TrySetResult();
                }

                return message;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleFrameThenBlockingRuntime(System.Buffers.IMemoryOwner<byte> owner) : IRemoteSessionRuntime
    {
        public int ReceiveCount { get; private set; }
        public bool ReceiveCancelled { get; private set; }
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveCount++;
            if (ReceiveCount == 1)
            {
                return new RemoteFramebufferMessage(
                    new RemoteFramebufferSize(1, 1),
                    owner,
                    4,
                    4,
                    [new RemoteRectangle(0, 0, 1, 1)]);
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
            catch (OperationCanceledException)
            {
                ReceiveCancelled = true;
                throw;
            }
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class TimedFrameRuntime(
        ManualTimestampProvider timeProvider,
        TimeSpan responseTime,
        TimeSpan? requestQueueTime = null) : IRemoteSessionRuntime
    {
        private int _receiveCount;
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(
            bool incremental,
            CancellationToken cancellationToken)
        {
            timeProvider.Advance(requestQueueTime ?? TimeSpan.Zero);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _receiveCount) == 1)
            {
                timeProvider.Advance(responseTime);
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
    }

    private sealed class ManualTimestampProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) =>
            _timestamp = checked(_timestamp + (long)duration.TotalMilliseconds);
    }

    private sealed class TrackingLifetime : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class TrackingMemoryOwner(byte[] bytes) : System.Buffers.IMemoryOwner<byte>
    {
        private byte[]? _bytes = bytes;

        public bool IsDisposed => _bytes is null;
        public int DisposeCount { get; private set; }
        public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(TrackingMemoryOwner));

        public void Dispose()
        {
            DisposeCount++;
            _bytes = null;
        }
    }

    private sealed class SignalingLifetime(TaskCompletionSource receiveReleased) : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            receiveReleased.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SynchronouslyThrowingLifetime : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            throw new InvalidOperationException("ownership cleanup failed");
        }
    }

    private sealed class TrackingPresenter : IFramePresenter
    {
        public int DisposeCount { get; private set; }
        public void Resize(int width, int height) { }
        public void Present(ReadOnlySpan<byte> bgra32, int stride, IReadOnlyList<RemoteRectangle> dirtyRectangles) { }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class ThrowingPresenter : IFramePresenter
    {
        public void Resize(int width, int height) { }
        public void Present(ReadOnlySpan<byte> bgra32, int stride, IReadOnlyList<RemoteRectangle> dirtyRectangles) { }
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("presenter cleanup failed"));
    }

    private sealed class PermanentlyFailingPresenter : IFramePresenter
    {
        public int PresentCount { get; private set; }
        public void Resize(int width, int height) { }

        public void Present(
            ReadOnlySpan<byte> bgra32,
            int stride,
            IReadOnlyList<RemoteRectangle> dirtyRectangles)
        {
            PresentCount++;
            throw new InvalidOperationException("sensitive GPU failure");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingWithExceptionPresenter(Exception exception) : IFramePresenter
    {
        public int PresentCount { get; private set; }

        public void Resize(int width, int height) { }

        public void Present(
            ReadOnlySpan<byte> bgra32,
            int stride,
            IReadOnlyList<RemoteRectangle> dirtyRectangles)
        {
            PresentCount++;
            throw exception;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DispatcherBoundPresenter(Func<bool> isDispatching) : IFramePresenter
    {
        public bool WasDisposed { get; private set; }
        public void Resize(int width, int height) { }
        public void Present(ReadOnlySpan<byte> bgra32, int stride, IReadOnlyList<RemoteRectangle> dirtyRectangles) { }

        public ValueTask DisposeAsync()
        {
            if (!isDispatching())
            {
                throw new InvalidOperationException("Presenter disposal must run through the UI dispatcher.");
            }

            WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingDispatcher : IUiDispatcher
    {
        public bool IsDispatching { get; private set; }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsDispatching = true;
            try
            {
                action();
            }
            finally
            {
                IsDispatching = false;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CountingDispatcher : IUiDispatcher
    {
        public int InvocationCount { get; private set; }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly object _sync = new();
        private readonly List<(Action Action, TaskCompletionSource Completion)> _queued = [];
        private readonly TaskCompletionSource _invocationQueued = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _releaseImmediately;

        public Task InvocationQueued => _invocationQueued.Task;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_releaseImmediately)
                {
                    action();
                    return Task.CompletedTask;
                }

                var completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _queued.Add((action, completion));
                _invocationQueued.TrySetResult();
                return completion.Task;
            }
        }

        public void ReleaseAll()
        {
            (Action Action, TaskCompletionSource Completion)[] queued;
            lock (_sync)
            {
                _releaseImmediately = true;
                queued = [.. _queued];
                _queued.Clear();
            }

            foreach (var invocation in queued)
            {
                invocation.Action();
                invocation.Completion.TrySetResult();
            }
        }
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

    private sealed class ThrowingSink : ISafeDiagnosticSink
    {
        public void Write(SafeDiagnosticEventInput diagnosticEvent) =>
            throw new InvalidOperationException("sink failed");

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
    }

    private sealed class RecordingDiagnosticSink(ISafeDiagnosticSink? inner = null)
        : ISafeDiagnosticSink
    {
        public List<SafeDiagnosticEventInput> Events { get; } = [];

        public void Write(SafeDiagnosticEventInput diagnosticEvent)
        {
            Events.Add(diagnosticEvent);
            inner?.Write(diagnosticEvent);
        }

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
    }

    private sealed class FailingDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("dispatcher unavailable"));
    }

    private sealed class PresentationThenFailingDispatcher : IUiDispatcher
    {
        private int _invocationCount;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _invocationCount) == 1)
            {
                action();
                return Task.CompletedTask;
            }

            return Task.FromException(new InvalidOperationException("status dispatcher unavailable"));
        }
    }

    private sealed class ActionThenCanceledDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            action();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        }
    }
}
