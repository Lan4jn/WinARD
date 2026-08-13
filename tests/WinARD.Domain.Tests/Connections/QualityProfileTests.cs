using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Domain.Tests.Connections;

public sealed class QualityProfileTests
{
    [Fact]
    public void Quality_scale_values_preserve_native_as_the_percent100_compatibility_alias()
    {
        Assert.Equal(0, (int)QualityScale.Automatic);
        Assert.Equal(1, (int)QualityScale.Percent100);
        Assert.Equal(
            QualityScale.Percent100,
            (QualityScale)(typeof(QualityScale).GetField("Native")!.GetRawConstantValue() ?? -1));
        Assert.Equal(2, (int)QualityScale.Percent75);
        Assert.Equal(3, (int)QualityScale.Percent50);
        Assert.Equal(4, (int)QualityScale.Percent25);
    }

    [Fact]
    public void Quality_scales_expose_five_canonical_values_and_stable_names()
    {
        Assert.Equal(
            [
                QualityScale.Automatic,
                QualityScale.Percent100,
                QualityScale.Percent75,
                QualityScale.Percent50,
                QualityScale.Percent25,
            ],
            QualityScales.CanonicalValues);
        Assert.Equal(
            ["Automatic", "Percent100", "Percent75", "Percent50", "Percent25"],
            QualityScales.CanonicalValues.Select(QualityScales.GetStableName));
    }

