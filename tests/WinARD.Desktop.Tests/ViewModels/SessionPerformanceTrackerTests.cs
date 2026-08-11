using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Desktop.Input;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Remote.Protocol.Encodings;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class SessionPerformanceTrackerTests
{
    [Fact]
    public void One_second_window_reports_frames_bytes_and_primary_pixel_encoding()
    {
        var time = new ManualTimeProvider();
        var tracker = new SessionPerformanceTracker(
            time,
            FrameRefreshPolicy.Fixed(60),
            targetFramesPerSecond: 60);
        const long totalBytes = 6 * 1024 * 1024;
        var bytesPerFrame = totalBytes / 60;

        SessionPerformanceSnapshot snapshot = tracker.Current;
        for (var index = 0; index < 60; index++)
        {
            time.Advance(index == 59
                ? TimeSpan.FromMilliseconds(410)
                : TimeSpan.FromMilliseconds(10));
            snapshot = tracker.ObserveFrame(
                new RemoteUpdateStatistics(
                    index == 59 ? totalBytes - bytesPerFrame * 59 : bytesPerFrame,
                    new Dictionary<int, int>
                    {
                        [0] = 1,
                        [16] = 2,
                        [1] = 20,
                        [-239] = 20,
                        [-223] = 20,
                    }),
                [new RemoteRectangle(0, 0, 100, 100)],
                new RemoteFramebufferSize(100, 100),
                TimeSpan.FromMilliseconds(12),
                TimeSpan.FromMilliseconds(2),
                new RemoteRuntimePerformanceSnapshot(3, 4, index + 1),
                new RemotePointerCoalescerSnapshot(7, 1));
        }

        Assert.Equal(FrameRefreshMode.Fixed, snapshot.Mode);
        Assert.Equal(60, snapshot.TargetFramesPerSecond);
        Assert.Equal(60, snapshot.ActualFramesPerSecond);
        Assert.Equal(6 * 1024 * 1024, snapshot.ReceiveBytesPerSecond);
        Assert.Equal(16, snapshot.PrimaryFramebufferEncoding);
        Assert.Equal(12, snapshot.ResponseMilliseconds);
        Assert.Equal(3, snapshot.InputWriteMilliseconds);
        Assert.Equal(4, snapshot.InputQueueDepth);
        Assert.Equal(7, snapshot.CoalescedPointerMoves);
        Assert.Equal(60, snapshot.SampleSequence);
    }

    [Fact]
    public void Empty_tracker_starts_with_zero_measurements()
    {
        var tracker = new SessionPerformanceTracker(
            new ManualTimeProvider(),
            FrameRefreshPolicy.Automatic,
            targetFramesPerSecond: 60);

        var snapshot = tracker.Current;

        Assert.Equal(0, snapshot.ActualFramesPerSecond);
        Assert.Equal(0, snapshot.ReceiveBytesPerSecond);
        Assert.Null(snapshot.PrimaryFramebufferEncoding);
        Assert.Equal(0, snapshot.ResponseMilliseconds);
        Assert.Equal(0, snapshot.InputWriteMilliseconds);
        Assert.Equal(0, snapshot.InputQueueDepth);
        Assert.Equal(0, snapshot.CoalescedPointerMoves);
        Assert.Equal(0, snapshot.SampleSequence);
    }

    [Fact]
    public void Ard_positive_metadata_encodings_do_not_become_primary_framebuffer_encoding()
    {
        var time = new ManualTimeProvider();
        var tracker = new SessionPerformanceTracker(
            time,
            FrameRefreshPolicy.Fixed(60),
            targetFramesPerSecond: 60);
        time.Advance(TimeSpan.FromSeconds(1));

        var snapshot = tracker.ObserveFrame(
            new RemoteUpdateStatistics(
                1024,
                new Dictionary<int, int>
                {
                    [(int)RfbEncodingType.ArdDisplayInfo] = 100,
                    [(int)RfbEncodingType.ArdSessionEncryption] = 100,
                    [(int)RfbEncodingType.ArdDisplayInfo2] = 100,
                    [(int)RfbEncodingType.Raw] = 2,
                    [(int)RfbEncodingType.Zrle] = 3,
                }),
            [new RemoteRectangle(0, 0, 1, 1)],
            new RemoteFramebufferSize(1, 1),
            TimeSpan.Zero,
            TimeSpan.Zero,
            RemoteRuntimePerformanceSnapshot.Empty,
            default);

        Assert.Equal((int)RfbEncodingType.Zrle, snapshot.PrimaryFramebufferEncoding);
    }

    [Fact]
    public void Frame_counter_saturates_at_long_maximum()
    {
        var time = new ManualTimeProvider();
        var tracker = new SessionPerformanceTracker(
            time,
            FrameRefreshPolicy.Unlimited,
            targetFramesPerSecond: null,
            initialWindowFrames: long.MaxValue);
        time.Advance(TimeSpan.FromSeconds(1));

        var snapshot = tracker.ObserveFrame(
            RemoteUpdateStatistics.Empty,
            [],
            new RemoteFramebufferSize(1, 1),
            TimeSpan.Zero,
            TimeSpan.Zero,
            RemoteRuntimePerformanceSnapshot.Empty,
            default);

        Assert.Equal(int.MaxValue, snapshot.ActualFramesPerSecond);
    }

    [Fact]
    public void Diagnostic_snapshot_keeps_cumulative_encoding_counts_across_rate_windows()
    {
        var time = new ManualTimeProvider();
        var tracker = new SessionPerformanceTracker(
            time,
            FrameRefreshPolicy.Automatic,
            targetFramesPerSecond: 90);

        time.Advance(TimeSpan.FromSeconds(1));
        _ = tracker.ObserveFrame(
            new RemoteUpdateStatistics(100, new Dictionary<int, int>
            {
                [(int)RfbEncodingType.Zrle] = 10,
                [(int)RfbEncodingType.CopyRect] = 3,
                [(int)RfbEncodingType.Cursor] = 1,
                [-321] = 2,
            }),
            [new RemoteRectangle(0, 0, 1, 1)],
            new RemoteFramebufferSize(1, 1),
            TimeSpan.FromMilliseconds(17),
            TimeSpan.FromMilliseconds(5),
            new RemoteRuntimePerformanceSnapshot(3, 2, 1),
            new RemotePointerCoalescerSnapshot(42, 1));
        time.Advance(TimeSpan.FromSeconds(1));
        _ = tracker.ObserveFrame(
            new RemoteUpdateStatistics(200, new Dictionary<int, int> { [(int)RfbEncodingType.Zrle] = 8 }),
            [new RemoteRectangle(0, 0, 1, 1)],
            new RemoteFramebufferSize(1, 1),
            TimeSpan.FromMilliseconds(17),
            TimeSpan.FromMilliseconds(7),
            new RemoteRuntimePerformanceSnapshot(3, 2, 2),
            new RemotePointerCoalescerSnapshot(42, 2));
        tracker.RecordAutomaticTargetChange();

        var diagnostics = tracker.CurrentDiagnostics;

        Assert.Equal(18, diagnostics.EncodingCounts[(int)RfbEncodingType.Zrle]);
        Assert.Equal(3, diagnostics.EncodingCounts[(int)RfbEncodingType.CopyRect]);
        Assert.Equal(1, diagnostics.EncodingCounts[(int)RfbEncodingType.Cursor]);
        Assert.Equal(2, diagnostics.EncodingCounts[-321]);
        Assert.Equal(6, diagnostics.PresentationMilliseconds);
        Assert.Equal(1, diagnostics.AutomaticTargetChanges);
    }

    [Fact]
    public void Diagnostic_snapshot_bounds_unknown_encoding_keys_and_aggregates_the_overflow()
    {
        var time = new ManualTimeProvider();
        var tracker = new SessionPerformanceTracker(
            time,
            FrameRefreshPolicy.Fixed(60),
            targetFramesPerSecond: 60);
        var encodings = Enumerable.Range(10_000, 100).ToDictionary(value => value, _ => 1);
        encodings[(int)RfbEncodingType.Zrle] = 7;

        _ = tracker.ObserveFrame(
            new RemoteUpdateStatistics(1024, encodings),
            [new RemoteRectangle(0, 0, 1, 1)],
            new RemoteFramebufferSize(1, 1),
            TimeSpan.Zero,
            TimeSpan.Zero,
            RemoteRuntimePerformanceSnapshot.Empty,
            default);

        var diagnostics = tracker.CurrentDiagnostics;

        Assert.True(diagnostics.EncodingCounts.Count <= 40);
        Assert.Equal(7, diagnostics.EncodingCounts[(int)RfbEncodingType.Zrle]);
        Assert.Equal(100, diagnostics.EncodingCounts.Values.Sum() + diagnostics.OtherEncodingCount - 7);
        Assert.Equal(68, diagnostics.OtherEncodingCount);
    }

    [Fact]
    public void Quality_observation_averages_five_complete_buckets_and_excludes_current_bucket()
    {
        var time = new ManualTimeProvider(frequency: 3_000_000);
        var tracker = CreateTracker(time);

        for (var mib = 1; mib <= 5; mib++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            ObserveFrame(tracker, bytes: mib * 1024L * 1024L, dirty: [], size: new(100, 100));
        }

        time.Advance(TimeSpan.FromMilliseconds(500));
        ObserveFrame(tracker, bytes: 100 * 1024L * 1024L, dirty: [], size: new(100, 100));
        var observation = tracker.CreateQualityObservation();

        Assert.Equal(3 * 1024 * 1024, observation.AverageBytesPerSecond5s);
        Assert.Equal(5 * 1024 * 1024, observation.PeakBytesPerSecond5s);
        Assert.Equal(1, observation.ActualFramesPerSecond);
        Assert.Equal(time.GetUtcNow(), observation.Timestamp);
    }

    [Fact]
    public void Quality_observation_rolls_the_sixth_complete_bucket_into_the_bounded_window()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);

        for (var mib = 1; mib <= 6; mib++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            ObserveFrame(tracker, bytes: mib * 1024L * 1024L, dirty: [], size: new(1, 1));
        }

        var observation = tracker.CreateQualityObservation();

        Assert.Equal(4 * 1024 * 1024, observation.AverageBytesPerSecond5s);
        Assert.Equal(6 * 1024 * 1024, observation.PeakBytesPerSecond5s);
    }

    [Fact]
    public void Quality_observation_without_complete_bucket_returns_finite_zero_rates()
    {
        var tracker = CreateTracker(new ManualTimeProvider());

        var observation = tracker.CreateQualityObservation();

        Assert.Equal(0, observation.AverageBytesPerSecond5s);
        Assert.Equal(0, observation.PeakBytesPerSecond5s);
        Assert.Equal(0, observation.ActualFramesPerSecond);
        Assert.Equal(0, observation.DirtyCoverage);
    }

    [Fact]
    public void Quality_bucket_saturates_long_byte_accumulation()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        _ = tracker.ObserveNonFrameUpdate(
            new RemoteUpdateStatistics(long.MaxValue, new Dictionary<int, int>()),
            RemoteRuntimePerformanceSnapshot.Empty,
            default);
        time.Advance(TimeSpan.FromSeconds(1));
        _ = tracker.ObserveNonFrameUpdate(
            new RemoteUpdateStatistics(long.MaxValue, new Dictionary<int, int>()),
            RemoteRuntimePerformanceSnapshot.Empty,
            default);

        var observation = tracker.CreateQualityObservation();

        Assert.Equal((double)long.MaxValue, observation.AverageBytesPerSecond5s);
        Assert.True(double.IsFinite(observation.AverageBytesPerSecond5s));
    }

    [Fact]
    public void Quality_observation_counts_non_frame_bytes_without_counting_a_pixel_frame()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);

        _ = tracker.ObserveNonFrameUpdate(
            new RemoteUpdateStatistics(3 * 1024 * 1024, new Dictionary<int, int>()),
            RemoteRuntimePerformanceSnapshot.Empty,
            default);
        time.Advance(TimeSpan.FromSeconds(1));
        _ = tracker.ObserveNonFrameUpdate(
            new RemoteUpdateStatistics(2 * 1024 * 1024, new Dictionary<int, int>()),
            RemoteRuntimePerformanceSnapshot.Empty,
            default);

        var observation = tracker.CreateQualityObservation();

        Assert.Equal(5 * 1024 * 1024, observation.AverageBytesPerSecond5s);
        Assert.Equal(0, observation.ActualFramesPerSecond);
        Assert.Equal(0, observation.DirtyCoverage);
    }

    [Fact]
    public void Dirty_coverage_clips_rectangles_sums_overlap_conservatively_and_clamps()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        time.Advance(TimeSpan.FromSeconds(1));

        ObserveFrame(
            tracker,
            bytes: 0,
            dirty:
            [
                new RemoteRectangle(-10, -10, 60, 60),
                new RemoteRectangle(25, 25, 100, 100),
                new RemoteRectangle(0, 0, 50, 50),
                new RemoteRectangle(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue),
                new RemoteRectangle(0, 0, -1, 10),
            ],
            size: new(100, 100));

        Assert.Equal(1, tracker.CreateQualityObservation().DirtyCoverage);
    }

    [Fact]
    public void Dirty_coverage_uses_maximum_frame_coverage_within_a_bucket()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        ObserveFrame(tracker, 0, [new RemoteRectangle(0, 0, 10, 10)], new(100, 100));
        ObserveFrame(tracker, 0, [new RemoteRectangle(0, 0, 50, 50)], new(100, 100));
        time.Advance(TimeSpan.FromSeconds(1));
        ObserveFrame(tracker, 0, [], new(100, 100));

        Assert.Equal(0.25, tracker.CreateQualityObservation().DirtyCoverage, precision: 6);
    }

    [Fact]
    public void Invalid_frame_size_is_still_rejected_and_extreme_geometry_is_safe()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ObserveFrame(tracker, 0, [], default));

        time.Advance(TimeSpan.FromSeconds(1));
        ObserveFrame(
            tracker,
            0,
            [new RemoteRectangle(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue)],
            new RemoteFramebufferSize(int.MaxValue, int.MaxValue));
        Assert.InRange(tracker.CreateQualityObservation().DirtyCoverage, 0, 1);
    }

    [Fact]
    public void Quality_observation_reports_reliable_timings_and_explicit_decode_measurement()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        time.Advance(TimeSpan.FromSeconds(1));
        _ = tracker.ObserveFrame(
            RemoteUpdateStatistics.Empty,
            [],
            new RemoteFramebufferSize(1, 1),
            TimeSpan.FromMilliseconds(12),
            TimeSpan.FromMilliseconds(4),
            RemoteRuntimePerformanceSnapshot.Empty,
            default);

        var withoutDecode = tracker.CreateQualityObservation();
        var withDecode = tracker.CreateQualityObservation(TimeSpan.FromMilliseconds(7));

        Assert.Equal(TimeSpan.FromMilliseconds(12), withoutDecode.ResponseTime);
        Assert.Equal(TimeSpan.Zero, withoutDecode.DecodeTime);
        Assert.Equal(TimeSpan.FromMilliseconds(4), withoutDecode.PresentationTime);
        Assert.Equal(TimeSpan.FromMilliseconds(7), withDecode.DecodeTime);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tracker.CreateQualityObservation(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Input_activity_keeps_only_aggregate_state_and_has_explicit_lifecycle()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);

        tracker.RecordInputActivity(
            QualityInputActivityKind.Pointer,
            pointerDragActive: true,
            scrollActive: false,
            pendingInputCount: 3);
        time.Advance(TimeSpan.FromMilliseconds(250));

        var active = tracker.CreateQualityObservation();
        Assert.Equal(TimeSpan.FromMilliseconds(250), active.SinceLastInput);
        Assert.True(active.PointerDragActive);
        Assert.False(active.ScrollActive);
        Assert.Equal(3, active.PendingInputCount);
        Assert.Equal(QualityInputActivityKind.Pointer, tracker.CurrentActivity.LastInputKind);

        tracker.RecordInputActivity(
            QualityInputActivityKind.Keyboard,
            pointerDragActive: false,
            scrollActive: false,
            pendingInputCount: 0);
        time.Advance(TimeSpan.FromHours(1));
        var inactive = tracker.CreateQualityObservation();
        Assert.False(inactive.PointerDragActive);
        Assert.False(inactive.ScrollActive);
        Assert.Equal(0, inactive.PendingInputCount);
        Assert.DoesNotContain(
            tracker.CurrentActivity.GetType().GetProperties(),
            property => property.Name.Contains("Text", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Coordinate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Input_activity_rejects_negative_pending_count_and_clock_rollback_is_safe()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        tracker.RecordInputActivity(QualityInputActivityKind.Scroll, false, true, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tracker.RecordInputActivity(QualityInputActivityKind.Scroll, false, true, -1));
        time.SetTimestamp(-TimeSpan.TicksPerSecond);
        Assert.Equal(TimeSpan.Zero, tracker.CreateQualityObservation().SinceLastInput);
    }

    [Fact]
    public async Task Current_and_quality_observation_are_safe_during_concurrent_updates()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);

        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 1_000; index++)
            {
                ObserveFrame(tracker, 1, [], new(1, 1));
            }
        });
        var reader = Task.Run(() =>
        {
            for (var index = 0; index < 1_000; index++)
            {
                _ = tracker.Current;
                _ = tracker.CreateQualityObservation();
            }
        });

        await Task.WhenAll(writer, reader);
    }

    [Fact]
    public void Zlib_is_a_known_pixel_encoding_for_quality_tracking_regression()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        time.Advance(TimeSpan.FromSeconds(1));

        var snapshot = tracker.ObserveFrame(
            new RemoteUpdateStatistics(1, new Dictionary<int, int> { [(int)RfbEncodingType.Zlib] = 1 }),
            [],
            new RemoteFramebufferSize(1, 1),
            TimeSpan.Zero,
            TimeSpan.Zero,
            RemoteRuntimePerformanceSnapshot.Empty,
            default);

        Assert.Equal((int)RfbEncodingType.Zlib, snapshot.PrimaryFramebufferEncoding);
    }

    private static SessionPerformanceTracker CreateTracker(ManualTimeProvider time) =>
        new(time, FrameRefreshPolicy.Automatic, targetFramesPerSecond: 60);

    private static SessionPerformanceSnapshot ObserveFrame(
        SessionPerformanceTracker tracker,
        long bytes,
        IReadOnlyList<RemoteRectangle> dirty,
        RemoteFramebufferSize size) =>
        tracker.ObserveFrame(
            new RemoteUpdateStatistics(bytes, new Dictionary<int, int>()),
            dirty,
            size,
            TimeSpan.Zero,
            TimeSpan.Zero,
            RemoteRuntimePerformanceSnapshot.Empty,
            default);

    private sealed class ManualTimeProvider(long frequency = TimeSpan.TicksPerSecond) : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => frequency;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds((double)GetTimestamp() / TimestampFrequency);

        public void Advance(TimeSpan duration) =>
            Interlocked.Add(
                ref _timestamp,
                checked((long)Math.Round(
                    duration.TotalSeconds * TimestampFrequency,
                    MidpointRounding.AwayFromZero)));

        public void SetTimestamp(long timestamp) => Interlocked.Exchange(ref _timestamp, timestamp);
    }
}
