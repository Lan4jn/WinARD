using WinARD.Application.Quality;
using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Application.Tests.Quality;

public sealed class AdaptiveQualityControllerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Scroll_enters_motion_and_targets_sixty_frames_per_second()
    {
        var controller = CreateController();

        var decision = controller.Observe(Observation(Epoch, scrolling: true));

        Assert.Equal(QualityContentState.Motion, decision.ContentState);
        Assert.Equal(60, decision.TargetFramesPerSecond);
        Assert.Equal(QualityDecisionReason.Initial, decision.Reason);
    }

    [Fact]
    public void Fixed_quality_table_contains_only_the_five_approved_combinations()
    {
        Assert.Collection(
            AdaptiveQualityController.QualityTable,
            value => Assert.Equal((QualityLevel.Q0, QualityColor.Full32, QualityScale.Native, 60), value),
            value => Assert.Equal((QualityLevel.Q1, QualityColor.Color16, QualityScale.Native, 60), value),
            value => Assert.Equal((QualityLevel.Q2, QualityColor.Color16, QualityScale.Percent75, 60), value),
            value => Assert.Equal((QualityLevel.Q3, QualityColor.Color16, QualityScale.Percent50, 45), value),
            value => Assert.Equal((QualityLevel.Q4, QualityColor.Grayscale, QualityScale.Percent50, 30), value));
    }

    [Fact]
    public void Two_distinct_over_target_windows_degrade_only_one_adjacent_level()
    {
        var controller = CreateController(target: 1_000);
        controller.Observe(Observation(Epoch, average: 1_200));
        var duplicateWindow = controller.Observe(Observation(Epoch.AddSeconds(1), average: 1_200));
        var secondWindow = controller.Observe(Observation(Epoch.AddSeconds(5), average: 1_200));

        Assert.Equal(QualityLevel.Q0, duplicateWindow.Level);
        Assert.Equal(QualityLevel.Q1, secondWindow.Level);
        Assert.Equal(QualityDecisionReason.SustainedOverTarget, secondWindow.Reason);
    }

    [Theory]
    [InlineData(1_500, 0, 0)]
    [InlineData(0, 67, 0)]
    [InlineData(0, 0, 3)]
    public void Severe_bandwidth_response_or_backlog_degrades_immediately(
        double average,
        double responseMilliseconds,
        int pending)
    {
        var controller = CreateController(target: 1_000);

        var decision = controller.Observe(Observation(
            Epoch,
            average: average,
            peak: average,
            response: TimeSpan.FromMilliseconds(responseMilliseconds),
            pending: pending));

        Assert.Equal(QualityLevel.Q1, decision.Level);
        Assert.Equal(QualityDecisionReason.SevereOverTarget, decision.Reason);
    }

    [Fact]
    public void Recovery_requires_fifteen_stable_seconds_and_five_second_upgrade_cooldown()
    {
        var controller = CreateController(target: 1_000);
        controller.Observe(Observation(Epoch, average: 1_500));
        var before = controller.Observe(Observation(Epoch.AddSeconds(14), average: 700));
        var start = controller.Observe(Observation(Epoch.AddSeconds(15), average: 700));
        var upgraded = controller.Observe(Observation(Epoch.AddSeconds(30), average: 700));

        Assert.Equal(QualityLevel.Q1, before.Level);
        Assert.Equal(QualityLevel.Q1, start.Level);
        Assert.Equal(QualityLevel.Q0, upgraded.Level);
        Assert.Equal(QualityDecisionReason.StableRecovery, upgraded.Reason);
    }

    [Fact]
    public void Repeated_severe_signals_respect_two_second_degrade_cooldown()
    {
        var controller = CreateController(target: 1_000);
        controller.Observe(Observation(Epoch, average: 1_500));
        var immediate = controller.Observe(Observation(Epoch.AddSeconds(1), average: 1_500));
        var next = controller.Observe(Observation(Epoch.AddSeconds(2), average: 1_500));

        Assert.Equal(QualityLevel.Q1, immediate.Level);
        Assert.Equal(QualityLevel.Q2, next.Level);
    }

    [Fact]
    public void Content_transitions_through_interactive_recovery_and_idle()
    {
        var controller = CreateController();
        Assert.Equal(QualityContentState.Interactive,
            controller.Observe(Observation(Epoch, dirty: .1, sinceInput: TimeSpan.FromMilliseconds(100))).ContentState);
        Assert.Equal(QualityContentState.Interactive,
            controller.Observe(Observation(Epoch.AddMilliseconds(1), dirty: .01)).ContentState);
        Assert.Equal(QualityContentState.Recovery,
            controller.Observe(Observation(Epoch.AddMilliseconds(601), dirty: .01)).ContentState);
        Assert.Equal(QualityContentState.Idle,
            controller.Observe(Observation(Epoch.AddMilliseconds(602), dirty: .01)).ContentState);
    }

    [Fact]
    public void Since_last_input_contributes_to_the_single_six_hundred_millisecond_recovery_period()
    {
        var controller = CreateController();
        controller.Observe(Observation(Epoch, dirty: .01, sinceInput: TimeSpan.Zero));

        var decision = controller.Observe(Observation(
            Epoch.AddMilliseconds(600),
            dirty: .01,
            sinceInput: TimeSpan.FromMilliseconds(600)));

        Assert.Equal(QualityContentState.Recovery, decision.ContentState);
    }

    [Fact]
    public void Dirty_motion_requires_sixty_milliseconds_of_continuous_high_coverage()
    {
        var controller = CreateController();

        Assert.Equal(QualityContentState.Interactive,
            controller.Observe(Observation(Epoch, dirty: .25)).ContentState);
        Assert.Equal(QualityContentState.Interactive,
            controller.Observe(Observation(Epoch.AddMilliseconds(1), dirty: .25)).ContentState);
        Assert.Equal(QualityContentState.Interactive,
            controller.Observe(Observation(Epoch.AddMilliseconds(59), dirty: .25)).ContentState);
        Assert.Equal(QualityContentState.Motion,
            controller.Observe(Observation(Epoch.AddMilliseconds(60), dirty: .25)).ContentState);
    }

    [Fact]
    public void Dirty_motion_timer_resets_when_coverage_drops_below_twenty_five_percent()
    {
        var controller = CreateController();
        controller.Observe(Observation(Epoch, dirty: .25));
        controller.Observe(Observation(Epoch.AddMilliseconds(30), dirty: .24));

        Assert.Equal(QualityContentState.Interactive,
            controller.Observe(Observation(Epoch.AddMilliseconds(100), dirty: .25)).ContentState);
        Assert.Equal(QualityContentState.Interactive,
            controller.Observe(Observation(Epoch.AddMilliseconds(159), dirty: .25)).ContentState);
        Assert.Equal(QualityContentState.Motion,
            controller.Observe(Observation(Epoch.AddMilliseconds(160), dirty: .25)).ContentState);
    }

    [Fact]
    public void Two_percent_is_interactive_while_coverage_below_it_is_idle()
    {

        var boundary = CreateController().Observe(Observation(Epoch, dirty: .02));
        var below = CreateController().Observe(Observation(Epoch, dirty: .0199));
        Assert.Equal(QualityContentState.Interactive, boundary.ContentState);
        Assert.Equal(QualityContentState.Idle, below.ContentState);
    }

    [Fact]
    public void Capabilities_and_decoder_gate_remove_unsafe_levels()
    {
        var unsafeController = CreateController(capabilities: Capabilities(rgb: CapabilitySupport.Unknown, scaling: CapabilitySupport.Unknown));
        var unsafeDecision = unsafeController.Observe(Observation(Epoch, average: 9_000, peak: 9_000));

        var gatedController = CreateController(
            profile: QualityProfile.Automatic,
            capabilities: Capabilities(gray: CapabilitySupport.Observed),
            gates: new QualityDecoderGates(false, true));
        for (var index = 0; index < 5; index++)
        {
            gatedController.Observe(Observation(Epoch.AddSeconds(index * 2), average: 9_000_000, peak: 9_000_000));
        }

        Assert.Equal(QualityLevel.Q0, unsafeDecision.Level);
        Assert.Equal(QualityDecisionReason.TargetUnsatisfied, unsafeDecision.Reason);
        Assert.Equal(QualityLevel.Q4, gatedController.CurrentLevel);
    }

    [Fact]
    public void A_capability_gap_never_causes_a_multi_level_jump()
    {
        var controller = CreateController(
            capabilities: Capabilities(rgb: CapabilitySupport.Unknown),
            gates: new QualityDecoderGates(false, true));

        var decision = controller.Observe(Observation(Epoch, average: 9_000, peak: 9_000));

        Assert.Equal(QualityLevel.Q0, decision.Level);
        Assert.Equal(QualityDecisionReason.TargetUnsatisfied, decision.Reason);
        Assert.False(decision.TargetSatisfied);
    }

    [Fact]
    public void Grayscale_requires_observation_gate_and_profile_permission_together()
    {
        var defaultGate = CreateController(profile: QualityProfile.Automatic);
        Assert.Equal(QualityLevel.Q3, DegradeRepeatedly(defaultGate));

        var disallowedProfile = QualityProfile.CreateCustom(
            1_000,
            QualityColor.Automatic,
            QualityScale.Automatic,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false);
        var disallowed = CreateController(
            profile: disallowedProfile,
            gates: new QualityDecoderGates(false, true));
        Assert.Equal(QualityLevel.Q3, DegradeRepeatedly(disallowed));
    }

    [Fact]
    public void Unlimited_bandwidth_never_causes_bandwidth_degradation()
    {
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Automatic,
            QualityScale.Automatic,
            FrameRefreshPolicy.Unlimited);
        var controller = CreateController(profile: profile);

        Assert.Equal(QualityLevel.Q0, DegradeRepeatedly(controller));
    }

    [Fact]
    public void Presets_enforce_their_quality_floors()
    {
        Assert.Equal(QualityLevel.Q0, DegradeRepeatedly(CreateController(profile: QualityProfile.Original)));
        Assert.Equal(QualityLevel.Q2, DegradeRepeatedly(CreateController(profile: QualityProfile.Balanced)));
        Assert.Equal(QualityLevel.Q3, DegradeRepeatedly(CreateController(profile: QualityProfile.Smooth)));
    }

    [Fact]
    public void Custom_color_scale_refresh_and_bandwidth_locks_are_respected()
    {
        var profile = QualityProfile.CreateCustom(
            1_000,
            QualityColor.Color16,
            QualityScale.Percent75,
            FrameRefreshPolicy.Fixed(45),
            allowAutomaticGrayscale: false,
            bandwidthLocked: true,
            colorLocked: true,
            scaleLocked: true,
            refreshLocked: true);
        var controller = CreateController(profile: profile);

        var decision = controller.Observe(Observation(Epoch, average: 900, peak: 900, scrolling: true));

        Assert.Equal(QualityLevel.Q2, decision.Level);
        Assert.Equal(45, decision.TargetFramesPerSecond);
        Assert.Equal(QualityDecisionReason.UserConstraint, decision.Reason);
    }

    [Fact]
    public void Bandwidth_lock_preserves_the_target_but_does_not_disable_quality_adaptation()
    {
        var profile = QualityProfile.CreateCustom(
            1_000,
            QualityColor.Automatic,
            QualityScale.Automatic,
            FrameRefreshPolicy.Automatic,
            bandwidthLocked: true);
        var controller = CreateController(profile: profile);

        var decision = controller.Observe(Observation(Epoch, average: 1_500));

        Assert.Equal(QualityLevel.Q1, decision.Level);
        Assert.Equal(QualityDecisionReason.SevereOverTarget, decision.Reason);
    }

    [Fact]
    public void Locked_dimensions_report_unsatisfied_when_no_quality_step_remains()
    {
        var profile = QualityProfile.CreateCustom(
            1_000,
            QualityColor.Color16,
            QualityScale.Percent75,
            FrameRefreshPolicy.Fixed(60),
            allowAutomaticGrayscale: false,
            bandwidthLocked: true,
            colorLocked: true,
            scaleLocked: true,
            refreshLocked: true);
        var controller = CreateController(profile: profile);

        var decision = controller.Observe(Observation(Epoch, average: 1_500));

        Assert.Equal(QualityLevel.Q2, decision.Level);
        Assert.False(decision.TargetSatisfied);
        Assert.Equal(QualityDecisionReason.TargetUnsatisfied, decision.Reason);
    }

    [Fact]
    public void Recovery_restores_one_adjacent_level_once_with_unlimited_bandwidth()
    {
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Percent50,
            FrameRefreshPolicy.Unlimited,
            allowAutomaticGrayscale: false);
        var controller = CreateController(profile: profile);
        controller.Observe(Observation(Epoch, scrolling: true));
        controller.Observe(Observation(Epoch.AddMilliseconds(1), dirty: .01));

        var recovery = controller.Observe(Observation(Epoch.AddMilliseconds(601), dirty: .01));
        var idle = controller.Observe(Observation(Epoch.AddMilliseconds(602), dirty: .01));
        var beforeStableIdle = controller.Observe(Observation(Epoch.AddMilliseconds(15_600), dirty: .01));
        var stableIdle = controller.Observe(Observation(Epoch.AddMilliseconds(15_601), dirty: .01));
        var immediateIdle = controller.Observe(Observation(Epoch.AddMilliseconds(15_602), dirty: .01));

        Assert.Equal(QualityContentState.Recovery, recovery.ContentState);
        Assert.Equal(QualityLevel.Q2, recovery.Level);
        Assert.Equal(QualityDecisionReason.StableRecovery, recovery.Reason);
        Assert.Equal(QualityContentState.Idle, idle.ContentState);
        Assert.Equal(QualityLevel.Q2, idle.Level);
        Assert.Equal(QualityLevel.Q2, beforeStableIdle.Level);
        Assert.Equal(QualityLevel.Q1, stableIdle.Level);
        Assert.Equal(QualityLevel.Q1, immediateIdle.Level);
    }

    [Theory]
    [InlineData("scroll")]
    [InlineData("drag")]
    [InlineData("dirty-motion")]
    [InlineData("interactive")]
    public void Continuous_activity_never_accumulates_stable_quality_recovery(string activity)
    {
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Percent50,
            FrameRefreshPolicy.Unlimited,
            allowAutomaticGrayscale: false);
        var controller = CreateController(profile: profile);

        QualityDecision decision;
        switch (activity)
        {
            case "scroll":
                controller.Observe(Observation(Epoch, scrolling: true));
                decision = controller.Observe(Observation(Epoch.AddSeconds(16), scrolling: true));
                break;
            case "drag":
                controller.Observe(Observation(Epoch, dragging: true));
                decision = controller.Observe(Observation(Epoch.AddSeconds(16), dragging: true));
                break;
            case "dirty-motion":
                controller.Observe(Observation(Epoch, dirty: .25));
                controller.Observe(Observation(Epoch.AddMilliseconds(60), dirty: .25));
                decision = controller.Observe(Observation(Epoch.AddSeconds(16), dirty: .25));
                break;
            default:
                controller.Observe(Observation(Epoch, dirty: .1));
                decision = controller.Observe(Observation(Epoch.AddSeconds(16), dirty: .1));
                break;
        }

        Assert.Equal(QualityLevel.Q3, decision.Level);
        Assert.NotEqual(QualityDecisionReason.StableRecovery, decision.Reason);
        Assert.Equal(
            activity == "interactive" ? QualityContentState.Interactive : QualityContentState.Motion,
            decision.ContentState);
    }

    [Fact]
    public void Severe_backlog_has_priority_over_recovery_with_unlimited_bandwidth()
    {
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Percent75,
            FrameRefreshPolicy.Unlimited,
            allowAutomaticGrayscale: false);
        var controller = CreateController(profile: profile);
        controller.Observe(Observation(Epoch, scrolling: true));
        controller.Observe(Observation(Epoch.AddMilliseconds(1), dirty: .01));

        var recovery = controller.Observe(Observation(
            Epoch.AddMilliseconds(601),
            dirty: .01,
            pending: 3));

        Assert.Equal(QualityContentState.Recovery, recovery.ContentState);
        Assert.Equal(QualityLevel.Q3, recovery.Level);
        Assert.Equal(QualityDecisionReason.SevereOverTarget, recovery.Reason);
    }

    [Fact]
    public void Recovery_does_not_upgrade_while_an_ordinary_window_is_over_target()
    {
        var profile = QualityProfile.CreateCustom(
            1_000,
            QualityColor.Color16,
            QualityScale.Percent50,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false);
        var controller = CreateController(profile: profile);
        controller.Observe(Observation(Epoch, scrolling: true));
        controller.Observe(Observation(Epoch.AddMilliseconds(1), dirty: .01));

        var recovery = controller.Observe(Observation(
            Epoch.AddMilliseconds(601),
            average: 1_200,
            dirty: .01));

        Assert.Equal(QualityContentState.Recovery, recovery.ContentState);
        Assert.Equal(QualityLevel.Q3, recovery.Level);
    }

    [Fact]
    public void Locked_fixed_refresh_is_preserved_outside_motion_and_removes_slower_levels()
    {
        var profile = QualityProfile.CreateCustom(
            1_000,
            QualityColor.Automatic,
            QualityScale.Automatic,
            FrameRefreshPolicy.Fixed(45),
            refreshLocked: true);
        var controller = CreateController(profile: profile);

        Assert.Equal(45, controller.Observe(Observation(Epoch)).TargetFramesPerSecond);
        Assert.Equal(QualityLevel.Q3, DegradeRepeatedlyFrom(controller, Epoch.AddSeconds(2)));
        var exhausted = controller.Observe(Observation(Epoch.AddSeconds(12), average: 9_000_000, peak: 9_000_000));
        Assert.Equal(45, exhausted.TargetFramesPerSecond);
        Assert.Equal(QualityDecisionReason.TargetUnsatisfied, exhausted.Reason);
    }

    [Fact]
    public void High_locked_fixed_refresh_remains_a_valid_user_constraint()
    {
        var profile = QualityProfile.CreateCustom(
            1_000,
            QualityColor.Automatic,
            QualityScale.Automatic,
            FrameRefreshPolicy.Fixed(120),
            refreshLocked: true);
        var controller = CreateController(profile: profile, capabilities: Capabilities(maximumRefresh: 90));

        var decision = controller.Observe(Observation(Epoch, scrolling: true));

        Assert.Equal(QualityLevel.Q0, decision.Level);
        Assert.Equal(60, decision.TargetFramesPerSecond);
    }

    [Fact]
    public void Severe_signal_bypasses_upgrade_cooldown_for_an_immediate_reverse_degrade()
    {
        var controller = CreateController(target: 1_000);
        controller.Observe(Observation(Epoch, average: 1_500));
        controller.Observe(Observation(Epoch.AddSeconds(1), average: 700));
        Assert.Equal(QualityLevel.Q0,
            controller.Observe(Observation(Epoch.AddSeconds(16), average: 700)).Level);

        var reverse = controller.Observe(Observation(Epoch.AddSeconds(18), average: 1_500));

        Assert.Equal(QualityLevel.Q1, reverse.Level);
        Assert.Equal(QualityDecisionReason.SevereOverTarget, reverse.Reason);
    }

    [Fact]
    public void Remote_maximum_refresh_rate_clips_state_target_without_inventing_a_level()
    {
        var controller = CreateController(capabilities: Capabilities(maximumRefresh: 30));

        var decision = controller.Observe(Observation(Epoch, scrolling: true));

        Assert.Equal(QualityLevel.Q0, decision.Level);
        Assert.Equal(30, decision.TargetFramesPerSecond);
    }

    [Fact]
    public void Interactive_target_remains_one_of_thirty_or_forty_five_when_remote_max_is_between_them()
    {
        var controller = CreateController(capabilities: Capabilities(maximumRefresh: 40));

        var decision = controller.Observe(Observation(
            Epoch,
            dirty: .1,
            sinceInput: TimeSpan.FromMilliseconds(100)));

        Assert.Equal(30, decision.TargetFramesPerSecond);
    }

    [Fact]
    public void Target_unsatisfied_holds_lowest_safe_frame_rate_and_reports_false()
    {
        var controller = CreateController(profile: QualityProfile.Smooth);
        DegradeRepeatedly(controller);

        var decision = controller.Observe(Observation(Epoch.AddSeconds(10), average: 9_000_000, scrolling: true));

        Assert.Equal(QualityLevel.Q3, decision.Level);
        Assert.Equal(30, decision.TargetFramesPerSecond);
        Assert.False(decision.TargetSatisfied);
        Assert.Equal(QualityDecisionReason.TargetUnsatisfied, decision.Reason);
    }

    [Fact]
    public void Invalid_observations_and_timestamp_regression_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Observation(Epoch, dirty: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Observation(Epoch, average: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Observation(Epoch, peak: double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => Observation(Epoch, average: 2, peak: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Observation(Epoch, response: TimeSpan.FromTicks(-1)));
        var controller = CreateController();
        controller.Observe(Observation(Epoch, dirty: .25));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            controller.Observe(Observation(Epoch.AddTicks(-1), dirty: .25)));
    }

    [Fact]
    public void Adaptive_quality_models_expose_only_the_approved_public_names()
    {
        var controllerMethods = typeof(AdaptiveQualityController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name);
        var observationProperties = typeof(QualityObservation).GetProperties().Select(property => property.Name);
        var decisionProperties = typeof(QualityDecision).GetProperties().Select(property => property.Name);

        Assert.Equal([nameof(AdaptiveQualityController.Observe)], controllerMethods);
        Assert.Contains(nameof(QualityObservation.AverageBytesPerSecond5s), observationProperties);
        Assert.Contains(nameof(QualityObservation.PeakBytesPerSecond5s), observationProperties);
        Assert.Contains(nameof(QualityObservation.PointerDragActive), observationProperties);
        Assert.Contains(nameof(QualityObservation.ScrollActive), observationProperties);
        Assert.DoesNotContain(observationProperties, name => name.Contains("5Seconds", StringComparison.Ordinal));
        Assert.Contains(nameof(QualityDecision.ContentState), decisionProperties);
        Assert.DoesNotContain("State", decisionProperties);
        Assert.DoesNotContain("TargetFps", decisionProperties);
    }

    private static QualityLevel DegradeRepeatedly(AdaptiveQualityController controller)
        => DegradeRepeatedlyFrom(controller, Epoch);

    private static QualityLevel DegradeRepeatedlyFrom(
        AdaptiveQualityController controller,
        DateTimeOffset start)
    {
        for (var index = 0; index < 5; index++)
        {
            controller.Observe(Observation(start.AddSeconds(index * 2), average: 9_000_000, peak: 9_000_000));
        }

        return controller.CurrentLevel;
    }

    private static AdaptiveQualityController CreateController(
        long target = 1_000,
        QualityProfile? profile = null,
        ArdDisplayCapabilities? capabilities = null,
        QualityDecoderGates? gates = null)
    {
        profile ??= QualityProfile.CreateCustom(
            target,
            QualityColor.Automatic,
            QualityScale.Automatic,
            FrameRefreshPolicy.Automatic);
        return new AdaptiveQualityController(profile, capabilities ?? Capabilities(), gates);
    }

    private static ArdDisplayCapabilities Capabilities(
        CapabilitySupport rgb = CapabilitySupport.Observed,
        CapabilitySupport scaling = CapabilitySupport.Observed,
        CapabilitySupport gray = CapabilitySupport.Observed,
        int? maximumRefresh = null) =>
        new(
            CapabilitySupport.Observed,
            rgb,
            scaling,
            CapabilitySupport.Observed,
            gray,
            true,
            true,
            maximumRefresh);

    private static QualityObservation Observation(
        DateTimeOffset timestamp,
        double average = 0,
        double? peak = null,
        double dirty = 0,
        TimeSpan? sinceInput = null,
        TimeSpan? response = null,
        bool dragging = false,
        bool scrolling = false,
        int pending = 0) =>
        new(
            timestamp,
            average,
            peak ?? average,
            60,
            response ?? TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            dirty,
            sinceInput ?? TimeSpan.FromSeconds(1),
            dragging,
            scrolling,
            pending);
}
