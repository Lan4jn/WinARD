using System.Runtime.InteropServices;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Remote.Protocol.Encodings;

namespace WinARD.Desktop.Services;

internal static class DesktopDiagnosticContextFactory
{
    public static DiagnosticExportContext CreateSession(
        ConnectionProfile? profile,
        SessionPerformanceDiagnosticSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
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
        return Create(profiles, counters);
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
        foreach (var (encoding, count) in encodingCounts.OrderBy(pair => pair.Key))
        {
            if (count <= 0)
            {
                continue;
            }

            statistics[EncodingName(encoding)] = count;
        }

        if (otherEncodingCount > 0)
        {
            statistics["Encoding.Other"] = otherEncodingCount;
        }

        return statistics;
    }

    private static string EncodingName(int encoding) => encoding switch
    {
        (int)RfbEncodingType.Raw => "Raw",
        (int)RfbEncodingType.CopyRect => "CopyRect",
        (int)RfbEncodingType.Zrle => "ZRLE",
        (int)RfbEncodingType.DesktopSize => "DesktopSize",
        (int)RfbEncodingType.Cursor => "Cursor",
        (int)RfbEncodingType.ArdDisplayInfo => "ARD.DisplayInfo",
        (int)RfbEncodingType.ArdSessionEncryption => "ARD.SessionEncryption",
        (int)RfbEncodingType.ArdDisplayInfo2 => "ARD.DisplayInfo2",
        _ => $"Encoding.{encoding}",
    };
}
