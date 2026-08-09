using WinARD.Desktop.ViewModels;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class ConnectionQualityTrackerTests
{
    [Fact]
    public void Starts_unmeasured_and_ignores_response_without_request()
    {
        var tracker = new ConnectionQualityTracker(new ManualTimeProvider());

        Assert.Equal(ConnectionQualityLevel.Unmeasured, tracker.Current.Level);
        Assert.Equal("等待测量", tracker.Current.DisplayText);
        Assert.Null(tracker.CompleteResponse());
        Assert.Equal(ConnectionQualityLevel.Unmeasured, tracker.Current.Level);
    }

    [Theory]
    [InlineData(149, ConnectionQualityLevel.Good, "良好 · 149 ms")]
    [InlineData(150, ConnectionQualityLevel.Fair, "一般 · 150 ms")]
    [InlineData(500, ConnectionQualityLevel.Fair, "一般 · 500 ms")]
    [InlineData(501, ConnectionQualityLevel.Poor, "较差 · 501 ms")]
    public void Classifies_response_time_boundaries(
        int milliseconds,
        ConnectionQualityLevel expectedLevel,
        string expectedText)
    {
        var time = new ManualTimeProvider();
        var tracker = new ConnectionQualityTracker(time);

        tracker.BeginRequest();
        time.Advance(TimeSpan.FromMilliseconds(milliseconds));
        var snapshot = Assert.IsType<ConnectionQualitySnapshot>(tracker.CompleteResponse());

        Assert.Equal(expectedLevel, snapshot.Level);
        Assert.Equal(milliseconds, snapshot.ResponseMilliseconds);
        Assert.Equal(expectedText, snapshot.DisplayText);
    }

    [Fact]
    public void Uses_exponential_moving_average_after_first_sample()
    {
        var time = new ManualTimeProvider();
        var tracker = new ConnectionQualityTracker(time);

        tracker.BeginRequest();
        time.Advance(TimeSpan.FromMilliseconds(100));
        _ = tracker.CompleteResponse();
        tracker.BeginRequest();
        time.Advance(TimeSpan.FromMilliseconds(500));
        var snapshot = Assert.IsType<ConnectionQualitySnapshot>(tracker.CompleteResponse());

        Assert.Equal(200, snapshot.ResponseMilliseconds);
        Assert.Equal(ConnectionQualityLevel.Fair, snapshot.Level);
        Assert.Equal("一般 · 200 ms", snapshot.DisplayText);
    }

    [Fact]
    public void Classification_uses_the_same_rounded_value_that_is_displayed()
    {
        var time = new ManualTimeProvider();
        var tracker = new ConnectionQualityTracker(time);

        tracker.BeginRequest();
        time.Advance(TimeSpan.FromMilliseconds(149));
        _ = tracker.CompleteResponse();
        tracker.BeginRequest();
        time.Advance(TimeSpan.FromMilliseconds(151));
        var snapshot = Assert.IsType<ConnectionQualitySnapshot>(tracker.CompleteResponse());

        Assert.Equal(150, snapshot.ResponseMilliseconds);
        Assert.Equal(ConnectionQualityLevel.Fair, snapshot.Level);
        Assert.Equal("一般 · 150 ms", snapshot.DisplayText);
    }

    [Fact]
    public void Disconnect_clears_pending_request_and_reports_disconnected()
    {
        var time = new ManualTimeProvider();
        var tracker = new ConnectionQualityTracker(time);
        tracker.BeginRequest();

        var snapshot = tracker.Disconnect();
        time.Advance(TimeSpan.FromMilliseconds(10));

        Assert.Equal(ConnectionQualityLevel.Disconnected, snapshot.Level);
        Assert.Null(snapshot.ResponseMilliseconds);
        Assert.Equal("已断开", snapshot.DisplayText);
        Assert.Null(tracker.CompleteResponse());
    }

    [Fact]
    public void Samples_and_disconnect_receive_monotonically_increasing_sequences()
    {
        var time = new ManualTimeProvider();
        var tracker = new ConnectionQualityTracker(time);

        tracker.BeginRequest();
        time.Advance(TimeSpan.FromMilliseconds(10));
        var first = Assert.IsType<ConnectionQualitySnapshot>(tracker.CompleteResponse());
        tracker.BeginRequest();
        time.Advance(TimeSpan.FromMilliseconds(20));
        var second = Assert.IsType<ConnectionQualitySnapshot>(tracker.CompleteResponse());
        var disconnected = tracker.Disconnect();

        Assert.True(first.SampleSequence < second.SampleSequence);
        Assert.True(second.SampleSequence < disconnected.SampleSequence);
    }

    [Fact]
    public void Publication_gate_rejects_older_sample_after_newer_sample_was_published()
    {
        var gate = new ConnectionQualityPublicationGate();
        var older = new ConnectionQualitySnapshot(
            ConnectionQualityLevel.Good,
            80,
            "良好 · 80 ms",
            SampleSequence: 1);
        var newer = new ConnectionQualitySnapshot(
            ConnectionQualityLevel.Fair,
            200,
            "一般 · 200 ms",
            SampleSequence: 2);

        Assert.True(gate.TryAccept(newer));
        Assert.False(gate.TryAccept(older));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) =>
            _timestamp = checked(_timestamp + (long)duration.TotalMilliseconds);
    }
}
