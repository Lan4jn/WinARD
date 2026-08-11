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
        RemoteSessionDiagnosticQualitySnapshot snapshot)
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
                    qualityCapabilities),
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
        ArdDisplayCapabilities capabilities)
    {
        return new DiagnosticQualitySummary(
            profile.Preset.ToString(),
            profile.TargetBytesPerSecond,
            decision.Level.ToString(),
            decision.ContentState.ToString(),
            decision.Color.ToString(),
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

    private static int ScalePercent(QualityScale scale) => scale switch
    {
        QualityScale.Native => 100,
        QualityScale.Percent75 => 75,
        QualityScale.Percent50 => 50,
        _ => throw new ArgumentOutOfRangeException(nameof(scale)),
    };

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
