using System.Runtime.InteropServices;
using WinARD.Application.Quality;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Remote.Protocol.Encodings;

namespace WinARD.Desktop.Services;

internal static class DesktopDiagnosticContextFactory
{
    public static DiagnosticExportContext CreateSession(
        ConnectionProfile? profile,
        RemoteSessionDiagnosticQualitySnapshot snapshot,
        DiagnosticReconnectSummary? reconnect = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var session = snapshot.Performance;
        var qualityPresentation = snapshot.QualityPresentation;
        var qualityObservation = snapshot.QualityObservation;
        var qualityCapabilities = snapshot.QualityCapabilities;
        var performance = session.Performance;
        var counters = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["Session.RefreshMode"] = (long)performance.Mode,
            ["Session.ActualFps"] = performance.ActualFramesPerSecond,
            ["Session.ReceiveBytesPerSecond"] = performance.ReceiveBytesPerSecond,
            ["Session.ResponseMilliseconds"] = performance.ResponseMilliseconds,
            ["Session.PresentationMilliseconds"] = session.PresentationMilliseconds,
            ["Session.InputWriteMilliseconds"] = performance.InputWriteMilliseconds,
            ["Session.InputQueueDepth"] = performance.InputQueueDepth,
            ["Session.PointerMovesCoalesced"] = performance.CoalescedPointerMoves,
            ["Session.AutomaticTargetChanges"] = session.AutomaticTargetChanges,
        };
        if (performance.TargetFramesPerSecond is { } targetFramesPerSecond)
        {
            counters["Session.TargetFps"] = targetFramesPerSecond;
        }

        if (profile is not null)
        {
            counters["Session.ReceiveRateInsideSshTunnel"] =
                profile.TransportMode == TransportMode.Ssh ? 1 : 0;
        }

