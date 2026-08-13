using WinARD.Application.Ports;
using WinARD.Domain.Connections;

namespace WinARD.Application.Quality;

public static class QualityBootstrapPlanner
{
    private static readonly int[] PreferredEncodings = [16, 6, 0, 1, -239, -223];
    private static readonly int[] FallbackEncodings = [6, 16, 0, 1, -239, -223];

    public static QualityBootstrapPlan CreatePlan(
        QualityProfile profile,
        ArdDisplayCapabilities capabilities,
        QualityDecoderGates decoderGates)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(decoderGates);

        var (pixelFormat, reason) = SelectPreferredSettings(profile, capabilities, decoderGates);
        var scaleFactor = ResolveScaleFactor(profile.Scale, profile.TargetBytesPerSecond);
        return new QualityBootstrapPlan(
            new QualityBootstrapSettings(pixelFormat, PreferredEncodings, reason, scaleFactor),
            new QualityBootstrapSettings(
                RemotePixelFormatKind.Bgra32,
                FallbackEncodings,
                QualityBootstrapReason.SafeFallback,
                scaleFactor: 1d));
    }

    public static double ResolveScaleFactor(QualityScale scale, long? targetBytesPerSecond) => scale switch
    {
        QualityScale.Automatic => targetBytesPerSecond switch
        {
            <= 1L * 1024 * 1024 => 0.25d,
            <= 2L * 1024 * 1024 => 0.5d,
            <= 4L * 1024 * 1024 => 0.75d,
            _ => 1d,
        },
        QualityScale.Native => 1d,
        QualityScale.Percent75 => 0.75d,
        QualityScale.Percent50 => 0.5d,
        QualityScale.Percent25 => 0.25d,
        _ => throw new ArgumentOutOfRangeException(nameof(scale)),
    };

    private static (RemotePixelFormatKind PixelFormat, QualityBootstrapReason Reason) SelectPreferredSettings(
        QualityProfile profile,
        ArdDisplayCapabilities capabilities,
        QualityDecoderGates decoderGates)
    {
        if (profile.Preset is QualityPreset.Original ||
            (profile.Color is QualityColor.Full32 && profile.ColorLocked))
        {
            return (RemotePixelFormatKind.Bgra32, QualityBootstrapReason.UserFull32);
        }

        if (profile.Preset is QualityPreset.Custom && profile.Color is QualityColor.Color16)
        {
            return (RemotePixelFormatKind.Rgb565, QualityBootstrapReason.UserColor16);
        }

        if (profile.Color is QualityColor.Grayscale &&
            (!capabilities.AppleGrayscale1001.IsObserved() || !decoderGates.AppleGrayscale1001Approved))
        {
            return (RemotePixelFormatKind.Rgb565, QualityBootstrapReason.CapabilityLimited);
        }

        return (RemotePixelFormatKind.Rgb565, QualityBootstrapReason.AutomaticBandwidth);
    }
}
