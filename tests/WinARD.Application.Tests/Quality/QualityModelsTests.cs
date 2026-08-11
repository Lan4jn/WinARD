using WinARD.Application.Quality;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Application.Tests.Quality;

public sealed class QualityModelsTests
{
    [Theory]
    [InlineData(CapabilitySupport.Unknown, false)]
    [InlineData(CapabilitySupport.Unsupported, false)]
    [InlineData(CapabilitySupport.Advertised, false)]
    [InlineData(CapabilitySupport.Observed, true)]
    public void Only_observed_evidence_is_observed(
        CapabilitySupport support,
        bool isObserved)
    {
        Assert.Equal(isObserved, support.IsObserved());
    }

    [Fact]
    public void Evidence_queries_do_not_generalize_advertising_into_enablement()
    {
        var booleanQueries = typeof(CapabilitySupportExtensions)
            .GetMethods(System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Where(method => method.ReturnType == typeof(bool))
            .Select(method => method.Name);

        Assert.Equal([nameof(CapabilitySupportExtensions.IsObserved)], booleanQueries);
        Assert.False(CapabilitySupport.Advertised.IsObserved());
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
        Assert.Equal(CapabilitySupport.Unsupported, capabilities.AppleColor1002);
        Assert.Equal(CapabilitySupport.Advertised, capabilities.AppleGrayscale1001);
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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Each_capability_rejects_an_undefined_evidence_value(int capabilityIndex)
    {
        var values = Enumerable.Repeat(CapabilitySupport.Unknown, 5).ToArray();
        values[capabilityIndex] = (CapabilitySupport)4;

        Assert.Throws<ArgumentOutOfRangeException>(() => new ArdDisplayCapabilities(
            values[0],
            values[1],
            values[2],
            values[3],
            values[4],
            SafeOnlinePixelFormatSwitch: false,
            SafeOnlineScaleSwitch: false,
            MaximumRefreshRate: null));
    }

    [Fact]
    public void Apple_evidence_properties_use_explicit_encoding_names_without_1000()
    {
        var propertyNames = typeof(ArdDisplayCapabilities).GetProperties().Select(property => property.Name);

        Assert.Contains(nameof(ArdDisplayCapabilities.AppleColor1002), propertyNames);
        Assert.Contains(nameof(ArdDisplayCapabilities.AppleGrayscale1001), propertyNames);
        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("1000", StringComparison.Ordinal) ||
            name is "AppleThousands" or "AppleGrayscale");
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
