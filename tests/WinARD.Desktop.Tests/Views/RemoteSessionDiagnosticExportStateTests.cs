using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
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
    public void New_session_context_retains_successful_automatic_reconnect_summary()
    {
        var profile = ConnectionProfile.Create(
            Guid.NewGuid(), "Mac", "private-host", 5900, "private-user");
        var performance = new SessionPerformanceDiagnosticSnapshot(
            new SessionPerformanceSnapshot(
                FrameRefreshMode.Automatic, null, 0, 0, null, 0, 0, 0, 0, 0),
            0, 0, new Dictionary<int, long>());

        var context = DesktopDiagnosticContextFactory.CreateSession(
            profile,
            CreateDiagnosticSnapshot(profile, performance),
            new DiagnosticReconnectSummary("Succeeded", 3, 0));

        Assert.Equal(new DiagnosticReconnectSummary("Succeeded", 3, 0), context.Reconnect);
    }

    [Fact]
    public void Session_context_maps_atomic_bootstrap_quality_and_transfer_evidence()
    {
        var profile = ConnectionProfile.Create(
            Guid.NewGuid(), "Mac", "private.internal", 5900, "private-user")
            .WithQualityProfile(QualityProfile.CreateCustom(
                2 * 1024 * 1024,
                QualityColor.Color16,
                QualityScale.Percent75,
                FrameRefreshPolicy.Automatic));
        var performance = new SessionPerformanceSnapshot(
            FrameRefreshMode.Automatic, 60, 30, 1024, (int)RfbEncodingType.Zlib,
            10, 2, 0, 0, 1);
        var session = new SessionPerformanceDiagnosticSnapshot(
            performance, 2, 0, new Dictionary<int, long>())
        {
            Transfer = new SessionTransferDiagnosticSnapshot(
                3, 2_000, 800, 400, 200,
                new Dictionary<int, long>
                {
                    [(int)RfbEncodingType.Zlib] = 700,
                    [(int)RfbEncodingType.CopyRect] = 100,
                },
                9),
        };
        var decision = new QualityDecision(
            1, QualityContentState.Motion, QualityLevel.Q2, QualityColor.Color16,
            QualityScale.Percent75, 60, QualityDecisionReason.MotionDetected,
            true, false, false, QualityLevel.Q2, QualityContentState.Motion);
        var presentation = new QualityPresentationSnapshot(
            profile.Quality, decision, QualityTransitionStatus.Applied, performance, 1, 1,
            new QualityActualState(RemotePixelFormatKind.Bgra32, QualityActualEncoding.Zlib, true));
        var snapshot = new RemoteSessionDiagnosticQualitySnapshot(
            session,
            presentation,
            CreateDiagnosticSnapshot(profile, session).QualityObservation,
            ArdDisplayCapabilities.Unknown)
        {
            BootstrapState = new QualityBootstrapState(
                QualityBootstrapAttempt.Fallback,
                new QualityBootstrapSettings(
                    RemotePixelFormatKind.Bgra32,
                    [6, 16, 0, 1, -239, -223],
                    QualityBootstrapReason.SafeFallback),
                QualityBootstrapFailureReason.DecoderFailure)
                .ConfirmApplied(new RemoteFramebufferSize(1680, 1050), 3),
            PreferredBootstrap = new QualityBootstrapSettings(
                RemotePixelFormatKind.Rgb565,
                [16, 6, 0, 1, -239, -223],
                QualityBootstrapReason.UserColor16),
        };

        var context = DesktopDiagnosticContextFactory.CreateSession(profile, snapshot);

        Assert.Equal("Full32", context.Quality!.Color);
        var transfer = Assert.IsType<DiagnosticTransferSummary>(context.Transfer);
        Assert.Equal("Rgb565", transfer.PreferredPixelFormat);
        Assert.Equal("Bgra32", transfer.AppliedPixelFormat);
        Assert.Equal("Fallback", transfer.BootstrapAttempt);
        Assert.Equal("DecoderFailure", transfer.BootstrapFallbackReason);
        Assert.Equal("ZrleFirst", transfer.PreferredEncodingOrder);
        Assert.Equal("Color16", transfer.DesiredColor);
        Assert.Equal("Full32", transfer.AppliedColor);
        Assert.Equal(75, transfer.DesiredScalePercent);
        Assert.Equal(100, transfer.ResolvedScalePercent);
        Assert.Equal(100, transfer.AppliedScalePercent);
        Assert.Equal(1680, transfer.FirstFrameWidth);
        Assert.Equal(1050, transfer.FirstFrameHeight);
        Assert.Equal(3, transfer.FirstFrameRectangleCount);
        Assert.Equal(3, transfer.RectangleCount);
        Assert.Equal(9, transfer.EncodingWireBytes["Other"]);
        Assert.Equal(700, transfer.EncodingWireBytes["Zlib"]);
    }

    [Fact]
    public void Diagnostic_quality_summary_keeps_the_public_21_parameter_constructor_and_deconstruct()
    {
        var constructor = typeof(DiagnosticQualitySummary).GetConstructors()
            .Single(candidate => candidate.IsPublic);
        var deconstruct = typeof(DiagnosticQualitySummary).GetMethod(
            "Deconstruct", BindingFlags.Public | BindingFlags.Instance);

        Type[] expectedTypes =
        [
            typeof(string), typeof(long?), typeof(string), typeof(string), typeof(string),
            typeof(int), typeof(string), typeof(int?), typeof(int), typeof(long), typeof(long),
            typeof(int), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(bool), typeof(bool), typeof(string), typeof(bool),
        ];
        string[] expectedNames =
        [
            "Preset", "TargetBytesPerSecond", "QualityLevel", "ContentState", "Color",
            "ScalePercent", "EncodingName", "TargetFramesPerSecond", "ActualFramesPerSecond",
            "AverageBytesPerSecond", "PeakBytesPerSecond", "ResponseMilliseconds",
            "ZlibCapability", "Rgb565Capability", "ServerScalingCapability",
            "AppleColor1002Capability", "AppleGrayscale1001Capability",
            "SafeOnlinePixelFormatSwitch", "SafeOnlineScaleSwitch", "Reason", "TargetSatisfied",
        ];

        Assert.Equal(expectedTypes, constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(expectedNames, constructor.GetParameters().Select(parameter => parameter.Name));
        Assert.NotNull(deconstruct);
        var deconstructParameters = deconstruct!.GetParameters();
        Assert.Equal(expectedTypes.Select(type => type.MakeByRefType()),
            deconstructParameters.Select(parameter => parameter.ParameterType));
        Assert.All(deconstructParameters, parameter => Assert.True(parameter.IsOut));
        Assert.Equal(expectedNames, deconstructParameters.Select(parameter => parameter.Name));
    }
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
            PrimaryFramebufferEncoding: (int)RfbEncodingType.Zlib,
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
                [(int)RfbEncodingType.Zlib] = 9,
                [(int)RfbEncodingType.Zrle] = 18,
                [(int)RfbEncodingType.Raw] = 7,
                [-321] = 2,
            },
            OtherEncodingCount: 68);
        var decision = new QualityDecision(
            generation: 4,
            QualityContentState.Motion,
            QualityLevel.Q3,
            QualityColor.Color16,
            QualityScale.Percent50,
            targetFramesPerSecond: 45,
            QualityDecisionReason.SevereOverTarget,
            targetSatisfied: false,
            levelChanged: true,
            contentStateChanged: true,
            previousLevel: QualityLevel.Q2,
            previousContentState: QualityContentState.Interactive);
        var observation = new QualityObservation(
            DateTimeOffset.UtcNow,
            averageBytesPerSecond5s: 9_000_000,
            peakBytesPerSecond5s: 12_000_000,
            actualFramesPerSecond: 38,
            responseTime: TimeSpan.FromMilliseconds(86),
            decodeTime: TimeSpan.FromMilliseconds(5),
            presentationTime: TimeSpan.FromMilliseconds(4),
            dirtyCoverage: 0.9,
            sinceLastInput: TimeSpan.FromMilliseconds(20),
            pointerDragActive: false,
            scrollActive: true,
            pendingInputCount: 2);
        var capabilities = new ArdDisplayCapabilities(
            CapabilitySupport.Observed,
            CapabilitySupport.Observed,
            CapabilitySupport.Observed,
            CapabilitySupport.Unknown,
            CapabilitySupport.Unsupported,
            SafeOnlinePixelFormatSwitch: true,
            SafeOnlineScaleSwitch: false,
            MaximumRefreshRate: 90);
        var presentation = new QualityPresentationSnapshot(
            profile.Quality,
            decision,
            QualityTransitionStatus.Applied,
            performance,
            Epoch: 1,
            Version: 1);
        var context = DesktopDiagnosticContextFactory.CreateSession(
            profile,
            new RemoteSessionDiagnosticQualitySnapshot(
                session,
                presentation,
                observation,
                capabilities));
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
            Assert.Equal(9, root.GetProperty("profiles")[0]
                .GetProperty("encodingStatistics").GetProperty("Zlib").GetInt64());
            Assert.False(root.GetProperty("profiles")[0]
                .GetProperty("encodingStatistics").TryGetProperty("Encoding.-321", out _));
            Assert.Equal(70, root.GetProperty("profiles")[0]
                .GetProperty("encodingStatistics").GetProperty("Encoding.Other").GetInt64());

            var quality = root.GetProperty("quality");
            Assert.Equal("Automatic", quality.GetProperty("preset").GetString());
            Assert.Equal("Motion", quality.GetProperty("contentState").GetString());
            Assert.Equal("Q3", quality.GetProperty("qualityLevel").GetString());
            Assert.Equal("Zlib", quality.GetProperty("encodingName").GetString());
            Assert.Equal(9_000_000, quality.GetProperty("averageBps").GetInt64());
            Assert.Equal(12_000_000, quality.GetProperty("peakBps").GetInt64());
            Assert.Equal("SevereOverTarget", quality.GetProperty("reason").GetString());
            Assert.False(quality.GetProperty("targetSatisfied").GetBoolean());

            var json = root.GetRawText();
            foreach (var forbidden in new[]
            {
                "Coordinate", "PointerX", "PointerY", "Keysym", "Ciphertext", "Sequence",
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
            CreateDiagnosticSnapshot(
                profile,
                new SessionPerformanceDiagnosticSnapshot(performance, 0, 0, new Dictionary<int, long>())));

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
            CreateDiagnosticSnapshot(
                profile: null,
                new SessionPerformanceDiagnosticSnapshot(performance, 0, 0, new Dictionary<int, long>())));

        Assert.Empty(context.Profiles);
        Assert.DoesNotContain("Session.ReceiveRateInsideSshTunnel", context.PerformanceCounters.Keys);
    }

    [Fact]
    public void Unknown_encoding_counts_saturate_the_fixed_other_category()
    {
        var performance = new SessionPerformanceSnapshot(
            FrameRefreshMode.Automatic,
            TargetFramesPerSecond: 60,
            ActualFramesPerSecond: 0,
            ReceiveBytesPerSecond: 0,
            PrimaryFramebufferEncoding: -1,
            ResponseMilliseconds: 0,
            InputWriteMilliseconds: 0,
            InputQueueDepth: 0,
            CoalescedPointerMoves: 0,
            SampleSequence: 0);
        var session = new SessionPerformanceDiagnosticSnapshot(
            performance,
            0,
            0,
            new Dictionary<int, long> { [-1] = long.MaxValue, [-2] = 1 });
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.internal", 5900, "operator");

        var context = DesktopDiagnosticContextFactory.CreateSession(
            profile,
            CreateDiagnosticSnapshot(profile, session));

        Assert.Equal(long.MaxValue, context.Profiles[0].EncodingStatistics!["Encoding.Other"]);
    }

    private static RemoteSessionDiagnosticQualitySnapshot CreateDiagnosticSnapshot(
        ConnectionProfile? profile,
        SessionPerformanceDiagnosticSnapshot performance)
    {
        var presentation = new QualityPresentationSnapshot(
            profile?.Quality ?? QualityProfile.Automatic,
            Decision: null,
            QualityTransitionStatus.NoChange,
            performance.Performance,
            Epoch: 0,
            Version: 0);
        var observation = new QualityObservation(
            DateTimeOffset.UnixEpoch,
            averageBytesPerSecond5s: performance.Performance.ReceiveBytesPerSecond,
            peakBytesPerSecond5s: performance.Performance.ReceiveBytesPerSecond,
            actualFramesPerSecond: performance.Performance.ActualFramesPerSecond,
            responseTime: TimeSpan.FromMilliseconds(performance.Performance.ResponseMilliseconds),
            decodeTime: TimeSpan.Zero,
            presentationTime: TimeSpan.FromMilliseconds(performance.PresentationMilliseconds),
            dirtyCoverage: 0,
            sinceLastInput: TimeSpan.Zero,
            pointerDragActive: false,
            scrollActive: false,
            pendingInputCount: 0);
        return new RemoteSessionDiagnosticQualitySnapshot(
            performance,
            presentation,
            observation,
            ArdDisplayCapabilities.Unknown);
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return await reader.ReadToEndAsync();
    }
}
