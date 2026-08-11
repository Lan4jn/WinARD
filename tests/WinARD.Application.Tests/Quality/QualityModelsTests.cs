using WinARD.Application.Quality;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Application.Tests.Quality;

public sealed class QualityModelsTests
{
    [Theory]
    [InlineData(CapabilitySupport.Unknown, false, false)]
    [InlineData(CapabilitySupport.Unsupported, false, false)]
    [InlineData(CapabilitySupport.Advertised, true, false)]
    [InlineData(CapabilitySupport.Observed, true, true)]
    public void Capability_queries_preserve_evidence_strength(
        CapabilitySupport support,
        bool isSupported,
        bool isObserved)
    {
        Assert.Equal(isSupported, support.IsSupported());
        Assert.Equal(isObserved, support.IsObserved());
    }

    [Fact]
    public void Capabilities_preserve_each_independent_evidence_level()
    {
        var capabilities = new ArdDisplayCapabilities(
            CapabilitySupport.Advertised,
            CapabilitySupport.Observed,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unsupported,
            CapabilitySupport.Advertised,
            SafeOnlinePixelFormatSwitch: true,
            SafeOnlineScaleSwitch: false,
            MaximumRefreshRate: 120);

        Assert.Equal(CapabilitySupport.Advertised, capabilities.Zlib);
        Assert.Equal(CapabilitySupport.Observed, capabilities.Rgb565);
        Assert.Equal(CapabilitySupport.Unknown, capabilities.ServerScaling);
        Assert.Equal(CapabilitySupport.Unsupported, capabilities.AppleThousands);
        Assert.Equal(CapabilitySupport.Advertised, capabilities.AppleGrayscale);
        Assert.True(capabilities.SafeOnlinePixelFormatSwitch);
        Assert.False(capabilities.SafeOnlineScaleSwitch);
        Assert.Equal(120, capabilities.MaximumRefreshRate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(30)]
    [InlineData(240)]
    public void Maximum_refresh_rate_accepts_unknown_and_inclusive_bounds(int? maximumRefreshRate)
    {
        var capabilities = CreateCapabilities(maximumRefreshRate);

        Assert.Equal(maximumRefreshRate, capabilities.MaximumRefreshRate);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(241)]
    public void Maximum_refresh_rate_rejects_values_outside_remote_evidence_bounds(int maximumRefreshRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateCapabilities(maximumRefreshRate));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(255)]
    public void Capabilities_reject_undefined_evidence_values(int invalidValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArdDisplayCapabilities(
            (CapabilitySupport)invalidValue,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            SafeOnlinePixelFormatSwitch: false,
            SafeOnlineScaleSwitch: false,
            MaximumRefreshRate: null));
    }

    [Fact]
    public void Model_does_not_expose_apple_black_and_white_encoding_1000()
    {
        var propertyNames = typeof(ArdDisplayCapabilities).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("1000", StringComparison.Ordinal));
    }

    private static ArdDisplayCapabilities CreateCapabilities(int? maximumRefreshRate) =>
        new(
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unknown,
            SafeOnlinePixelFormatSwitch: false,
            SafeOnlineScaleSwitch: false,
            MaximumRefreshRate: maximumRefreshRate);
}
