using System.IO.Compression;
using System.Text.Json;
using WinARD.Application.Ports;
using WinARD.Desktop.Services;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Desktop.Views;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Views;

public sealed class RemoteSessionDiagnosticExportStateTests
{
    [Fact]
    public void Completing_export_after_closing_does_not_reenable_export()
    {
        var state = new RemoteSessionDiagnosticExportState(serviceAvailable: true);

        Assert.True(state.IsEnabled);
        Assert.True(state.TryBeginExport());
        Assert.False(state.IsEnabled);

        state.BeginClosing();
        state.CompleteExport();

        Assert.False(state.IsEnabled);
        Assert.False(state.TryBeginExport());
    }

    [Fact]
    public void Missing_service_never_enables_export()
    {
        var state = new RemoteSessionDiagnosticExportState(serviceAvailable: false);

        Assert.False(state.IsEnabled);
        Assert.False(state.TryBeginExport());
    }

    [Fact]
    public void Concurrent_second_export_is_rejected_and_closing_permanently_disables_both_entries()
    {
        var state = new RemoteSessionDiagnosticExportState(serviceAvailable: true);

        Assert.True(state.TryBeginExport());
        Assert.False(state.IsEnabled);
        Assert.False(state.TryBeginExport());

        state.CompleteExport();

        Assert.True(state.IsEnabled);
        state.BeginClosing();
        Assert.False(state.IsEnabled);
        Assert.False(state.TryBeginExport());
    }

    [Fact]
    public void Closing_cancels_the_independent_active_export_token()
    {
        var state = new RemoteSessionDiagnosticExportState(serviceAvailable: true);

        Assert.True(state.TryBeginExport(CancellationToken.None, out var exportToken));
        Assert.False(exportToken.IsCancellationRequested);

        state.BeginClosing();

        Assert.True(state.IsClosing);
        Assert.True(exportToken.IsCancellationRequested);
        state.CompleteExport();
    }

    [Fact]
    public void Active_export_token_is_linked_to_the_session_lifetime()
    {
        using var lifetime = new CancellationTokenSource();
        var state = new RemoteSessionDiagnosticExportState(serviceAvailable: true);
        Assert.True(state.TryBeginExport(lifetime.Token, out var exportToken));

        lifetime.Cancel();

        Assert.True(exportToken.IsCancellationRequested);
        state.CompleteExport();
    }

