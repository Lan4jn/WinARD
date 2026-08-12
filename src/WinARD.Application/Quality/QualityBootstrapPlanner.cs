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
        return new QualityBootstrapPlan(
            new QualityBootstrapSettings(pixelFormat, PreferredEncodings, reason),
            new QualityBootstrapSettings(
                RemotePixelFormatKind.Bgra32,
                FallbackEncodings,
                QualityBootstrapReason.SafeFallback));
    }

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
