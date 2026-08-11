namespace WinARD.Application.Quality;

public enum CapabilitySupport
{
    Unknown,
    Unsupported,

    /// <summary>
    /// The peer advertised the capability. Advertisement is neither observation nor approval to enable it.
    /// </summary>
    Advertised,

    Observed,
}

public static class CapabilitySupportExtensions
{
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

/// <summary>
/// Records connection-specific protocol evidence. These values do not by themselves authorize
/// negotiating or decoding an Apple-private encoding.
/// </summary>
public sealed record ArdDisplayCapabilities(
    CapabilitySupport Zlib,
    CapabilitySupport Rgb565,
    CapabilitySupport ServerScaling,
    CapabilitySupport AppleColor1002,
    CapabilitySupport AppleGrayscale1001,
    bool SafeOnlinePixelFormatSwitch,
    bool SafeOnlineScaleSwitch,
    int? MaximumRefreshRate)
{
    public CapabilitySupport Zlib { get; } = CapabilitySupportExtensions.Validate(Zlib);
    public CapabilitySupport Rgb565 { get; } = CapabilitySupportExtensions.Validate(Rgb565);
    public CapabilitySupport ServerScaling { get; } = CapabilitySupportExtensions.Validate(ServerScaling);

    /// <summary>
    /// Gets evidence about encoding 1002, not permission to enable it. Formal availability requires
    /// a separate, explicit decoder evidence gate; no such gate exists in this model.
    /// </summary>
    public CapabilitySupport AppleColor1002 { get; } = CapabilitySupportExtensions.Validate(AppleColor1002);

    /// <summary>
    /// Gets evidence about encoding 1001, not permission to enable it. Formal availability requires
    /// a separate, explicit decoder evidence gate; no such gate exists in this model.
    /// </summary>
    public CapabilitySupport AppleGrayscale1001 { get; } =
        CapabilitySupportExtensions.Validate(AppleGrayscale1001);

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
