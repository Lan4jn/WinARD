using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Application.Tests.Quality;

public sealed class QualityBootstrapPlannerTests
{
    private const long MiB = 1024L * 1024;
    private static readonly Type[] ThreeParameterSettingsConstructor =
    [
        typeof(RemotePixelFormatKind),
        typeof(IReadOnlyList<int>),
        typeof(QualityBootstrapReason),
    ];
    private static readonly int[] SingleZlibEncoding = [6];
    private static readonly int[] PreferredEncodings = [16, 6, 0, 1, -239, -223];
    private static readonly int[] FallbackEncodings = [6, 16, 0, 1, -239, -223];

    [Fact]
    public void Attempts_are_a_closed_preferred_then_fallback_pair()
    {
        Assert.Equal(
            [QualityBootstrapAttempt.Preferred, QualityBootstrapAttempt.Fallback],
            Enum.GetValues<QualityBootstrapAttempt>());
    }

    [Theory]
    [MemberData(nameof(Full32Profiles))]
    public void Original_or_locked_full32_starts_in_bgra32(QualityProfile profile)
    {
        var plan = CreatePlan(profile);

        AssertSettings(
            plan.Preferred,
            RemotePixelFormatKind.Bgra32,
            QualityBootstrapReason.UserFull32,
            PreferredEncodings);
    }

    [Fact]
    public void Unlocked_full32_uses_the_bandwidth_startup_format()
    {
        var profile = QualityProfile.CreateCustom(
            targetBytesPerSecond: 2L * 1024 * 1024,
            QualityColor.Full32,
            QualityScale.Automatic,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: true,
            colorLocked: false);

        var plan = CreatePlan(profile);

        AssertSettings(
            plan.Preferred,
            RemotePixelFormatKind.Rgb565,
            QualityBootstrapReason.AutomaticBandwidth,
            PreferredEncodings);
    }

    [Theory]
    [MemberData(nameof(BandwidthManagedProfiles))]
    public void Bandwidth_managed_profiles_start_in_rgb565(QualityProfile profile)
    {
        var plan = CreatePlan(profile);

        AssertSettings(
            plan.Preferred,
            RemotePixelFormatKind.Rgb565,
            QualityBootstrapReason.AutomaticBandwidth,
            PreferredEncodings);
    }

    [Fact]
    public void Explicit_color16_starts_in_rgb565_for_the_user_request()
    {
        var profile = QualityProfile.CreateCustom(
            targetBytesPerSecond: null,
            QualityColor.Color16,
            QualityScale.Native,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: false,
            colorLocked: true);

        var plan = CreatePlan(profile);

        AssertSettings(
            plan.Preferred,
            RemotePixelFormatKind.Rgb565,
            QualityBootstrapReason.UserColor16,
            PreferredEncodings);
    }

    [Theory]
    [InlineData(CapabilitySupport.Unknown, false)]
    [InlineData(CapabilitySupport.Advertised, true)]
    [InlineData(CapabilitySupport.Observed, false)]
    public void Unavailable_grayscale_is_safely_limited_to_rgb565(
        CapabilitySupport grayscaleSupport,
        bool decoderApproved)
    {
        var profile = QualityProfile.CreateCustom(
            targetBytesPerSecond: 1024,
            QualityColor.Grayscale,
            QualityScale.Percent50,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: true,
            colorLocked: false);
        var capabilities = CreateCapabilities(grayscaleSupport);

        var plan = QualityBootstrapPlanner.CreatePlan(
            profile,
            capabilities,
            new QualityDecoderGates(AppleGrayscale1001Approved: decoderApproved));

        AssertSettings(
            plan.Preferred,
            RemotePixelFormatKind.Rgb565,
            QualityBootstrapReason.CapabilityLimited,
            PreferredEncodings);
    }

    [Fact]
    public void Approved_observed_grayscale_uses_the_zero_install_bandwidth_format()
    {
        var profile = QualityProfile.CreateCustom(
            targetBytesPerSecond: 1024,
            QualityColor.Grayscale,
            QualityScale.Percent50,
            FrameRefreshPolicy.Automatic,
            allowAutomaticGrayscale: true,
            colorLocked: false);

        var plan = QualityBootstrapPlanner.CreatePlan(
            profile,
            CreateCapabilities(CapabilitySupport.Observed),
            new QualityDecoderGates(AppleGrayscale1001Approved: true));

        AssertSettings(
            plan.Preferred,
            RemotePixelFormatKind.Rgb565,
            QualityBootstrapReason.AutomaticBandwidth,
            PreferredEncodings);
    }

