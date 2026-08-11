namespace WinARD.Application.Quality;

public enum CapabilitySupport
{
    Unknown,
    Unsupported,
    Advertised,
    Observed,
}

public static class CapabilitySupportExtensions
{
    public static bool IsSupported(this CapabilitySupport support)
    {
        Validate(support);
        return support is CapabilitySupport.Advertised or CapabilitySupport.Observed;
    }

    public static bool IsObserved(this CapabilitySupport support)
    {
        Validate(support);
        return support is CapabilitySupport.Observed;
    }

    internal static CapabilitySupport Validate(CapabilitySupport support)
    {
        if (!Enum.IsDefined(support))
        {
            throw new ArgumentOutOfRangeException(nameof(support));
        }

        return support;
    }
}

public sealed record ArdDisplayCapabilities(
    CapabilitySupport Zlib,
    CapabilitySupport Rgb565,
    CapabilitySupport ServerScaling,
    CapabilitySupport AppleThousands,
    CapabilitySupport AppleGrayscale,
    bool SafeOnlinePixelFormatSwitch,
    bool SafeOnlineScaleSwitch,
    int? MaximumRefreshRate)
{
    public CapabilitySupport Zlib { get; } = CapabilitySupportExtensions.Validate(Zlib);
    public CapabilitySupport Rgb565 { get; } = CapabilitySupportExtensions.Validate(Rgb565);
    public CapabilitySupport ServerScaling { get; } = CapabilitySupportExtensions.Validate(ServerScaling);
    public CapabilitySupport AppleThousands { get; } = CapabilitySupportExtensions.Validate(AppleThousands);
    public CapabilitySupport AppleGrayscale { get; } = CapabilitySupportExtensions.Validate(AppleGrayscale);
    public int? MaximumRefreshRate { get; } = ValidateMaximumRefreshRate(MaximumRefreshRate);

    private static int? ValidateMaximumRefreshRate(int? maximumRefreshRate)
    {
        if (maximumRefreshRate is < 30 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRefreshRate));
        }

        return maximumRefreshRate;
    }
}
