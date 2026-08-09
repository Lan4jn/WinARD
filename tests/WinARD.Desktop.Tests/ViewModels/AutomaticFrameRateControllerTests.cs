using WinARD.Desktop.ViewModels;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class AutomaticFrameRateControllerTests
{
    private static readonly FrameRateLoadSample Good = new(
        Response: TimeSpan.FromMilliseconds(1),
        Presentation: TimeSpan.FromMilliseconds(1),
        ChangedAreaRatio: 0.1,
        InputWriteLatency: TimeSpan.FromMilliseconds(1));

    private static readonly FrameRateLoadSample Bad = new(
        Response: TimeSpan.FromMilliseconds(1),
        Presentation: TimeSpan.FromMilliseconds(1),
        ChangedAreaRatio: 0.1,
        InputWriteLatency: TimeSpan.FromMilliseconds(60));

    [Fact]
    public void Starts_at_sixty_frames_per_second()
    {
        var controller = new AutomaticFrameRateController(new ManualTimestampProvider(), remoteMaximum: null);

        Assert.Equal(60, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Reliable_remote_maximum_sets_initial_tier_and_caps_available_tiers()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: 45);

        Assert.Equal(45, controller.CurrentFramesPerSecond);

        ObserveGoodStreak(controller, time);

        Assert.Equal(45, controller.CurrentFramesPerSecond);
    }

    [Theory]
    [InlineData(59, 45)]
    [InlineData(60, 60)]
    [InlineData(74, 60)]
    [InlineData(121, 60)]
    public void Remote_maximum_removes_higher_tiers(int remoteMaximum, int expectedInitial)
    {
        var controller = new AutomaticFrameRateController(new ManualTimestampProvider(), remoteMaximum);

        Assert.Equal(expectedInitial, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Remote_maximum_caps_later_upgrades_to_the_highest_available_tier()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: 74);

        ObserveGoodStreak(controller, time);

        Assert.Equal(60, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Remote_maximum_below_the_minimum_tier_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AutomaticFrameRateController(new ManualTimestampProvider(), remoteMaximum: 29));
    }

    [Fact]
    public void Three_bad_samples_drop_one_step()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: null);

        Assert.False(controller.Observe(Bad));
        Assert.False(controller.Observe(Bad));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(controller.Observe(Bad));

        Assert.Equal(45, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Consecutive_downgrades_are_at_least_one_second_apart()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: null);
        ObserveBadStreak(controller);
        Assert.Equal(45, controller.CurrentFramesPerSecond);

        ObserveBadStreak(controller);
        Assert.Equal(45, controller.CurrentFramesPerSecond);

        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(controller.Observe(Bad));
        Assert.Equal(45, controller.CurrentFramesPerSecond);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(controller.Observe(Bad));
        Assert.Equal(30, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Thirty_good_samples_over_five_seconds_raise_only_one_step()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: null);

        for (var index = 0; index < 29; index++)
        {
            Assert.False(controller.Observe(Good));
        }

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.True(controller.Observe(Good));
        Assert.Equal(75, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Good_samples_do_not_raise_before_both_thresholds_are_met()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: null);

        for (var index = 0; index < 30; index++)
        {
            controller.Observe(Good);
        }

        Assert.Equal(60, controller.CurrentFramesPerSecond);

        var secondTime = new ManualTimestampProvider();
        var secondController = new AutomaticFrameRateController(secondTime, remoteMaximum: null);
        for (var index = 0; index < 28; index++)
        {
            secondController.Observe(Good);
        }

        secondTime.Advance(TimeSpan.FromSeconds(5));
        Assert.False(secondController.Observe(Good));
        Assert.Equal(60, secondController.CurrentFramesPerSecond);

        Assert.True(secondController.Observe(Good));
        Assert.Equal(75, secondController.CurrentFramesPerSecond);
    }

    [Fact]
    public void Neutral_sample_resets_consecutive_bad_samples()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: null);
        var neutral = new FrameRateLoadSample(
            Response: TimeSpan.FromMilliseconds(10),
            Presentation: TimeSpan.FromMilliseconds(10),
            ChangedAreaRatio: 0.1,
            InputWriteLatency: TimeSpan.Zero);

        controller.Observe(Bad);
        controller.Observe(Bad);
        controller.Observe(neutral);
        controller.Observe(Bad);
        controller.Observe(Bad);
        controller.Observe(Bad);

        Assert.Equal(60, controller.CurrentFramesPerSecond);

        Assert.True(controller.Observe(Bad));
        Assert.Equal(45, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Neutral_sample_resets_the_good_stability_window()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: null);
        var neutral = new FrameRateLoadSample(
            Response: TimeSpan.FromMilliseconds(10),
            Presentation: TimeSpan.FromMilliseconds(40),
            ChangedAreaRatio: 0.1,
            InputWriteLatency: TimeSpan.FromMilliseconds(30));

        for (var index = 0; index < 29; index++)
        {
            controller.Observe(Good);
        }

        time.Advance(TimeSpan.FromSeconds(5));
        controller.Observe(neutral);

        for (var index = 0; index < 29; index++)
        {
            controller.Observe(Good);
        }

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(60, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Frame_rate_is_bounded_by_thirty_and_one_hundred_twenty()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: null);

        ObserveBadStreak(controller);
        time.Advance(TimeSpan.FromSeconds(1));
        ObserveBadStreak(controller);
        Assert.Equal(30, controller.CurrentFramesPerSecond);

        time.Advance(TimeSpan.FromSeconds(1));
        ObserveBadStreak(controller);
        Assert.Equal(30, controller.CurrentFramesPerSecond);

        for (var step = 0; step < 6; step++)
        {
            ObserveGoodStreak(controller, time);
        }

        Assert.Equal(120, controller.CurrentFramesPerSecond);

        ObserveGoodStreak(controller, time);
        Assert.Equal(120, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Observe_returns_true_only_when_the_tier_changes()
    {
        var controller = new AutomaticFrameRateController(new ManualTimestampProvider(), remoteMaximum: null);

        Assert.False(controller.Observe(Bad));
        Assert.False(controller.Observe(Bad));
        Assert.True(controller.Observe(Bad));
        Assert.False(controller.Observe(Bad));
    }

    [Fact]
    public void Classification_uses_ema_with_latest_sample_weight_of_one_quarter()
    {
        var controller = new AutomaticFrameRateController(new ManualTimestampProvider(), remoteMaximum: null);
        var neutral = Good with { InputWriteLatency = TimeSpan.FromMilliseconds(40) };
        var overloaded = Good with { InputWriteLatency = TimeSpan.FromMilliseconds(80) };

        Assert.False(controller.Observe(neutral));
        Assert.False(controller.Observe(overloaded));
        Assert.False(controller.Observe(overloaded));
        Assert.True(controller.Observe(overloaded));

        Assert.Equal(45, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Response_classification_uses_ema_instead_of_the_latest_raw_sample()
    {
        var controller = new AutomaticFrameRateController(new ManualTimestampProvider(), remoteMaximum: null);
        var baseline = Good with { ChangedAreaRatio = 0.5 };
        var overloaded = baseline with { Response = TimeSpan.FromMilliseconds(40) };
        controller.Observe(baseline);

        for (var index = 0; index < 4; index++)
        {
            Assert.False(controller.Observe(overloaded));
        }

        Assert.True(controller.Observe(overloaded));
        Assert.Equal(45, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Presentation_classification_uses_ema_instead_of_the_latest_raw_sample()
    {
        var controller = new AutomaticFrameRateController(new ManualTimestampProvider(), remoteMaximum: null);
        var overloaded = Good with { Presentation = TimeSpan.FromMilliseconds(20) };
        controller.Observe(Good);

        for (var index = 0; index < 5; index++)
        {
            Assert.False(controller.Observe(overloaded));
        }

        Assert.True(controller.Observe(overloaded));
        Assert.Equal(45, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Changed_area_classification_uses_ema_instead_of_the_latest_raw_sample()
    {
        var controller = new AutomaticFrameRateController(new ManualTimestampProvider(), remoteMaximum: null);
        var baseline = Good with
        {
            Response = TimeSpan.FromMilliseconds(30),
            ChangedAreaRatio = 0.1,
        };
        var overloaded = baseline with { ChangedAreaRatio = 1 };
        controller.Observe(baseline);

        for (var index = 0; index < 4; index++)
        {
            Assert.False(controller.Observe(overloaded));
        }

        Assert.True(controller.Observe(overloaded));
        Assert.Equal(45, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Input_latency_at_fifty_milliseconds_is_bad()
    {
        var controller = new AutomaticFrameRateController(new ManualTimestampProvider(), remoteMaximum: null);
        var boundary = Good with { InputWriteLatency = TimeSpan.FromMilliseconds(50) };

        Assert.False(controller.Observe(boundary));
        Assert.False(controller.Observe(boundary));
        Assert.True(controller.Observe(boundary));
    }

    [Fact]
    public void Presentation_bad_threshold_is_strictly_greater_than_seventy_five_percent()
    {
        var boundary = Good with
        {
            Presentation = TimeSpan.FromSeconds((1d / 60) * 0.75),
        };
        var aboveBoundary = boundary with
        {
            Presentation = boundary.Presentation + TimeSpan.FromTicks(1),
        };
        var boundaryController = new AutomaticFrameRateController(
            new ManualTimestampProvider(),
            remoteMaximum: null);
        var aboveController = new AutomaticFrameRateController(
            new ManualTimestampProvider(),
            remoteMaximum: null);

        ObserveBadStreak(boundaryController, boundary);
        Assert.Equal(60, boundaryController.CurrentFramesPerSecond);

        ObserveBadStreak(aboveController, aboveBoundary);
        Assert.Equal(45, aboveController.CurrentFramesPerSecond);
    }

    [Fact]
    public void Changed_area_response_bad_threshold_uses_inclusive_area_and_strict_response()
    {
        var boundary = Good with
        {
            Response = TimeSpan.FromSeconds((1d / 60) * 1.25),
            ChangedAreaRatio = 0.5,
        };
        var aboveBoundary = boundary with { Response = boundary.Response + TimeSpan.FromTicks(1) };
        var boundaryController = new AutomaticFrameRateController(
            new ManualTimestampProvider(),
            remoteMaximum: null);
        var aboveController = new AutomaticFrameRateController(
            new ManualTimestampProvider(),
            remoteMaximum: null);

        ObserveBadStreak(boundaryController, boundary);
        Assert.Equal(60, boundaryController.CurrentFramesPerSecond);

        ObserveBadStreak(aboveController, aboveBoundary);
        Assert.Equal(45, aboveController.CurrentFramesPerSecond);
    }

    [Fact]
    public void Good_threshold_rejects_input_at_twenty_milliseconds()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: null);
        var boundary = Good with { InputWriteLatency = TimeSpan.FromMilliseconds(20) };

        for (var index = 0; index < 29; index++)
        {
            controller.Observe(boundary);
        }

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(controller.Observe(boundary));
        Assert.Equal(60, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Good_threshold_accepts_presentation_and_response_at_their_boundaries()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: null);
        var boundary = Good with
        {
            Presentation = TimeSpan.FromSeconds((1d / 60) * 0.50),
            Response = TimeSpan.FromSeconds((1d / 60) * 0.75),
        };

        for (var index = 0; index < 29; index++)
        {
            Assert.False(controller.Observe(boundary));
        }

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(controller.Observe(boundary));
        Assert.Equal(75, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Invalid_sample_does_not_change_existing_ema_or_streak_state()
    {
        var controller = new AutomaticFrameRateController(new ManualTimestampProvider(), remoteMaximum: null);
        var changedAreaOverload = Good with
        {
            Response = TimeSpan.FromMilliseconds(30),
            ChangedAreaRatio = 0.75,
        };

        Assert.False(controller.Observe(changedAreaOverload));
        Assert.False(controller.Observe(changedAreaOverload));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => controller.Observe(changedAreaOverload with { ChangedAreaRatio = double.NaN }));

        Assert.True(controller.Observe(changedAreaOverload));
        Assert.Equal(45, controller.CurrentFramesPerSecond);
    }

    [Fact]
    public void Without_observations_the_tier_does_not_change()
    {
        var time = new ManualTimestampProvider();
        var controller = new AutomaticFrameRateController(time, remoteMaximum: null);

        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(60, controller.CurrentFramesPerSecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Invalid_samples_are_rejected_without_changing_the_tier(int invalidSample)
    {
        var controller = new AutomaticFrameRateController(new ManualTimestampProvider(), remoteMaximum: null);
        var sample = invalidSample switch
        {
            0 => Good with { Response = TimeSpan.FromTicks(-1) },
            1 => Good with { Presentation = TimeSpan.FromTicks(-1) },
            2 => Good with { InputWriteLatency = TimeSpan.FromTicks(-1) },
            3 => Good with { ChangedAreaRatio = -0.01 },
            4 => Good with { ChangedAreaRatio = 1.01 },
            5 => Good with { ChangedAreaRatio = double.NaN },
            _ => Good with { ChangedAreaRatio = double.PositiveInfinity },
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Observe(sample));
        Assert.Equal(60, controller.CurrentFramesPerSecond);
    }

    private static void ObserveBadStreak(AutomaticFrameRateController controller)
    {
        ObserveBadStreak(controller, Bad);
    }

    private static void ObserveBadStreak(
        AutomaticFrameRateController controller,
        FrameRateLoadSample sample)
    {
        controller.Observe(sample);
        controller.Observe(sample);
        controller.Observe(sample);
    }

    private static void ObserveGoodStreak(
        AutomaticFrameRateController controller,
        ManualTimestampProvider time)
    {
        for (var index = 0; index < 33; index++)
        {
            controller.Observe(Good);
        }

        time.Advance(TimeSpan.FromSeconds(5));
        controller.Observe(Good);
    }

    private sealed class ManualTimestampProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan value) => _timestamp += value.Ticks;
    }
}
