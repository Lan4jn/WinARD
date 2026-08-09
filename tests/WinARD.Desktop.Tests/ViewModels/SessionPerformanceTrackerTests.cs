using WinARD.Application.Ports;
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