        var profiles = profile is null
            ? Array.Empty<DiagnosticProfileSummary>()
            :
            [
                new DiagnosticProfileSummary(
                    "Remote session",
                    profile.Host,
                    profile.Port,
                    string.Empty,
                    "RFB 3.x",
                    "ARD-30",
                    BuildEncodingStatistics(session.EncodingCounts, session.OtherEncodingCount)),
            ];
        return Create(profiles, counters) with
        {
            Quality = qualityPresentation.Decision is not { } decision
                ? null
                : BuildQualitySummary(
                    qualityPresentation.Profile,
                    qualityPresentation.Performance,
                    decision,
                    qualityObservation,
                    qualityCapabilities,
                    qualityPresentation.Actual),
            Transfer = BuildTransferSummary(snapshot),
            Reconnect = reconnect,
        };
    }

    public static DiagnosticExportContext Create(
        IReadOnlyList<DiagnosticProfileSummary>? profiles = null,
        IReadOnlyDictionary<string, long>? performanceCounters = null) =>
        new(
            new DiagnosticApplicationInfo(
                "WinARD",
                typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription,
                typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version?.ToString() ?? "unknown"),
            profiles ?? [],
            performanceCounters ?? new Dictionary<string, long>(),
            IncludeHosts: false);

    private static Dictionary<string, long> BuildEncodingStatistics(
        IReadOnlyDictionary<int, long> encodingCounts,
        long otherEncodingCount)
    {
        var statistics = new Dictionary<string, long>(StringComparer.Ordinal);
        var other = otherEncodingCount;
        foreach (var (encoding, count) in encodingCounts.OrderBy(pair => pair.Key))
        {
            if (count <= 0)
            {
                continue;
            }

            var name = KnownEncodingName(encoding);
            if (name is null)
            {
                other = SaturatingAdd(other, count);
                continue;
            }

            statistics[name] = count;
        }

        if (other > 0)
        {
            statistics["Encoding.Other"] = other;
        }

        return statistics;
    }

    private static DiagnosticQualitySummary BuildQualitySummary(
        QualityProfile profile,
        SessionPerformanceSnapshot performance,
        QualityDecision decision,
        QualityObservation observation,
        ArdDisplayCapabilities capabilities,
        QualityActualState? actual)
    {
        return new DiagnosticQualitySummary(
            profile.Preset.ToString(),
            profile.TargetBytesPerSecond,
            decision.Level.ToString(),
            decision.ContentState.ToString(),
            AppliedColor(actual, decision.Color),
            ScalePercent(decision.Scale),
            performance.PrimaryFramebufferEncoding is { } encoding
                ? KnownEncodingName(encoding) ?? "Other"
                : "Other",
            decision.TargetFramesPerSecond,
            RoundNonNegative(observation.ActualFramesPerSecond),
            RoundNonNegativeLong(observation.AverageBytesPerSecond5s),
            RoundNonNegativeLong(observation.PeakBytesPerSecond5s),
            RoundNonNegative(observation.ResponseTime.TotalMilliseconds),
            capabilities.Zlib.ToString(),
            capabilities.Rgb565.ToString(),
            capabilities.ServerScaling.ToString(),
            capabilities.AppleColor1002.ToString(),
            capabilities.AppleGrayscale1001.ToString(),
            capabilities.SafeOnlinePixelFormatSwitch,
            capabilities.SafeOnlineScaleSwitch,
            decision.Reason.ToString(),
            decision.TargetSatisfied);
    }

    private static DiagnosticTransferSummary BuildTransferSummary(
        RemoteSessionDiagnosticQualitySnapshot snapshot)
    {
        var transfer = snapshot.Performance.Transfer;
        var encodingBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        var other = transfer.OtherEncodingWirePayloadBytes;
        foreach (var (encoding, byteCount) in transfer.WirePayloadBytesByEncoding)
        {
            var name = KnownEncodingName(encoding);
            if (name is null)
            {
                other = SaturatingAdd(other, byteCount);
            }
            else
            {
                encodingBytes[name] = byteCount;
            }
        }

        if (other > 0)
        {
            encodingBytes["Other"] = other;
        }

        var actualPixelFormat = snapshot.QualityPresentation.Actual?.PixelFormat ??
            snapshot.BootstrapState.ActualQuality.PixelFormat;
        return new DiagnosticTransferSummary(
            snapshot.PreferredBootstrap.PixelFormat.ToString(),
            actualPixelFormat.ToString(),
            snapshot.BootstrapState.Attempt?.ToString(),
            snapshot.BootstrapState.PreferredFailureReason?.ToString(),
            PreferredEncodingOrder(snapshot.PreferredBootstrap.Encodings),
            transfer.RectangleCount,
            transfer.PixelArea,
            transfer.WirePayloadBytes,
            transfer.BytesPerPixelMilli,
            transfer.DirtyCoveragePermille,
            snapshot.QualityPresentation.Profile.Color.ToString(),
            AppliedColor(snapshot.QualityPresentation.Actual, PixelFormatColorValue(actualPixelFormat)),
            encodingBytes)
        {
            DesiredScalePercent = DesiredScalePercent(snapshot.QualityPresentation.Profile.Scale),
            ResolvedScalePercent = ScalePercent(snapshot.PreferredBootstrap.ScaleFactor),
            AppliedScalePercent = snapshot.BootstrapState.AppliedScaleFactor is { } applied
                ? ScalePercent(applied)
                : null,
            FirstFrameWidth = snapshot.BootstrapState.FirstFrameSize?.Width,
            FirstFrameHeight = snapshot.BootstrapState.FirstFrameSize?.Height,
            FirstFrameRectangleCount = snapshot.BootstrapState.FirstFrameRectangleCount,
        };
    }

    private static string? PreferredEncodingOrder(IReadOnlyList<int> encodings) =>
        encodings.Count > 0 ? encodings[0] switch
        {
            (int)RfbEncodingType.Zrle => "ZrleFirst",
            (int)RfbEncodingType.Zlib => "ZlibFirst",
            _ => null,
        } : null;

    private static string PixelFormatColor(WinARD.Application.Ports.RemotePixelFormatKind format) =>
        format switch
        {
            WinARD.Application.Ports.RemotePixelFormatKind.Bgra32 => "Full32",
            WinARD.Application.Ports.RemotePixelFormatKind.Rgb565 => "Color16",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    private static QualityColor PixelFormatColorValue(
        WinARD.Application.Ports.RemotePixelFormatKind format) => format switch
        {
            WinARD.Application.Ports.RemotePixelFormatKind.Bgra32 => QualityColor.Full32,
            WinARD.Application.Ports.RemotePixelFormatKind.Rgb565 => QualityColor.Color16,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    private static string AppliedColor(QualityActualState? actual, QualityColor fallback) =>
        actual?.Encoding == QualityActualEncoding.AppleGrayscale
            ? QualityColor.Grayscale.ToString()
            : actual is { } state
                ? PixelFormatColor(state.PixelFormat)
                : fallback.ToString();

    private static int ScalePercent(QualityScale scale) => scale switch
    {
        QualityScale.Percent100 => 100,
        QualityScale.Percent75 => 75,
        QualityScale.Percent50 => 50,
        QualityScale.Percent25 => 25,
        _ => throw new ArgumentOutOfRangeException(nameof(scale)),
    };

    private static int ScalePercent(double scale) => scale switch
    {
        0.25d => 25,
        0.5d => 50,
        0.75d => 75,
        1d => 100,
        _ => throw new ArgumentOutOfRangeException(nameof(scale)),
    };

    private static int? DesiredScalePercent(QualityScale scale) => scale == QualityScale.Automatic
        ? null
        : ScalePercent(scale);

    private static int RoundNonNegative(double value) =>
        (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, int.MaxValue);

    private static long RoundNonNegativeLong(double value) =>
        (long)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, long.MaxValue);

    private static long SaturatingAdd(long first, long second) =>
        second > long.MaxValue - first ? long.MaxValue : first + second;

    internal static string? KnownEncodingName(int encoding) => encoding switch
    {
        (int)RfbEncodingType.Raw => "Raw",
        (int)RfbEncodingType.CopyRect => "CopyRect",
        (int)RfbEncodingType.Zlib => "Zlib",
        (int)RfbEncodingType.Zrle => "ZRLE",
        (int)RfbEncodingType.DesktopSize => "DesktopSize",
        (int)RfbEncodingType.Cursor => "Cursor",
        (int)RfbEncodingType.ArdDisplayInfo => "ARD.DisplayInfo",
        (int)RfbEncodingType.ArdSessionEncryption => "ARD.SessionEncryption",
        (int)RfbEncodingType.ArdDisplayInfo2 => "ARD.DisplayInfo2",
        _ => null,
    };
}