    [Fact]
    public void Every_plan_has_a_safe_bgra32_fallback_with_zlib_first()
    {
        var plan = CreatePlan(QualityProfile.Smooth);

        AssertSettings(
            plan.Fallback,
            RemotePixelFormatKind.Bgra32,
            QualityBootstrapReason.SafeFallback,
            FallbackEncodings);
        Assert.Equal(1d, plan.Fallback.ScaleFactor);
    }

    [Theory]
    [InlineData(1 * MiB, 0.25)]
    [InlineData(1 * MiB + 1, 0.5)]
    [InlineData(2 * MiB, 0.5)]
    [InlineData(2 * MiB + 1, 0.75)]
    [InlineData(4 * MiB, 0.75)]
    [InlineData(4 * MiB + 1, 1.0)]
    public void Automatic_scale_maps_bandwidth_boundaries(long targetBytesPerSecond, double expected)
    {
        var profile = QualityProfile.CreateCustom(
            targetBytesPerSecond,
            QualityColor.Automatic,
            QualityScale.Automatic,
            FrameRefreshPolicy.Automatic);

        Assert.Equal(expected, CreatePlan(profile).Preferred.ScaleFactor);
    }

    [Fact]
    public void Automatic_scale_maps_unlimited_bandwidth_to_percent100()
    {
        var profile = QualityProfile.CreateCustom(
            null,
            QualityColor.Automatic,
            QualityScale.Automatic,
            FrameRefreshPolicy.Automatic);

        Assert.Equal(1d, CreatePlan(profile).Preferred.ScaleFactor);
    }

    [Theory]
    [InlineData(QualityScale.Percent100, 1.0)]
    [InlineData(QualityScale.Percent75, 0.75)]
    [InlineData(QualityScale.Percent50, 0.5)]
    [InlineData(QualityScale.Percent25, 0.25)]
    public void Explicit_scale_is_not_overridden_by_bandwidth(QualityScale scale, double expected)
    {
        var profile = QualityProfile.CreateCustom(
            1,
            QualityColor.Automatic,
            scale,
            FrameRefreshPolicy.Automatic);

        Assert.Equal(expected, CreatePlan(profile).Preferred.ScaleFactor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Settings_reject_invalid_scale_factors(double scaleFactor)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new QualityBootstrapSettings(
            RemotePixelFormatKind.Bgra32,
            [6],
            QualityBootstrapReason.UserFull32,
            scaleFactor));

        Assert.Equal("scaleFactor", exception.ParamName);
    }

    [Fact]
    public void Settings_preserve_the_exact_public_three_parameter_constructor()
    {
        var constructor = typeof(QualityBootstrapSettings).GetConstructor(
            ThreeParameterSettingsConstructor);

        Assert.NotNull(constructor);
        var settings = Assert.IsType<QualityBootstrapSettings>(constructor!.Invoke(
            [RemotePixelFormatKind.Bgra32, SingleZlibEncoding, QualityBootstrapReason.UserFull32]));
        Assert.Equal(1d, settings.ScaleFactor);
    }

