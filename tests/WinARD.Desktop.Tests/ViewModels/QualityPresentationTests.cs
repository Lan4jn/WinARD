using WinARD.Application.Quality;
using WinARD.Application.Ports;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class QualityPresentationTests
{
    private const double MiB = 1024 * 1024;

    [Fact]
    public void Snapshot_preserves_the_six_value_positional_api_and_adds_actual_state_compatibly()
    {
        var positionalConstructor = typeof(QualityPresentationSnapshot).GetConstructor(
            [
                typeof(QualityProfile),
                typeof(QualityDecision),
                typeof(QualityTransitionStatus),
                typeof(SessionPerformanceSnapshot),
                typeof(long),
                typeof(long),
            ]);
        var deconstruct = typeof(QualityPresentationSnapshot).GetMethod(
            "Deconstruct",
            [
                typeof(QualityProfile).MakeByRefType(),
                typeof(QualityDecision).MakeByRefType(),
                typeof(QualityTransitionStatus).MakeByRefType(),
                typeof(SessionPerformanceSnapshot).MakeByRefType(),
                typeof(long).MakeByRefType(),
                typeof(long).MakeByRefType(),
            ]);
        var actual = new QualityActualState(
            RemotePixelFormatKind.Rgb565,
            QualityActualEncoding.Zrle,
            FallbackUsed: false);
        var snapshot = new QualityPresentationSnapshot(
            QualityProfile.Balanced,
            decision: null,
            QualityTransitionStatus.NoChange,
            Performance(),
            epoch: 3,
            version: 5,
            actual);

        Assert.NotNull(positionalConstructor);
        Assert.NotNull(deconstruct);
        Assert.Equal(6, positionalConstructor.GetParameters().Length);
        Assert.Equal(6, deconstruct.GetParameters().Length);
        Assert.Same(actual, snapshot.Actual);
        var (profile, decision, transition, performance, epoch, version) = snapshot;
        Assert.Same(QualityProfile.Balanced, profile);
        Assert.Null(decision);
        Assert.Equal(QualityTransitionStatus.NoChange, transition);
        Assert.Same(snapshot.Performance, performance);
        Assert.Equal(3, epoch);
        Assert.Equal(5, version);
    }

    [Fact]
    public void Actual_state_preserves_its_exact_three_value_positional_api_and_pending_is_snapshot_state()
    {
        var actualConstructor = typeof(QualityActualState).GetConstructor(
            [typeof(RemotePixelFormatKind), typeof(QualityActualEncoding), typeof(bool)]);
        var actualDeconstruct = typeof(QualityActualState).GetMethod(
            "Deconstruct",
            [
                typeof(RemotePixelFormatKind).MakeByRefType(),
                typeof(QualityActualEncoding).MakeByRefType(),
                typeof(bool).MakeByRefType(),
            ]);
        var actual = new QualityActualState(
            RemotePixelFormatKind.Bgra32,
            QualityActualEncoding.Zlib,
            FallbackUsed: true);
        var snapshot = new QualityPresentationSnapshot(
            QualityProfile.Balanced,
            decision: null,
            QualityTransitionStatus.ReconnectRequired,
            Performance(),
            epoch: 1,
            version: 2,
            actual,
            pendingReconnect: true);

        Assert.NotNull(actualConstructor);
        Assert.NotNull(actualDeconstruct);
        Assert.Equal(3, actualConstructor.GetParameters().Length);
        Assert.Equal(3, actualDeconstruct.GetParameters().Length);
        Assert.DoesNotContain(
            typeof(QualityActualState).GetProperties(),
            property => property.Name.Contains("Reconnect", StringComparison.Ordinal));
        Assert.False(typeof(QualityPresentationSnapshot)
            .GetProperty(nameof(QualityPresentationSnapshot.PendingReconnect))!
            .CanWrite);
        Assert.True(snapshot.PendingReconnect);
        Assert.Same(actual, snapshot.Actual);
    }

    [Fact]
    public void Desired_color16_and_observed_rgb565_are_presented_as_distinct_lines()
    {
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Color16,
            QualityScale.Percent100,
            FrameRefreshPolicy.Fixed(30));
        var actual = new QualityActualState(
            RemotePixelFormatKind.Rgb565,
            QualityActualEncoding.Zrle,
            FallbackUsed: false);

        Assert.Contains("期望：16 位", QualityPresentation.DesiredText(profile, decision: null));
        Assert.Contains("不限制", QualityPresentation.DesiredText(profile, decision: null));
        Assert.StartsWith("实际：16 位 · ZRLE", QualityPresentation.AppliedText(actual, Performance()));
    }

    [Fact]
    public void Desired_color16_and_observed_bgra32_fallback_disclose_safe_fallback()
    {
        var actual = new QualityActualState(
            RemotePixelFormatKind.Bgra32,
            QualityActualEncoding.Zlib,
            FallbackUsed: true);

        var text = QualityPresentation.AppliedText(actual, Performance());

        Assert.Contains("实际：32 位（已安全回退）", text);
        Assert.Contains("Zlib", text);
    }

    [Fact]
    public void Fallback_status_and_automation_name_include_desired_actual_and_fallback()
    {
        const string desired = "期望：16 位 · 100% · 30 FPS · 不限制";
        const string applied = "实际：32 位（已安全回退） · Zlib · 2 FPS · 4.1 MiB/s";

        Assert.Equal(
            QualityPresentationStatus.SafeFallback,
            QualityPresentation.StatusFor(
                QualityDecisionReason.Initial,
                targetSatisfied: true,
                QualityTransitionStatus.NoChange,
                fallbackUsed: true));
        Assert.Equal(
            $"画质：{desired}；{applied}；状态：本连接已安全回退",
            QualityPresentation.AutomationName(
                desired,
                applied,
                QualityPresentationStatus.SafeFallback));
    }

    [Fact]
    public void Only_user_pending_reconnect_takes_priority_over_existing_fallback_status()
    {
        var fallback = new QualityActualState(
            RemotePixelFormatKind.Bgra32,
            QualityActualEncoding.Zlib,
            FallbackUsed: true);
        var snapshot = new QualityPresentationSnapshot(
            QualityProfile.Balanced,
            decision: null,
            QualityTransitionStatus.ReconnectRequired,
            Performance(),
            epoch: 1,
            version: 1,
            fallback);

        Assert.Equal(
            QualityPresentationStatus.SafeFallback,
            QualityPresentation.StatusFor(snapshot));
        Assert.Equal(
            QualityPresentationStatus.ReconnectRequired,
            QualityPresentation.StatusFor(new QualityPresentationSnapshot(
                snapshot.Profile,
                snapshot.Decision,
                snapshot.TransitionStatus,
                snapshot.Performance,
                snapshot.Epoch,
                snapshot.Version,
                snapshot.Actual,
                pendingReconnect: true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Status_overloads_without_pending_information_keep_fallback_above_transition_reconnect(
        bool useBandwidthAwareOverload)
    {
        var status = useBandwidthAwareOverload
            ? QualityPresentation.StatusFor(
                QualityDecisionReason.Initial,
                targetSatisfied: true,
                QualityTransitionStatus.ReconnectRequired,
                fallbackUsed: true,
                hasNumericBandwidthTarget: true)
            : QualityPresentation.StatusFor(
                QualityDecisionReason.Initial,
                targetSatisfied: true,
                QualityTransitionStatus.ReconnectRequired,
                fallbackUsed: true);

        Assert.Equal(QualityPresentationStatus.SafeFallback, status);
    }

    [Fact]
    public void Unlimited_bandwidth_is_explicit_and_never_claims_a_low_bandwidth_target()
    {
        var text = QualityPresentation.DesiredText(QualityProfile.Original, decision: null);
        var decision = new QualityDecision(
            generation: 1,
            QualityContentState.Idle,
            QualityLevel.Q0,
            QualityColor.Full32,
            QualityScale.Percent100,
            targetFramesPerSecond: 60,
            QualityDecisionReason.Initial,
            targetSatisfied: true,
            levelChanged: false,
            contentStateChanged: false,
            QualityLevel.Q0,
            QualityContentState.Idle);
        var snapshot = new QualityPresentationSnapshot(
            QualityProfile.Original,
            decision,
            QualityTransitionStatus.NoChange,
            Performance(),
            Epoch: 1,
            Version: 1);
        var status = QualityPresentation.StatusFor(snapshot);

        Assert.Contains("不限制", text);
        Assert.Equal(QualityPresentationStatus.Unrestricted, status);
        Assert.Contains("不限制", QualityPresentation.StatusText(status), StringComparison.Ordinal);
        Assert.DoesNotContain("已达到", QualityPresentation.StatusText(status), StringComparison.Ordinal);
        Assert.DoesNotContain("已达到低带宽目标", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_for_preserves_the_original_three_parameter_api()
    {
        var method = typeof(QualityPresentation).GetMethod(
            nameof(QualityPresentation.StatusFor),
            [
                typeof(QualityDecisionReason),
                typeof(bool),
                typeof(QualityTransitionStatus),
            ]);

        Assert.NotNull(method);
        Assert.Equal(
            QualityPresentationStatus.TargetReached,
            QualityPresentation.StatusFor(
                QualityDecisionReason.Initial,
                targetSatisfied: true,
                QualityTransitionStatus.NoChange));
    }

    [Fact]
    public void Automatic_summary_contains_active_dimensions_and_evidence()
    {
        var text = QualityPresentation.FormatSummary(
            QualityPreset.Automatic,
            QualityColor.Color16,
            QualityScale.Percent75,
            45,
            3.1 * MiB);

        Assert.Equal("自动 · 16 位 · 75% · 45 FPS · 3.1 MiB/s", text);
    }

    [Theory]
    [InlineData(QualityPresentationStatus.TargetReached, "已达到目标")]
    [InlineData(QualityPresentationStatus.ReducingForMotion, "正在为运动画面降载")]
    [InlineData(QualityPresentationStatus.RecoveringClarity, "正在恢复清晰度")]
    [InlineData(QualityPresentationStatus.TargetUnsatisfied, "当前链路无法满足目标")]
    [InlineData(QualityPresentationStatus.CapabilityLimited, "服务器能力不足，自动范围已限制")]
    [InlineData(QualityPresentationStatus.ReconnectRequired, "下次连接生效")]
    [InlineData(QualityPresentationStatus.Unknown, "状态未知")]
    public void Status_has_complete_visible_text(QualityPresentationStatus status, string expected) =>
        Assert.Equal(expected, QualityPresentation.StatusText(status));

    [Fact]
    public void Automation_name_contains_summary_and_status_so_color_is_not_the_only_signal()
    {
        var summary = "自动 · 16 位 · 75% · 45 FPS · 3.1 MiB/s";

        Assert.Equal(
            $"画质：{summary}；状态：正在恢复清晰度",
            QualityPresentation.AutomationName(summary, QualityPresentationStatus.RecoveringClarity));
    }

    [Theory]
    [InlineData(0, "Raw")]
    [InlineData(6, "Zlib")]
    [InlineData(16, "ZRLE")]
    [InlineData(1001, "Apple 灰度")]
    [InlineData(1002, "Apple 彩色")]
    [InlineData(-314, "Unknown")]
    public void Encoding_uses_stable_name_and_never_exposes_raw_unknown_id(int encoding, string expected) =>
        Assert.Equal(expected, QualityPresentation.EncodingName(encoding));

    [Fact]
    public void Detailed_change_switches_to_custom_and_preserves_other_dimensions()
    {
        var changed = QualityPresentation.WithColor(QualityProfile.Balanced, QualityColor.Full32);

        Assert.Equal(QualityPreset.Custom, changed.Preset);
        Assert.Equal(QualityColor.Full32, changed.Color);
        Assert.Equal(QualityScale.Percent75, changed.Scale);
        Assert.Equal((long)(4 * MiB), changed.TargetBytesPerSecond);
    }

    [Fact]
    public void Preset_mapping_returns_the_domain_profiles()
    {
        Assert.Same(QualityProfile.Automatic, QualityPresentation.ApplyPreset(QualityPreset.Automatic));
        Assert.Same(QualityProfile.Original, QualityPresentation.ApplyPreset(QualityPreset.Original));
        Assert.Same(QualityProfile.Balanced, QualityPresentation.ApplyPreset(QualityPreset.Balanced));
        Assert.Same(QualityProfile.Smooth, QualityPresentation.ApplyPreset(QualityPreset.Smooth));
    }

    [Fact]
    public void Refresh_options_keep_saved_value_disabled_when_reliable_limit_is_lower()
    {
        var selected = FrameRefreshPolicy.Fixed(90);
        var options = QualityPresentation.RefreshOptions(selected, 60);

        var saved = Assert.Single(options, option => option.Value == selected);
        Assert.False(saved.IsEnabled);
        Assert.Contains("当前上限 60", saved.ConstraintText);
        Assert.Equal(9, QualityPresentation.RefreshOptions(selected, null).Count);
    }

    [Fact]
    public void All_required_choices_are_exposed()
    {
        Assert.Equal(5, QualityPresentation.PresetOptions.Count);
        Assert.Equal(new long?[] { 1L << 20, 2L << 20, 4L << 20, 8L << 20, 16L << 20, null },
            QualityPresentation.BandwidthOptions.Take(6).Select(option => option.Value));
        Assert.Equal(4, QualityPresentation.ColorOptions.Count);
        Assert.Equal(5, QualityPresentation.ScaleOptions.Count);
        Assert.Equal(
            QualityScales.CanonicalValues,
            QualityPresentation.ScaleOptions.Select(option => option.Value));
    }

    [Fact]
    public void Capability_limited_choices_keep_saved_scale_and_grayscale_disabled_and_annotated()
    {
        var capabilities = new ArdDisplayCapabilities(
            CapabilitySupport.Observed,
            CapabilitySupport.Observed,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            true,
            false,
            null);

        var colors = QualityPresentation.ColorOptionsFor(
            QualityColor.Grayscale,
            capabilities,
            new QualityDecoderGates());
        var scales = QualityPresentation.ScaleOptionsFor(QualityScale.Percent50, capabilities);

        var savedGray = Assert.Single(colors, option => option.Value == QualityColor.Grayscale);
        Assert.False(savedGray.IsEnabled);
        Assert.Equal("已保存，当前连接不可用", savedGray.ConstraintText);
        Assert.All(
            scales.Where(option => option.Value is QualityScale.Percent75 or QualityScale.Percent50 or QualityScale.Percent25),
            option => Assert.False(option.IsEnabled));
        var savedScale = Assert.Single(scales, option => option.Value == QualityScale.Percent50);
        Assert.Equal("已保存，当前连接不可用", savedScale.ConstraintText);
        Assert.Equal(4, colors.Count);
        Assert.Equal(5, scales.Count);
    }

    [Fact]
    public void Observed_capabilities_and_approved_gate_enable_remote_quality_choices()
    {
        var capabilities = new ArdDisplayCapabilities(
            CapabilitySupport.Observed,
            CapabilitySupport.Observed,
            CapabilitySupport.Observed,
            CapabilitySupport.Observed,
            CapabilitySupport.Observed,
            true,
            true,
            null);

        Assert.True(QualityPresentation.ScaleOptionsFor(
                QualityScale.Percent100,
                capabilities,
                scaleSwitchApproved: true)
            .Single(option => option.Value == QualityScale.Percent75).IsEnabled);
        Assert.True(QualityPresentation.ColorOptionsFor(
                QualityColor.Full32,
                capabilities,
                new QualityDecoderGates(AppleColor1002Approved: false, AppleGrayscale1001Approved: true))
            .Single(option => option.Value == QualityColor.Grayscale).IsEnabled);
    }

    [Fact]
    public void Observed_scaling_stays_disabled_until_the_connection_safety_gate_is_approved()
    {
        var capabilities = new ArdDisplayCapabilities(
            CapabilitySupport.Observed,
            CapabilitySupport.Observed,
            CapabilitySupport.Observed,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            true,
            true,
            null);

        var scale = QualityPresentation.ScaleOptionsFor(QualityScale.Percent100, capabilities)
            .Single(option => option.Value == QualityScale.Percent75);

        Assert.False(scale.IsEnabled);
        Assert.Equal("当前连接不可用", scale.ConstraintText);
    }

    [Fact]
    public void Summary_before_first_decision_labels_saved_values_as_settings_awaiting_confirmation()
    {
        var profile = QualityProfile.CreateCustom(
            2L << 20,
            QualityColor.Grayscale,
            QualityScale.Percent50,
            FrameRefreshPolicy.Automatic);

        var summary = QualityPresentation.FormatPendingSummary(profile, 60, null);

        Assert.Equal("设置：自定义 · 灰度 · 50% · 60 FPS · —（待能力确认）", summary);
        Assert.DoesNotContain("当前：灰度", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("实际：灰度", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Bandwidth_choices_have_stable_unique_ids_and_explicit_kinds()
    {
        Assert.Equal(
            ["preset-1", "preset-2", "preset-4", "preset-8", "preset-16", "unlimited", "custom"],
            QualityPresentation.BandwidthOptions.Select(option => option.Id));
        Assert.Equal(QualityBandwidthOptionKind.Unlimited, QualityPresentation.BandwidthOptions[5].Kind);
        Assert.Equal(QualityBandwidthOptionKind.Custom, QualityPresentation.BandwidthOptions[6].Kind);
        Assert.Null(QualityPresentation.BandwidthOptions[5].Value);
        Assert.Null(QualityPresentation.BandwidthOptions[6].Value);
    }

    [Fact]
    public void Runtime_reason_and_transition_map_to_visible_status()
    {
        Assert.Equal(
            QualityPresentationStatus.ReconnectRequired,
            QualityPresentation.StatusFor(QualityDecisionReason.Initial, true, QualityTransitionStatus.ReconnectRequired));
        Assert.Equal(
            QualityPresentationStatus.TargetUnsatisfied,
            QualityPresentation.StatusFor(QualityDecisionReason.TargetUnsatisfied, false, QualityTransitionStatus.NoChange));
        Assert.Equal(
            QualityPresentationStatus.RecoveringClarity,
            QualityPresentation.StatusFor(QualityDecisionReason.StableRecovery, true, QualityTransitionStatus.Applied));
    }

    [Fact]
    public void Custom_bandwidth_accepts_non_preset_value_and_unlimited()
    {
        var custom = QualityPresentation.WithBandwidth(QualityProfile.Automatic, 3 * (long)MiB);
        var unlimited = QualityPresentation.WithBandwidth(custom, null);

        Assert.Equal(3 * (long)MiB, custom.TargetBytesPerSecond);
        Assert.Null(unlimited.TargetBytesPerSecond);
    }

    [Fact]
    public void Locks_are_independent_and_grayscale_toggle_is_preserved_when_valid()
    {
        var profile = QualityPresentation.WithAutomaticGrayscale(QualityProfile.Smooth, false);
        profile = QualityPresentation.WithLocks(profile, true, false, true, false);

        Assert.True(profile.BandwidthLocked);
        Assert.False(profile.ColorLocked);
        Assert.True(profile.ScaleLocked);
        Assert.False(profile.RefreshLocked);
        Assert.False(profile.AllowAutomaticGrayscale);
        Assert.Equal(QualityPreset.Custom, profile.Preset);
    }

    [Fact]
    public void Every_detail_edit_returns_custom_profile()
    {
        Assert.Equal(QualityPreset.Custom,
            QualityPresentation.WithBandwidth(QualityProfile.Automatic, 8L << 20).Preset);
        Assert.Equal(QualityPreset.Custom,
            QualityPresentation.WithColor(QualityProfile.Automatic, QualityColor.Color16).Preset);
        Assert.Equal(QualityPreset.Custom,
            QualityPresentation.WithScale(QualityProfile.Automatic, QualityScale.Percent75).Preset);
        Assert.Equal(QualityPreset.Custom,
            QualityPresentation.WithRefresh(QualityProfile.Automatic, FrameRefreshPolicy.Fixed(45)).Preset);
        Assert.Equal(QualityPreset.Custom,
            QualityPresentation.WithAutomaticGrayscale(QualityProfile.Automatic, false).Preset);
    }

    [Fact]
    public void Selecting_custom_copies_current_details_into_applicable_profile()
    {
        var custom = QualityPresentation.AsCustom(QualityProfile.Balanced);

        Assert.Equal(QualityPreset.Custom, custom.Preset);
        Assert.Equal(QualityProfile.Balanced.TargetBytesPerSecond, custom.TargetBytesPerSecond);
        Assert.Equal(QualityProfile.Balanced.Color, custom.Color);
        Assert.Equal(QualityProfile.Balanced.Scale, custom.Scale);
    }

    [Fact]
    public void Performance_text_replaces_unknown_numeric_encoding_with_stable_name()
    {
        var text = QualityPresentation.SanitizePerformanceText(
            "自动 45 FPS · 实际 44 FPS · 3.1 MiB/s · 编码 -314 · 20 ms");

        Assert.Equal("自动 45 FPS · 实际 44 FPS · 3.1 MiB/s · Unknown · 20 ms", text);
        Assert.DoesNotContain("-314", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_quality_automation_name_never_reads_unknown_raw_encoding_id()
    {
        var name = QualityPresentation.ConnectionQualityAutomationName(
            "良好",
            "自动 45 FPS · 实际 44 FPS · 3.1 MiB/s · 编码 -314 · 20 ms");

        Assert.Equal(
            "连接质量：良好；会话性能：自动 45 FPS · 实际 44 FPS · 3.1 MiB/s · Unknown · 20 ms",
            name);
        Assert.DoesNotContain("-314", name, StringComparison.Ordinal);
    }

    private static SessionPerformanceSnapshot Performance() => new(
        FrameRefreshMode.Fixed,
        TargetFramesPerSecond: 30,
        ActualFramesPerSecond: 2,
        ReceiveBytesPerSecond: 2L << 20,
        PrimaryFramebufferEncoding: 6,
        ResponseMilliseconds: 20,
        InputWriteMilliseconds: 0,
        InputQueueDepth: 0,
        CoalescedPointerMoves: 0,
        SampleSequence: 1);
}
