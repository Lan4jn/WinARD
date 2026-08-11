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
    [InlineData(QualityPresentationStatus.ReconnectRequired, "重新连接后生效")]
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
        Assert.Equal(4, QualityPresentation.ScaleOptions.Count);
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
}
