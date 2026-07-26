using System.Runtime.InteropServices;
using WinARD.Infrastructure.Diagnostics;

namespace WinARD.Desktop.Services;

internal static class DesktopDiagnosticContextFactory
{
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
}