    [Fact]
    public void Independently_created_plans_have_deterministic_value_equality()
    {
        var first = CreatePlan(QualityProfile.Automatic);
        var second = CreatePlan(QualityProfile.Automatic);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Encoding_order_participates_in_settings_value_equality()
    {
        var first = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6],
            QualityBootstrapReason.AutomaticBandwidth);
        var second = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [6, 16],
            QualityBootstrapReason.AutomaticBandwidth);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Settings_copy_the_encoding_snapshot()
    {
        int[] encodings = [16, 6];
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            encodings,
            QualityBootstrapReason.UserColor16);

        encodings[0] = 0;

        Assert.Equal([16, 6], settings.Encodings);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<int>)settings.Encodings)[0] = 0);
    }

    [Fact]
    public void Settings_reject_null_or_empty_encodings()
    {
        var nullException = Assert.Throws<ArgumentNullException>(() => new QualityBootstrapSettings(
            RemotePixelFormatKind.Bgra32,
            null!,
            QualityBootstrapReason.UserFull32));
        var emptyException = Assert.Throws<ArgumentException>(() => new QualityBootstrapSettings(
            RemotePixelFormatKind.Bgra32,
            [],
            QualityBootstrapReason.UserFull32));

        Assert.Equal("encodings", nullException.ParamName);
        Assert.Equal("encodings", emptyException.ParamName);
    }

    [Fact]
    public void Settings_reject_duplicate_encodings()
    {
        var exception = Assert.Throws<ArgumentException>(() => new QualityBootstrapSettings(
            RemotePixelFormatKind.Bgra32,
            [6, 6],
            QualityBootstrapReason.UserFull32));

        Assert.Equal("encodings", exception.ParamName);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1001)]
    [InlineData(int.MinValue)]
    public void Settings_reject_encodings_outside_the_closed_allowlist(int encoding)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new QualityBootstrapSettings(
            RemotePixelFormatKind.Bgra32,
            [encoding],
            QualityBootstrapReason.UserFull32));

        Assert.Equal("encodings", exception.ParamName);
    }

    [Fact]
    public void Settings_reject_undefined_enum_values_with_precise_parameter_names()
    {
        var pixelFormatException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QualityBootstrapSettings(
                (RemotePixelFormatKind)int.MaxValue,
                [6],
                QualityBootstrapReason.UserFull32));
        var reasonException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QualityBootstrapSettings(
                RemotePixelFormatKind.Bgra32,
                [6],
                (QualityBootstrapReason)int.MaxValue));

        Assert.Equal("pixelFormat", pixelFormatException.ParamName);
        Assert.Equal("reason", reasonException.ParamName);
    }

    [Fact]
    public void Plan_rejects_null_attempt_settings_with_precise_parameter_names()
    {
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Bgra32,
            [6],
            QualityBootstrapReason.SafeFallback);

        var preferredException = Assert.Throws<ArgumentNullException>(() =>
            new QualityBootstrapPlan(null!, settings));
        var fallbackException = Assert.Throws<ArgumentNullException>(() =>
            new QualityBootstrapPlan(settings, null!));

        Assert.Equal("preferred", preferredException.ParamName);
        Assert.Equal("fallback", fallbackException.ParamName);
    }

    [Fact]
    public void BootstrapStateRetainsTheExactPublicTwoParameterConstructor()
    {
        var constructor = typeof(QualityBootstrapState).GetConstructor(
            [typeof(QualityBootstrapAttempt), typeof(QualityBootstrapSettings)]);

        Assert.NotNull(constructor);
        Assert.Equal(2, constructor!.GetParameters().Length);
    }

    [Fact]
    public void Planner_rejects_null_inputs()
    {
        Assert.Throws<ArgumentNullException>(() => QualityBootstrapPlanner.CreatePlan(
            null!,
            ArdDisplayCapabilities.Unknown,
            new QualityDecoderGates()));
        Assert.Throws<ArgumentNullException>(() => QualityBootstrapPlanner.CreatePlan(
            QualityProfile.Automatic,
            null!,
            new QualityDecoderGates()));
        Assert.Throws<ArgumentNullException>(() => QualityBootstrapPlanner.CreatePlan(
            QualityProfile.Automatic,
            ArdDisplayCapabilities.Unknown,
            null!));
    }

    public static TheoryData<QualityProfile> Full32Profiles => new()
    {
        QualityProfile.Original,
        QualityProfile.CreateCustom(
            targetBytesPerSecond: null,
            QualityColor.Full32,
            QualityScale.Native,
            FrameRefreshPolicy.Unlimited,
            allowAutomaticGrayscale: false,
            colorLocked: true),
    };

    public static TheoryData<QualityProfile> BandwidthManagedProfiles => new()
    {
        QualityProfile.Automatic,
        QualityProfile.Balanced,
        QualityProfile.Smooth,
    };

    private static QualityBootstrapPlan CreatePlan(QualityProfile profile) =>
        QualityBootstrapPlanner.CreatePlan(
            profile,
            CreateCapabilities(CapabilitySupport.Observed),
            new QualityDecoderGates(AppleGrayscale1001Approved: true));

    private static ArdDisplayCapabilities CreateCapabilities(CapabilitySupport grayscaleSupport) =>
        new(
            CapabilitySupport.Observed,
            CapabilitySupport.Observed,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            grayscaleSupport,
            SafeOnlinePixelFormatSwitch: false,
            SafeOnlineScaleSwitch: false,
            MaximumRefreshRate: null);

    private static void AssertSettings(
        QualityBootstrapSettings settings,
        RemotePixelFormatKind pixelFormat,
        QualityBootstrapReason reason,
        IReadOnlyList<int> encodings)
    {
        Assert.Equal(pixelFormat, settings.PixelFormat);
        Assert.Equal(reason, settings.Reason);
        Assert.Equal(encodings, settings.Encodings);
    }
}