    [Fact]
    public void Native_alias_is_obsolete_for_source_compatibility()
    {
        var field = typeof(QualityScale).GetField("Native");

        Assert.NotNull(field);
        Assert.NotNull(field!.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false).SingleOrDefault());
    }

    [Fact]
    public void Automatic_profile_has_expected_defaults()
    {
        var profile = QualityProfile.Automatic;

        Assert.Equal(QualityPreset.Automatic, profile.Preset);
        Assert.Equal(2L * 1024 * 1024, profile.TargetBytesPerSecond);
        Assert.Equal(QualityColor.Automatic, profile.Color);
        Assert.Equal(QualityScale.Automatic, profile.Scale);
        Assert.Equal(FrameRefreshPolicy.Automatic, profile.Refresh);
        Assert.True(profile.AllowAutomaticGrayscale);
        Assert.False(profile.BandwidthLocked);
        Assert.False(profile.ColorLocked);
        Assert.False(profile.ScaleLocked);
        Assert.False(profile.RefreshLocked);
    }

    [Fact]
    public void Quality_color_has_no_black_and_white_value() =>
        Assert.DoesNotContain("BlackAndWhite", Enum.GetNames<QualityColor>());

    [Fact]
    public void Named_presets_express_expected_intent()
    {
        Assert.Equal(
            (QualityPreset.Original, (long?)null, QualityColor.Full32, QualityScale.Percent100, FrameRefreshPolicy.Unlimited, false),
            Intent(QualityProfile.Original));
        Assert.Equal(
            (QualityPreset.Balanced, (long?)(4L * 1024 * 1024), QualityColor.Color16, QualityScale.Percent75, FrameRefreshPolicy.Automatic, true),
            Intent(QualityProfile.Balanced));
        Assert.Equal(
            (QualityPreset.Smooth, (long?)(2L * 1024 * 1024), QualityColor.Color16, QualityScale.Percent50, FrameRefreshPolicy.Automatic, true),
            Intent(QualityProfile.Smooth));
    }

    [Fact]
    public void Original_locks_every_dimension_while_adaptive_presets_leave_them_unlocked()
    {
        Assert.Equal((true, true, true, true), Locks(QualityProfile.Original));
        Assert.Equal((false, false, false, false), Locks(QualityProfile.Automatic));
        Assert.Equal((false, false, false, false), Locks(QualityProfile.Balanced));
        Assert.Equal((false, false, false, false), Locks(QualityProfile.Smooth));
    }

    [Fact]
    public void Changing_refresh_on_any_named_preset_creates_custom_intent_and_preserves_other_details()
    {
        var cases = new[]
        {
            (QualityProfile.Automatic, FrameRefreshPolicy.Fixed(30)),
            (QualityProfile.Original, FrameRefreshPolicy.Fixed(60)),
            (QualityProfile.Balanced, FrameRefreshPolicy.Unlimited),
            (QualityProfile.Smooth, FrameRefreshPolicy.Fixed(90)),
        };

        foreach (var (named, refresh) in cases)
        {
            var updated = named.WithRefresh(refresh);

            Assert.Equal(QualityPreset.Custom, updated.Preset);
            Assert.Equal(named.TargetBytesPerSecond, updated.TargetBytesPerSecond);
            Assert.Equal(named.Color, updated.Color);
            Assert.Equal(named.Scale, updated.Scale);
            Assert.Equal(refresh, updated.Refresh);
            Assert.Equal(named.AllowAutomaticGrayscale, updated.AllowAutomaticGrayscale);
            Assert.Equal(Locks(named), Locks(updated));
        }
    }

    [Fact]
    public void Reapplying_named_preset_refresh_keeps_the_named_preset()
    {
        Assert.Same(QualityProfile.Automatic, QualityProfile.Automatic.WithRefresh(FrameRefreshPolicy.Automatic));
        Assert.Same(QualityProfile.Original, QualityProfile.Original.WithRefresh(FrameRefreshPolicy.Unlimited));
        Assert.Same(QualityProfile.Balanced, QualityProfile.Balanced.WithRefresh(FrameRefreshPolicy.Automatic));
        Assert.Same(QualityProfile.Smooth, QualityProfile.Smooth.WithRefresh(FrameRefreshPolicy.Automatic));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1099511627777)]
    public void Custom_rejects_bandwidth_outside_supported_range(long targetBytesPerSecond) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateCustom(targetBytesPerSecond));

    [Fact]
    public void Custom_accepts_unlimited_bandwidth_and_all_four_locks()
    {
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Full32,
            QualityScale.Percent100,
            FrameRefreshPolicy.Fixed(60),
            allowAutomaticGrayscale: false,
            bandwidthLocked: true,
            colorLocked: true,
            scaleLocked: true,
            refreshLocked: true);

        Assert.Null(profile.TargetBytesPerSecond);
        Assert.True(profile.BandwidthLocked);
        Assert.True(profile.ColorLocked);
        Assert.True(profile.ScaleLocked);
        Assert.True(profile.RefreshLocked);
    }

    [Theory]
    [InlineData(99, 0)]
    [InlineData(0, 99)]
    public void Custom_rejects_unknown_enum_values(int color, int scale) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityProfile.CreateCustom(
            1024,
            (QualityColor)color,
            (QualityScale)scale,
            FrameRefreshPolicy.Automatic));

    [Fact]
    public void Custom_rejects_automatic_grayscale_when_color_is_locked_to_full_color() =>
        Assert.Throws<ArgumentException>(() => QualityProfile.CreateCustom(
            1024,
            QualityColor.Full32,
            QualityScale.Percent100,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: true,
            colorLocked: true));

    private static QualityProfile CreateCustom(long targetBytesPerSecond) =>
        QualityProfile.CreateCustom(
            targetBytesPerSecond,
            QualityColor.Automatic,
            QualityScale.Automatic,
            FrameRefreshPolicy.Automatic);

    private static (QualityPreset, long?, QualityColor, QualityScale, FrameRefreshPolicy, bool) Intent(QualityProfile profile) =>
        (profile.Preset, profile.TargetBytesPerSecond, profile.Color, profile.Scale, profile.Refresh, profile.AllowAutomaticGrayscale);

    private static (bool, bool, bool, bool) Locks(QualityProfile profile) =>
        (profile.BandwidthLocked, profile.ColorLocked, profile.ScaleLocked, profile.RefreshLocked);
}