    [Fact]
    public async Task Session_context_exports_stable_aggregate_evidence_without_sensitive_values()
    {
        const string hostMarker = "private-mac.internal";
        const string userMarker = "private-mac-user";
        const string passwordMarker = "private-password-marker";
        const string exceptionMarker = "private-exception-marker";
        const string keyMarker = "actual-key-marker";
        const string clipboardMarker = "actual-clipboard-marker";
        var profile = ConnectionProfile.Create(
            Guid.NewGuid(),
            "Private device name",
            hostMarker,
            5900,
            userMarker);
        var performance = new SessionPerformanceSnapshot(
            FrameRefreshMode.Automatic,
            TargetFramesPerSecond: 90,
            ActualFramesPerSecond: 64,
            ReceiveBytesPerSecond: 1_234_567,
            PrimaryFramebufferEncoding: (int)RfbEncodingType.Zrle,
            ResponseMilliseconds: 17,
            InputWriteMilliseconds: 3,
            InputQueueDepth: 2,
            CoalescedPointerMoves: 42,
            SampleSequence: 999);
        var session = new SessionPerformanceDiagnosticSnapshot(
            performance,
            PresentationMilliseconds: 5,
            AutomaticTargetChanges: 4,
            new Dictionary<int, long>
            {
                [(int)RfbEncodingType.Zrle] = 18,
                [(int)RfbEncodingType.Raw] = 7,
                [-321] = 2,
            },
            OtherEncodingCount: 68);
        var context = DesktopDiagnosticContextFactory.CreateSession(profile, session);
        var destination = Path.Combine(Path.GetTempPath(), $"winard-session-diag-{Guid.NewGuid():N}.zip");
        try
        {
            using var redactor = new SecretRedactor();
            using var password = redactor.Register(passwordMarker.AsSpan());
            var sink = new InMemorySafeDiagnosticSink(redactor);
            sink.Write(new SafeDiagnosticEventInput(
                "SAFE_EVENT",
                "safe-correlation",
                $"password={passwordMarker}",
                [
                    new("KeyContent", keyMarker, DiagnosticFieldCategory.Secret),
                    new("ClipboardContent", clipboardMarker, DiagnosticFieldCategory.ClipboardContent),
                ],
                new InvalidOperationException(exceptionMarker)));
            using var exporter = new DiagnosticExporter(sink, redactor);

            await exporter.ExportAsync(destination, context, CancellationToken.None);

            using var archive = ZipFile.OpenRead(destination);
            using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
            var root = document.RootElement;
            var counters = root.GetProperty("performanceCounters");
            string[] expectedCounterNames =
            [
                "Session.ActualFps",
                "Session.AutomaticTargetChanges",
                "Session.InputQueueDepth",
                "Session.InputWriteMilliseconds",
                "Session.PointerMovesCoalesced",
                "Session.PresentationMilliseconds",
                "Session.ReceiveBytesPerSecond",
                "Session.ReceiveRateInsideSshTunnel",
                "Session.RefreshMode",
                "Session.ResponseMilliseconds",
                "Session.TargetFps",
            ];
            Assert.Equal(
                expectedCounterNames,
                counters.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal(90, counters.GetProperty("Session.TargetFps").GetInt64());
            Assert.Equal(64, counters.GetProperty("Session.ActualFps").GetInt64());
            Assert.Equal(1_234_567, counters.GetProperty("Session.ReceiveBytesPerSecond").GetInt64());
            Assert.Equal(42, counters.GetProperty("Session.PointerMovesCoalesced").GetInt64());
            Assert.Equal(0, counters.GetProperty("Session.ReceiveRateInsideSshTunnel").GetInt64());
            Assert.Equal(18, root.GetProperty("profiles")[0]
                .GetProperty("encodingStatistics").GetProperty("ZRLE").GetInt64());
            Assert.Equal(2, root.GetProperty("profiles")[0]
                .GetProperty("encodingStatistics").GetProperty("Encoding.-321").GetInt64());
            Assert.Equal(68, root.GetProperty("profiles")[0]
                .GetProperty("encodingStatistics").GetProperty("Encoding.Other").GetInt64());

            var json = root.GetRawText();
            foreach (var forbidden in new[]
            {
                "Coordinate", "PointerX", "PointerY", "Keysym", "Pixel", "Ciphertext", "Sequence",
                "Private device name", hostMarker, userMarker, passwordMarker, exceptionMarker, keyMarker,
                clipboardMarker,
            })
            {
                Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void Unknown_target_fps_is_omitted_instead_of_fabricated()
    {
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.internal", 5900, "operator");
        var performance = new SessionPerformanceSnapshot(
            FrameRefreshMode.Unlimited,
            TargetFramesPerSecond: null,
            ActualFramesPerSecond: 0,
            ReceiveBytesPerSecond: 0,
            PrimaryFramebufferEncoding: null,
            ResponseMilliseconds: 0,
            InputWriteMilliseconds: 0,
            InputQueueDepth: 0,
            CoalescedPointerMoves: 0,
            SampleSequence: 0);

        var context = DesktopDiagnosticContextFactory.CreateSession(
            profile,
            new SessionPerformanceDiagnosticSnapshot(performance, 0, 0, new Dictionary<int, long>()));

        Assert.DoesNotContain("Session.TargetFps", context.PerformanceCounters.Keys);
        Assert.DoesNotContain(context.PerformanceCounters.Keys, key =>
            key.Contains("Sequence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_profile_omits_unknown_tunnel_state()
    {
        var performance = new SessionPerformanceSnapshot(
            FrameRefreshMode.Automatic,
            TargetFramesPerSecond: 60,
            ActualFramesPerSecond: 0,
            ReceiveBytesPerSecond: 0,
            PrimaryFramebufferEncoding: null,
            ResponseMilliseconds: 0,
            InputWriteMilliseconds: 0,
            InputQueueDepth: 0,
            CoalescedPointerMoves: 0,
            SampleSequence: 0);

        var context = DesktopDiagnosticContextFactory.CreateSession(
            profile: null,
            new SessionPerformanceDiagnosticSnapshot(performance, 0, 0, new Dictionary<int, long>()));

        Assert.Empty(context.Profiles);
        Assert.DoesNotContain("Session.ReceiveRateInsideSshTunnel", context.PerformanceCounters.Keys);
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return await reader.ReadToEndAsync();
    }
}
