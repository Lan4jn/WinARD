using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinARD.Infrastructure.Diagnostics;
using Xunit;

namespace WinARD.Infrastructure.Tests;

public sealed class DiagnosticExporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"winard-diag-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void EventTtlMustStayWithinTheDocumentedBound(int days)
    {
        using var redactor = new SecretRedactor();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticExporter(
            new InMemorySafeDiagnosticSink(redactor),
            redactor,
            new DiagnosticExportLimits(MaxEventAge: TimeSpan.FromDays(days))));
    }

    [Fact]
    public async Task ExportContainsOnlyWhitelistedRedactedJsonAndManifest()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "report.zip");
        using var redactor = new SecretRedactor();
        using var registration = redactor.Register("never-export-this-secret".AsSpan());
        var sink = new InMemorySafeDiagnosticSink(redactor, 20, 256);
        sink.Write(new SafeDiagnosticEventInput(
            "ARD_AUTH_REJECTED",
            "correlation-123",
            "failure never-export-this-secret clipboard=private text",
            [
                new("ClipboardContent", "private text", DiagnosticFieldCategory.ClipboardContent),
                new("securityType", "30"),
            ],
            new InvalidOperationException("never-export-this-secret")));
        registration.Dispose();
        var exporter = new DiagnosticExporter(sink, redactor);
        var context = new DiagnosticExportContext(
            new DiagnosticApplicationInfo("WinARD", "1.2.3", "Windows 11", ".NET 8", "1.6"),
            [new DiagnosticProfileSummary("Office Mac", "mac.internal", 5900, "mac-user", "3.8", "ARD-30")],
            new Dictionary<string, long> { ["framesPresented"] = 42 },
            IncludeHosts: false);

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        Assert.Equal(
            ["diagnostics.json", "manifest.json"],
            archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray());
        var content = await ReadAllAsync(archive);
        Assert.DoesNotContain("never-export-this-secret", content, StringComparison.Ordinal);
        Assert.DoesNotContain("private text", content, StringComparison.Ordinal);
        Assert.DoesNotContain("mac.internal", content, StringComparison.Ordinal);
        Assert.DoesNotContain("hostSha256", content, StringComparison.Ordinal);
        Assert.Contains("excluded", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQLite", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redactionVersion", content, StringComparison.Ordinal);
        Assert.DoesNotContain(".db", archive.Entries.Select(entry => entry.FullName), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("-wal", archive.Entries.Select(entry => entry.FullName), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationLeavesExistingDestinationUntouchedAndCleansTemporaryFile()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "report.zip");
        await File.WriteAllTextAsync(destination, "existing");
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor, 4, 64);
        var exporter = new DiagnosticExporter(sink, redactor);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(
            destination,
            DiagnosticExportContext.Empty,
            cancellation.Token));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CancellationAfterSnapshotStopsTheWriteAndReleasesTheExportGate()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "cancel-during-write.zip");
        await File.WriteAllTextAsync(destination, "existing");
        using var redactor = new SecretRedactor();
        using var cancellation = new CancellationTokenSource();
        var sink = new CancellingSnapshotSink(cancellation);
        using var exporter = new DiagnosticExporter(sink, redactor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(
            destination,
            DiagnosticExportContext.Empty,
            cancellation.Token));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.TopDirectoryOnly));
        var valid = Path.Combine(_directory, "after-write-cancel.zip");
        await exporter.ExportAsync(valid, DiagnosticExportContext.Empty, CancellationToken.None);
        Assert.True(File.Exists(valid));
    }

    [Fact]
    public async Task DisposeDuringExportDoesNotInvalidateTheActiveOperationGate()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "dispose-race.zip");
        using var redactor = new SecretRedactor();
        var sink = new BlockingSnapshotSink();
        var exporter = new DiagnosticExporter(sink, redactor);

        var export = Task.Run(() => exporter.ExportAsync(
            destination,
            DiagnosticExportContext.Empty,
            CancellationToken.None));
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        exporter.Dispose();
        sink.Release.TrySetResult();

        await export.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(File.Exists(destination));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => exporter.ExportAsync(
            Path.Combine(_directory, "after-dispose.zip"),
            DiagnosticExportContext.Empty,
            CancellationToken.None));
    }

    [Fact]
    public async Task LimitsEventsAndFieldLengths()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "limited.zip");
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor, 50, 1_000);
        for (var index = 0; index < 20; index++)
        {
            sink.Write(new SafeDiagnosticEventInput(
                "EVENT",
                index.ToString("x32", CultureInfo.InvariantCulture),
                new string('x', 500)));
        }

        var exporter = new DiagnosticExporter(
            sink,
            redactor,
            new DiagnosticExportLimits(MaxEvents: 3, MaxFieldLength: 40, MaxArchiveBytes: 128 * 1024));
        await exporter.ExportAsync(destination, DiagnosticExportContext.Empty, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        var diagnostics = archive.GetEntry("diagnostics.json")!;
        using var reader = new StreamReader(diagnostics.Open(), Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        Assert.DoesNotContain(16.ToString("x32", CultureInfo.InvariantCulture), json, StringComparison.Ordinal);
        Assert.Contains(19.ToString("x32", CultureInfo.InvariantCulture), json, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 41), json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionAggregateCountersAndEncodingStatisticsRoundTripAsLongs()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "session-aggregates.zip");
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new InMemorySafeDiagnosticSink(redactor), redactor);
        var context = new DiagnosticExportContext(
            DiagnosticExportContext.Empty.Application,
            [
                new DiagnosticProfileSummary(
                    "Remote session",
                    "private-host.internal",
                    5900,
                    string.Empty,
                    "RFB 3.x",
                    "ARD-30",
                    new Dictionary<string, long> { ["ZRLE"] = 18 }),
            ],
            new Dictionary<string, long>
            {
                ["Session.TargetFps"] = 90,
                ["Session.ActualFps"] = 64,
                ["Session.ReceiveBytesPerSecond"] = 1_234_567,
                ["Session.PointerMovesCoalesced"] = 42,
            },
            IncludeHosts: false);

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var root = document.RootElement;
        Assert.Equal(90, root.GetProperty("performanceCounters")
            .GetProperty("Session.TargetFps").GetInt64());
        Assert.Equal(64, root.GetProperty("performanceCounters")
            .GetProperty("Session.ActualFps").GetInt64());
        Assert.Equal(1_234_567, root.GetProperty("performanceCounters")
            .GetProperty("Session.ReceiveBytesPerSecond").GetInt64());
        Assert.Equal(42, root.GetProperty("performanceCounters")
            .GetProperty("Session.PointerMovesCoalesced").GetInt64());
        Assert.Equal(18, root.GetProperty("profiles")[0]
            .GetProperty("encodingStatistics").GetProperty("ZRLE").GetInt64());
        Assert.DoesNotContain("private-host.internal", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdaptiveQualityUsesOnlyTheFixedAllowlistedSchema()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "quality-allowlist.zip");
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new InMemorySafeDiagnosticSink(redactor), redactor);
        var quality = new DiagnosticQualitySummary(
            Preset: "Automatic",
            TargetBytesPerSecond: 8_000_000,
            QualityLevel: "Q3",
            ContentState: "Motion",
            Color: "Color16",
            ScalePercent: 50,
            EncodingName: "Zlib",
            TargetFramesPerSecond: 45,
            ActualFramesPerSecond: 38,
            AverageBytesPerSecond: 9_000_000,
            PeakBytesPerSecond: 12_000_000,
            ResponseMilliseconds: 86,
            ZlibCapability: "Observed",
            Rgb565Capability: "Observed",
            ServerScalingCapability: "Observed",
            AppleColor1002Capability: "Unknown",
            AppleGrayscale1001Capability: "Unsupported",
            SafeOnlinePixelFormatSwitch: true,
            SafeOnlineScaleSwitch: false,
            Reason: "SevereOverTarget",
            TargetSatisfied: false);
        var context = DiagnosticExportContext.Empty with { Quality = quality };

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var exported = document.RootElement.GetProperty("quality");
        string[] expectedNames =
        [
            "actualFps", "appleColor1002Capability", "appleGrayscale1001Capability",
            "averageBps", "color", "contentState", "encodingName", "peakBps", "preset",
            "qualityLevel", "reason", "responseMs", "rgb565Capability", "safeOnlineColorSwitch",
            "safeOnlineScaleSwitch", "scalePercent",
            "serverScalingCapability", "targetBps", "targetFps", "targetSatisfied",
            "zlibCapability",
        ];
        Assert.Equal(expectedNames, exported.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("Motion", exported.GetProperty("contentState").GetString());
        Assert.Equal("SevereOverTarget", exported.GetProperty("reason").GetString());
        Assert.False(exported.GetProperty("targetSatisfied").GetBoolean());
        string[] forbiddenNameTokens =
        [
            "host", "username", "password", "coordinate", "mouse", "key", "text", "clipboard",
            "pixel", "payload", "ciphertext", "sequence", "path",
        ];
        Assert.DoesNotContain(
            exported.EnumerateObject().Select(property => property.Name),
            name => forbiddenNameTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task TransferEfficiencyExportsOnlyAggregateClosedSchemaValues()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "transfer-allowlist.zip");
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new InMemorySafeDiagnosticSink(redactor), redactor);
        var context = DiagnosticExportContext.Empty with
        {
            Transfer = new DiagnosticTransferSummary(
                "Rgb565", "Bgra32", "Fallback", "DecoderFailure", "ZrleFirst",
                3, 2_000, 800, 400, 200, "Color16", "Full32",
                new Dictionary<string, long>
                {
                    ["Zlib"] = 700,
                    ["CopyRect"] = 100,
                    ["Other"] = 9,
                }),
        };

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var transfer = document.RootElement.GetProperty("transfer");
        Assert.Equal("Rgb565", transfer.GetProperty("PreferredPixelFormat").GetString());
        Assert.Equal("Bgra32", transfer.GetProperty("AppliedPixelFormat").GetString());
        Assert.Equal("Fallback", transfer.GetProperty("BootstrapAttempt").GetString());
        Assert.Equal("DecoderFailure", transfer.GetProperty("BootstrapFallbackReason").GetString());
        Assert.Equal("ZrleFirst", transfer.GetProperty("PreferredEncodingOrder").GetString());
        Assert.Equal(3, transfer.GetProperty("RectangleCount").GetInt64());
        Assert.Equal(2_000, transfer.GetProperty("PixelArea").GetInt64());
        Assert.Equal(800, transfer.GetProperty("WirePayloadBytes").GetInt64());
        Assert.Equal(400, transfer.GetProperty("BytesPerPixelMilli").GetInt64());
        Assert.Equal(200, transfer.GetProperty("DirtyCoveragePermille").GetInt32());
        Assert.Equal(700, transfer.GetProperty("EncodingZlibWireBytes").GetInt64());
        Assert.Equal(9, transfer.GetProperty("EncodingOtherWireBytes").GetInt64());
        Assert.DoesNotContain(transfer.EnumerateObject(), property =>
            property.Name.Contains("Coordinates", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Rectangles", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BootstrapAndReconnectEvidenceUsesBoundedClosedSchemaValues()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "bootstrap-reconnect.zip");
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new InMemorySafeDiagnosticSink(redactor), redactor);
        var context = DiagnosticExportContext.Empty with
        {
            Transfer = new DiagnosticTransferSummary(
                "Rgb565", "Bgra32", "Fallback", "FramebufferSizeMismatch", "ZlibFirst",
                3, 2_000, 800, 400, 200, "Color16", "Full32",
                new Dictionary<string, long>())
            {
                DesiredScalePercent = 25,
                ResolvedScalePercent = 25,
                AppliedScalePercent = 100,
                FirstFrameWidth = 1680,
                FirstFrameHeight = 1050,
                FirstFrameRectangleCount = 3,
            },
            Reconnect = new DiagnosticReconnectSummary("Waiting", 4, 12),
        };

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var root = document.RootElement;
        var transfer = root.GetProperty("transfer");
        Assert.Equal(25, transfer.GetProperty("DesiredScalePercent").GetInt32());
        Assert.Equal(25, transfer.GetProperty("ResolvedScalePercent").GetInt32());
        Assert.Equal(100, transfer.GetProperty("AppliedScalePercent").GetInt32());
        Assert.Equal(1680, transfer.GetProperty("FirstFrameWidth").GetInt32());
        Assert.Equal(1050, transfer.GetProperty("FirstFrameHeight").GetInt32());
        Assert.Equal(3, transfer.GetProperty("FirstFrameRectangleCount").GetInt32());
        Assert.Equal("FramebufferSizeMismatch", transfer.GetProperty("BootstrapFallbackReason").GetString());
        var reconnect = root.GetProperty("reconnect");
        Assert.Equal("Waiting", reconnect.GetProperty("State").GetString());
        Assert.Equal(4, reconnect.GetProperty("Attempt").GetInt32());
        Assert.Equal(12, reconnect.GetProperty("DelaySeconds").GetInt32());
    }

    [Fact]
    public async Task MigrationEvidenceExportsOnlyStableResultAndBoundedCounts()
    {
        var diagnosticEvent = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            "CREDENTIAL_BACKEND_MIGRATION",
            Guid.NewGuid().ToString("N"),
            "ignored",
            [
                new("result", "SucceededWithCleanupFailures", DiagnosticFieldCategory.Public),
                new("managed_count", "4", DiagnosticFieldCategory.Public),
                new("migrated_count", "4", DiagnosticFieldCategory.Public),
                new("compensation_failure_count", "0", DiagnosticFieldCategory.Public),
                new("cleanup_failure_count", "1", DiagnosticFieldCategory.Public),
            ],
            null);

        using var document = await ExportSingleEventAsync(
            diagnosticEvent, false, "migration-evidence.zip");

        var fields = document.RootElement.GetProperty("events")[0].GetProperty("fields");
        Assert.Equal("SucceededWithCleanupFailures", fields.GetProperty("MigrationResult").GetString());
        Assert.Equal("4", fields.GetProperty("MigrationManagedCount").GetString());
        Assert.Equal("4", fields.GetProperty("MigrationMigratedCount").GetString());
        Assert.Equal("0", fields.GetProperty("MigrationCompensationFailureCount").GetString());
        Assert.Equal("1", fields.GetProperty("MigrationCleanupFailureCount").GetString());
    }

    [Fact]
    public async Task ExportOmitsExpiredEventsAndAllInjectedSensitiveMaterial()
    {
        string[] markers =
        [
            "host-sensitive-marker", "user-sensitive-marker", @"C:\Users\private\sensitive-marker.key",
            "password-sensitive-marker", "clipboard-sensitive-marker", "raw-exception-sensitive-marker",
            "credential-key-sensitive-marker", "remote-pixel-sensitive-marker",
        ];
        var diagnosticEvent = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            "CURRENT_EVENT",
            Guid.NewGuid().ToString("N"),
            string.Join('|', markers),
            [
                new("endpoint", markers[0], DiagnosticFieldCategory.Host),
                new("Username", markers[1], DiagnosticFieldCategory.Public),
                new("Path", markers[2], DiagnosticFieldCategory.Path),
                new("Password", markers[3], DiagnosticFieldCategory.Password),
                new("Clipboard", markers[4], DiagnosticFieldCategory.ClipboardContent),
                new("CredentialKey", markers[6], DiagnosticFieldCategory.Credential),
                new("RemoteFrameBytes", markers[7], DiagnosticFieldCategory.Public),
            ],
            new("InvalidOperationException", "0x80131509"));
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "strict-private.zip");
        using var redactor = new SecretRedactor();
        var expiredEvent = diagnosticEvent with
        {
            Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromDays(2),
            Code = "EXPIRED_EVENT",
        };
        using var exporter = new DiagnosticExporter(
            new StaticSnapshotSink([expiredEvent, diagnosticEvent]),
            redactor,
            new DiagnosticExportLimits(MaxEventAge: TimeSpan.FromDays(1)));
        var context = DiagnosticExportContext.Empty with
        {
            IncludeHosts = true,
            Profiles = [new("Remote session", markers[0], 5900, markers[1], "RFB 3.x", "ARD-30")],
            Transfer = new DiagnosticTransferSummary(
                markers[6], markers[6], markers[6], markers[6], markers[6],
                0, 0, 0, null, 0, markers[6], markers[6],
                new Dictionary<string, long> { [markers[6]] = 1 }),
            Reconnect = new DiagnosticReconnectSummary(markers[5], int.MaxValue, int.MaxValue),
        };

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        var content = await ReadAllAsync(archive);
        foreach (var marker in markers)
        {
            Assert.DoesNotContain(marker, content, StringComparison.OrdinalIgnoreCase);
        }
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        Assert.Single(document.RootElement.GetProperty("events").EnumerateArray());
        var reconnect = document.RootElement.GetProperty("reconnect");
        Assert.Equal(JsonValueKind.Null, reconnect.GetProperty("State").ValueKind);
        Assert.Equal(JsonValueKind.Null, reconnect.GetProperty("Attempt").ValueKind);
        Assert.Equal(JsonValueKind.Null, reconnect.GetProperty("DelaySeconds").ValueKind);
    }

    [Fact]
    public async Task TransferEfficiencyDropsEveryInvalidValueAndLeaksNoArbitraryMarkers()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "transfer-private.zip");
        string[] markers =
        [
            "host-transfer-marker", "username-transfer-marker", "coordinate-transfer-marker",
            "payload-transfer-marker", "pixel-transfer-marker", "exception-transfer-marker",
            "encoding-314159-transfer-marker",
        ];
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        sink.Write(new SafeDiagnosticEventInput(
            "EVENT", Guid.NewGuid().ToString("N"), string.Join('|', markers), [],
            new InvalidOperationException(markers[5])));
        using var exporter = new DiagnosticExporter(sink, redactor);
        var context = DiagnosticExportContext.Empty with
        {
            Profiles =
            [
                new DiagnosticProfileSummary(
                    "Remote session", markers[0], 5900, markers[1], "RFB 3.x", "ARD-30"),
            ],
            Transfer = new DiagnosticTransferSummary(
                markers[4], markers[4], markers[0], markers[5], markers[6],
                -1, -2, -3, -4, 1001, markers[1], markers[2],
                new Dictionary<string, long>
                {
                    [markers[6]] = 314_159,
                    ["Zlib"] = -7,
                }),
        };

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        var allEntries = await ReadAllAsync(archive);
        foreach (var marker in markers)
        {
            Assert.DoesNotContain(marker, allEntries, StringComparison.OrdinalIgnoreCase);
        }

        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var transfer = document.RootElement.GetProperty("transfer");
        foreach (var property in transfer.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.Null, property.Value.ValueKind);
        }
    }

    [Fact]
    public async Task AdaptiveQualityRejectsUnlistedStringValuesAndLeaksNoSensitiveMarkers()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "quality-privacy.zip");
        string[] forbiddenMarkers =
        [
            "host-private-marker", "username-private-marker", "password-private-marker",
            "mouse-coordinate-marker", "pressed-key-marker", "typed-text-marker",
            "clipboard-private-marker", "pixel-private-marker", "payload-private-marker",
            "ciphertext-private-marker", "crypto-key-marker", "iv-private-marker",
            "sequence-private-marker", @"C:\Users\private\quality-payload.bin",
            "arbitrary-reason-private-marker", "encoding-314159-private-marker",
        ];
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        sink.Write(new SafeDiagnosticEventInput(
            "EVENT",
            Guid.NewGuid().ToString("N"),
            string.Join('|', forbiddenMarkers),
            forbiddenMarkers.Select(marker =>
                new DiagnosticField("Unlisted" + marker.Length, marker)).ToArray()));
        using var exporter = new DiagnosticExporter(sink, redactor);
        var quality = new DiagnosticQualitySummary(
            "arbitrary-preset-private-marker", null,
            "arbitrary-level-private-marker", "arbitrary-state-private-marker",
            "arbitrary-color-private-marker", 50,
            forbiddenMarkers[^1], 45, 38, 9_000_000, 12_000_000, 86,
            "arbitrary-zlib-capability-private-marker", "arbitrary-rgb-capability-private-marker",
            "arbitrary-scale-capability-private-marker", "arbitrary-1002-capability-private-marker",
            "arbitrary-1001-capability-private-marker",
            true, false, forbiddenMarkers[^2], false);
        var context = DiagnosticExportContext.Empty with
        {
            Quality = quality,
            Profiles =
            [
                new DiagnosticProfileSummary(
                    "Remote session", forbiddenMarkers[0], 5900, forbiddenMarkers[1],
                    "RFB 3.x", "ARD-30",
                    new Dictionary<string, long> { ["Encoding.314159"] = 1 }),
            ],
        };

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        var content = await ReadAllAsync(archive);
        foreach (var marker in forbiddenMarkers)
        {
            Assert.DoesNotContain(marker, content, StringComparison.OrdinalIgnoreCase);
        }

        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var exported = document.RootElement.GetProperty("quality");
        Assert.Equal("Other", exported.GetProperty("encodingName").GetString());
        Assert.Equal(JsonValueKind.Null, exported.GetProperty("reason").ValueKind);
        foreach (var propertyName in new[]
        {
            "preset", "qualityLevel", "contentState", "color", "zlibCapability",
            "rgb565Capability", "serverScalingCapability", "appleColor1002Capability",
            "appleGrayscale1001Capability",
        })
        {
            Assert.Equal(JsonValueKind.Null, exported.GetProperty(propertyName).ValueKind);
        }
        Assert.False(document.RootElement.GetProperty("profiles")[0]
            .GetProperty("encodingStatistics").TryGetProperty("Encoding.314159", out _));
    }

    [Fact]
    public async Task ExportOmitsForbiddenProtocolAndSensitiveEventFieldsAtTheBoundary()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "forbidden-event-fields.zip");
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        sink.Write(new SafeDiagnosticEventInput(
            "ARD_PACKET_REJECTED",
            "corr-safe",
            "ARD packet rejected.",
            [
                new("ArdEncryptionSequence", "sequence-value-marker"),
                new("ArdCiphertextLength", "48"),
                new("Pointer_X", "pointer-value-marker"),
                new("PixelFormat", "pixel-value-marker"),
                new("KeyContent", "key-value-marker"),
                new("Clipboard_Content", "clipboard-value-marker"),
                new("PressedKey", "pressed-key-marker"),
                new("Character", "character-marker"),
                new("PastedText", "pasted-text-marker"),
                new("ButtonOrder", "button-order-marker"),
                new("BenignName", "benign-sensitive-marker"),
                new("PayloadSize", "4096"),
                new("Category", "known-key-sequence-marker"),
                new("ProtocolFailureKind", "ArdEncryptionPacket"),
                new("ArdEncryptionStage", "Padding"),
            ]));
        using var exporter = new DiagnosticExporter(sink, redactor);

        await exporter.ExportAsync(destination, DiagnosticExportContext.Empty, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var root = document.RootElement;
        var diagnosticEvent = root.GetProperty("events")[0];
        Assert.Equal("ARD_PACKET_REJECTED", diagnosticEvent.GetProperty("code").GetString());
        Assert.Equal("ArdEncryptionPacket", diagnosticEvent.GetProperty("fields")
            .GetProperty("ProtocolFailureKind").GetString());
        Assert.Equal("Padding", diagnosticEvent.GetProperty("fields")
            .GetProperty("ArdEncryptionStage").GetString());
        Assert.Equal("48", diagnosticEvent.GetProperty("fields")
            .GetProperty("ArdEncryptedPacketLength").GetString());
        var json = root.GetRawText();
        foreach (var forbidden in new[]
        {
            "Coordinate", "PointerX", "Pointer_X", "PointerY", "Keysym", "Pixel", "Ciphertext",
            "Sequence", "sequence-value-marker", "pointer-value-marker", "pixel-value-marker",
            "key-value-marker", "clipboard-value-marker", "Clipboard_Content", "pressed-key-marker",
            "character-marker", "pasted-text-marker", "button-order-marker", "benign-sensitive-marker",
            "PayloadSize", "known-key-sequence-marker",
        })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task FilteringPrecedesThePerEventFieldLimit()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "filter-before-limit.zip");
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        sink.Write(new SafeDiagnosticEventInput(
            "ARD_PACKET_REJECTED",
            Guid.NewGuid().ToString("N"),
            "stable",
            [
                new("PressedKey", "secret-one"),
                new("PastedText", "secret-two"),
                new("ProtocolFailureKind", "ArdEncryptionPacket"),
                new("ArdEncryptionStage", "Padding"),
            ]));
        using var exporter = new DiagnosticExporter(
            sink,
            redactor,
            new DiagnosticExportLimits(MaxFieldsPerEvent: 2));

        await exporter.ExportAsync(destination, DiagnosticExportContext.Empty, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var fields = document.RootElement.GetProperty("events")[0].GetProperty("fields");
        Assert.Equal(2, fields.EnumerateObject().Count());
        Assert.Equal("ArdEncryptionPacket", fields.GetProperty("ProtocolFailureKind").GetString());
        Assert.Equal("Padding", fields.GetProperty("ArdEncryptionStage").GetString());
    }

    [Fact]
    public async Task UnknownDictionaryKeysAndUntrustedEventIdentityTextAreNotExported()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "untrusted-schema-values.zip");
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        sink.Write(new SafeDiagnosticEventInput(
            "code-sensitive-marker",
            "correlation-sensitive-marker",
            "message-sensitive-marker",
            [new("BenignName", "field-sensitive-marker")]));
        var context = new DiagnosticExportContext(
            DiagnosticExportContext.Empty.Application,
            [
                new DiagnosticProfileSummary(
                    "Remote session",
                    string.Empty,
                    5900,
                    string.Empty,
                    "RFB 3.x",
                    "ARD-30",
                    new Dictionary<string, long>
                    {
                        ["ZRLE"] = 18,
                        ["CiphertextStats"] = 99,
                        ["benign-sensitive-marker"] = 7,
                    },
                    ErrorCode: "profile-code-sensitive-marker",
                    CorrelationId: "profile-correlation-sensitive-marker"),
            ],
            new Dictionary<string, long>
            {
                ["Session.ActualFps"] = 64,
                ["Session.SequenceMarker"] = 9,
                ["benign-sensitive-marker"] = 7,
            },
            IncludeHosts: false);
        using var exporter = new DiagnosticExporter(sink, redactor);

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var root = document.RootElement;
        Assert.Equal(64, root.GetProperty("performanceCounters").GetProperty("Session.ActualFps").GetInt64());
        Assert.Equal(18, root.GetProperty("profiles")[0].GetProperty("encodingStatistics")
            .GetProperty("ZRLE").GetInt64());
        var json = root.GetRawText();
        foreach (var marker in new[]
        {
            "code-sensitive-marker", "correlation-sensitive-marker", "message-sensitive-marker",
            "field-sensitive-marker", "benign-sensitive-marker", "Ciphertext", "Sequence",
            "profile-code-sensitive-marker", "profile-correlation-sensitive-marker",
        })
        {
            Assert.DoesNotContain(marker, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ApplicationProfileAndEventStringChannelsUseExplicitSafeSchemas()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "explicit-string-schemas.zip");
        string[] markers =
        [
            "AlphaApplicationMarker", "BravoVersionMarker", "CharlieOperatingSystemMarker",
            "DeltaRuntimeMarker", "EchoWindowsSdkMarker", "FoxtrotDisplayMarker",
            "GolfHostMarker", "HotelUsernameMarker", "IndiaProtocolMarker",
            "JulietSecurityMarker", "KiloErrorMarker", "LimaCorrelationMarker",
            "MikeCodeMarker", "NovemberEventCorrelationMarker", "OscarMessageMarker",
            "PapaCategoryMarker", "QuebecMarkerException", "RomeoHResultMarker",
        ];
        var diagnosticEvent = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            markers[12],
            markers[13],
            markers[14],
            [new SafeDiagnosticField("Category", markers[15], DiagnosticFieldCategory.Public)],
            new SafeDiagnosticFailureMetadata(markers[16], markers[17]));
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new StaticSnapshotSink([diagnosticEvent]), redactor);
        var context = new DiagnosticExportContext(
            new DiagnosticApplicationInfo(markers[0], markers[1], markers[2], markers[3], markers[4]),
            [
                new DiagnosticProfileSummary(
                    markers[5], markers[6], 5900, markers[7], markers[8], markers[9],
                    ErrorCode: markers[10], CorrelationId: markers[11]),
                new DiagnosticProfileSummary(
                    "Safe display name is still private", string.Empty, 5900, "SafeUsername",
                    "RFB 3.x", "ARD-30"),
            ],
            new Dictionary<string, long>(),
            IncludeHosts: true);

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var root = document.RootElement;
        var json = root.GetRawText();
        foreach (var marker in markers)
        {
            Assert.DoesNotContain(marker, json, StringComparison.OrdinalIgnoreCase);
        }

        var application = root.GetProperty("application");
        Assert.Equal("WinARD", application.GetProperty("name").GetString());
        Assert.Equal("unknown", application.GetProperty("version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(application.GetProperty("operatingSystem").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(application.GetProperty("dotNet").GetString()));
        Assert.Equal("unknown", application.GetProperty("windowsAppSdk").GetString());
        var unsafeProfile = root.GetProperty("profiles")[0];
        Assert.Equal("Remote session", unsafeProfile.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, unsafeProfile.GetProperty("username").ValueKind);
        Assert.Equal("Unknown", unsafeProfile.GetProperty("protocolVersion").GetString());
        Assert.Equal("Unknown", unsafeProfile.GetProperty("securityType").GetString());
        var safeProfile = root.GetProperty("profiles")[1];
        Assert.Equal("RFB 3.x", safeProfile.GetProperty("protocolVersion").GetString());
        Assert.Equal("ARD-30", safeProfile.GetProperty("securityType").GetString());
        var exportedEvent = root.GetProperty("events")[0];
        Assert.Equal("DIAGNOSTIC_EVENT_OMITTED", exportedEvent.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, exportedEvent.GetProperty("correlationId").ValueKind);
        Assert.Equal("Diagnostic event.", exportedEvent.GetProperty("message").GetString());
        Assert.False(exportedEvent.GetProperty("fields").TryGetProperty("Category", out _));
        Assert.Equal(JsonValueKind.Null, exportedEvent.GetProperty("exception").GetProperty("type").ValueKind);
        Assert.Equal(JsonValueKind.Null, exportedEvent.GetProperty("exception").GetProperty("hResult").ValueKind);
    }

    [Fact]
    public async Task EveryAllowedEventFieldRejectsOrNormalizesAUniqueUnregisteredValue()
    {
        string[] fieldNames =
        [
            "stage", "action", "Kind", "Boundary", "Count", "Reason", "Encrypted", "Sampled",
            "Category", "ProtocolVersion", "ClientInit", "ServerFlags", "MayControl",
            "SessionSelectRequired", "SessionSelectCompleted", "RequestedMode", "FinalState",
            "Status", "Flags", "Action", "ProtocolFailureKind", "RfbHandshakeStage",
            "ExpectedByteCount", "ActualByteCount", "PresentationStage", "ProtocolReadStage",
            "ServerMessageType", "EncodingName", "RectangleIndex", "ArdEncryptionStage",
            "ArdEncryptionDirection", "securityType", "endpoint", "fingerprint", "oldFingerprint",
            "newFingerprint", "ArdCiphertextLength",
            "BootstrapAttempt", "BootstrapFallbackReason", "FallbackFailed", "PreferredFailureReason",
        ];
        var markers = fieldNames
            .Select((_, index) => $"ValueMarker{index:D2}")
            .ToArray();
        var fields = fieldNames.Select((name, index) => new SafeDiagnosticField(
            name,
            name == "endpoint" ? $"{markers[index]} invalid host" : markers[index],
            name == "endpoint" ? DiagnosticFieldCategory.Host : DiagnosticFieldCategory.Public))
            .ToArray();
        var diagnosticEvent = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            "EVENT_FIELD_SCHEMA_TEST",
            Guid.NewGuid().ToString("N"),
            "ignored",
            fields,
            null);

        using var document = await ExportSingleEventAsync(
            diagnosticEvent,
            includeHosts: true,
            "field-marker-schema.zip");

        var json = document.RootElement.GetRawText();
        foreach (var marker in markers)
        {
            Assert.DoesNotContain(marker, json, StringComparison.Ordinal);
        }

        var exported = document.RootElement.GetProperty("events")[0].GetProperty("fields");
        var encodingName = Assert.Single(exported.EnumerateObject());
        Assert.Equal("EncodingName", encodingName.Name);
        Assert.Equal("Other", encodingName.Value.GetString());
    }

    [Fact]
    public async Task EveryEventFieldSchemaRetainsRepresentativeValidValues()
    {
        var fingerprint = $"SHA256:{new string('A', 43)}";
        SafeDiagnosticField[] fields =
        [
            new("stage", "Resolving", DiagnosticFieldCategory.Public),
            new("action", "Retry", DiagnosticFieldCategory.Public),
            new("Kind", "Keyboard", DiagnosticFieldCategory.Public),
            new("Boundary", "UiCaptured", DiagnosticFieldCategory.Public),
            new("Count", "42", DiagnosticFieldCategory.Public),
            new("Reason", "SessionClosing", DiagnosticFieldCategory.Public),
            new("Encrypted", "True", DiagnosticFieldCategory.Public),
            new("Sampled", "False", DiagnosticFieldCategory.Public),
            new("Category", "InvalidRefreshRateRange", DiagnosticFieldCategory.Public),
            new("ProtocolVersion", "003.008", DiagnosticFieldCategory.Public),
            new("ClientInit", "0xC1", DiagnosticFieldCategory.Public),
            new("ServerFlags", "0x00000001", DiagnosticFieldCategory.Public),
            new("MayControl", "NotApplicable", DiagnosticFieldCategory.Public),
            new("SessionSelectRequired", "False", DiagnosticFieldCategory.Public),
            new("SessionSelectCompleted", "True", DiagnosticFieldCategory.Public),
            new("RequestedMode", "Shared", DiagnosticFieldCategory.Public),
            new("FinalState", "SharedControlNegotiated", DiagnosticFieldCategory.Public),
            new("Status", "65535", DiagnosticFieldCategory.Public),
            new("Flags", "0x00AF", DiagnosticFieldCategory.Public),
            new("ProtocolFailureKind", "ArdEncryptionPacket", DiagnosticFieldCategory.Public),
            new("DecoderFailureReason", "InvalidCompressedStream", DiagnosticFieldCategory.Public),
            new("RfbHandshakeStage", "SecurityTypes", DiagnosticFieldCategory.Public),
            new("ExpectedByteCount", "1024", DiagnosticFieldCategory.Public),
            new("ActualByteCount", "512", DiagnosticFieldCategory.Public),
            new("PresentationStage", "Present1", DiagnosticFieldCategory.Public),
            new("ProtocolReadStage", "FramebufferRectanglePayload", DiagnosticFieldCategory.Public),
            new("ServerMessageType", "0xFA", DiagnosticFieldCategory.Public),
            new("EncodingName", "DesktopSize", DiagnosticFieldCategory.Public),
            new("RectangleIndex", "7", DiagnosticFieldCategory.Public),
            new("ArdEncryptionStage", "Integrity", DiagnosticFieldCategory.Public),
            new("ArdEncryptionDirection", "Receive", DiagnosticFieldCategory.Public),
            new("securityType", "30", DiagnosticFieldCategory.Public),
            new("endpoint", "safe.example", DiagnosticFieldCategory.Host),
            new("fingerprint", fingerprint, DiagnosticFieldCategory.Public),
            new("oldFingerprint", fingerprint, DiagnosticFieldCategory.Public),
            new("newFingerprint", fingerprint, DiagnosticFieldCategory.Public),
            new("ArdCiphertextLength", "48", DiagnosticFieldCategory.Public),
            new("BootstrapAttempt", "Fallback", DiagnosticFieldCategory.Public),
            new("BootstrapFallbackReason", "DecoderFailure", DiagnosticFieldCategory.Public),
            new("FallbackFailed", "True", DiagnosticFieldCategory.Public),
            new("PreferredFailureReason", "UnsupportedEncoding", DiagnosticFieldCategory.Public),
        ];
        var diagnosticEvent = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            "EVENT_FIELD_SCHEMA_TEST",
            Guid.NewGuid().ToString("N"),
            "ignored",
            fields,
            null);

        using var document = await ExportSingleEventAsync(
            diagnosticEvent,
            includeHosts: true,
            "field-valid-schema.zip");

        var exported = document.RootElement.GetProperty("events")[0].GetProperty("fields");
        Assert.Equal("Resolving", exported.GetProperty("Stage").GetString());
        Assert.Equal("Retry", exported.GetProperty("Action").GetString());
        Assert.Equal("42", exported.GetProperty("Count").GetString());
        Assert.Equal("003.008", exported.GetProperty("ProtocolVersion").GetString());
        Assert.Equal("0x00000001", exported.GetProperty("ServerFlags").GetString());
        Assert.Equal("True", exported.GetProperty("SessionSelectCompleted").GetString());
        Assert.Equal("65535", exported.GetProperty("Status").GetString());
        Assert.Equal("0x00AF", exported.GetProperty("Flags").GetString());
        Assert.Equal("DesktopSize", exported.GetProperty("EncodingName").GetString());
        Assert.Equal("InvalidCompressedStream", exported.GetProperty("DecoderFailureReason").GetString());
        Assert.False(exported.TryGetProperty("endpoint", out _));
        Assert.Equal(fingerprint, exported.GetProperty("fingerprint").GetString());
        Assert.Equal("48", exported.GetProperty("ArdEncryptedPacketLength").GetString());
        Assert.Equal("Fallback", exported.GetProperty("BootstrapAttempt").GetString());
        Assert.Equal("DecoderFailure", exported.GetProperty("BootstrapFallbackReason").GetString());
        Assert.Equal("True", exported.GetProperty("FallbackFailed").GetString());
        Assert.Equal("UnsupportedEncoding", exported.GetProperty("PreferredFailureReason").GetString());
    }

    [Fact]
    public async Task BootstrapFieldsExportOnlyClosedValuesAndDropRawMarkers()
    {
        const string marker = "raw-bootstrap-host-payload-marker";
        var diagnosticEvent = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            "QUALITY_BOOTSTRAP_FALLBACK",
            Guid.NewGuid().ToString("N"),
            marker,
            [
                new("BootstrapAttempt", "Preferred", DiagnosticFieldCategory.Public),
                new("BootstrapFallbackReason", "RemoteSessionClosed", DiagnosticFieldCategory.Public),
                new("FallbackFailed", "False", DiagnosticFieldCategory.Public),
                new("PreferredFailureReason", "MalformedFramebufferUpdate", DiagnosticFieldCategory.Public),
                new("BootstrapAttemptRaw", marker, DiagnosticFieldCategory.Public),
                new("BootstrapFallbackReasonRaw", marker, DiagnosticFieldCategory.Public),
            ],
            null);

        using var document = await ExportSingleEventAsync(
            diagnosticEvent,
            includeHosts: false,
            "bootstrap-field-schema.zip");

        var json = document.RootElement.GetRawText();
        Assert.DoesNotContain(marker, json, StringComparison.Ordinal);
        var fields = document.RootElement.GetProperty("events")[0].GetProperty("fields");
        Assert.Equal("Preferred", fields.GetProperty("BootstrapAttempt").GetString());
        Assert.Equal("RemoteSessionClosed", fields.GetProperty("BootstrapFallbackReason").GetString());
        Assert.Equal("False", fields.GetProperty("FallbackFailed").GetString());
        Assert.Equal("MalformedFramebufferUpdate", fields.GetProperty("PreferredFailureReason").GetString());
    }

    [Fact]
    public async Task InvalidBootstrapFieldValuesAreOmittedFromExport()
    {
        const string marker = "raw-invalid-bootstrap-value-marker";
        var diagnosticEvent = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            "QUALITY_BOOTSTRAP_FALLBACK",
            Guid.NewGuid().ToString("N"),
            "ignored",
            [
                new("BootstrapAttempt", marker, DiagnosticFieldCategory.Public),
                new("BootstrapFallbackReason", marker, DiagnosticFieldCategory.Public),
                new("FallbackFailed", marker, DiagnosticFieldCategory.Public),
                new("PreferredFailureReason", marker, DiagnosticFieldCategory.Public),
            ],
            null);

        using var document = await ExportSingleEventAsync(
            diagnosticEvent,
            includeHosts: false,
            "bootstrap-invalid-values.zip");

        var fields = document.RootElement.GetProperty("events")[0].GetProperty("fields");
        Assert.Empty(fields.EnumerateObject());
        Assert.DoesNotContain(marker, document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawEncodingIdentifiersAreNeverExportedAndUnknownNamesBecomeOther()
    {
        Directory.CreateDirectory(_directory);
        const string rawEncodingIdMarker = "-2147483123";
        const string unknownEncodingNameMarker = "raw-encoding-name-marker";
        var diagnosticEvent = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            "EVENT_ENCODING_SCHEMA_TEST",
            Guid.NewGuid().ToString("N"),
            "ignored",
            [
                new("EncodingId", rawEncodingIdMarker, DiagnosticFieldCategory.Public),
                new("EncodingName", unknownEncodingNameMarker, DiagnosticFieldCategory.Public),
            ],
            null);
        var destination = Path.Combine(_directory, "encoding-schema.zip");
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new StaticSnapshotSink([diagnosticEvent]), redactor);

        await exporter.ExportAsync(destination, DiagnosticExportContext.Empty, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        var content = await ReadAllAsync(archive);
        Assert.DoesNotContain(rawEncodingIdMarker, content, StringComparison.Ordinal);
        Assert.DoesNotContain(unknownEncodingNameMarker, content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"EncodingId\"", content, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var fields = document.RootElement.GetProperty("events")[0].GetProperty("fields");
        Assert.Equal("Other", fields.GetProperty("EncodingName").GetString());
    }

    [Fact]
    public async Task ArbitraryDecoderFailureReasonIsOmittedFromExport()
    {
        const string marker = "arbitrary-decoder-private-marker";
        var diagnosticEvent = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            "EVENT_DECODER_REASON_SCHEMA_TEST",
            Guid.NewGuid().ToString("N"),
            "ignored",
            [new("DecoderFailureReason", marker, DiagnosticFieldCategory.Public)],
            null);

        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "decoder-reason-schema.zip");
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new StaticSnapshotSink([diagnosticEvent]), redactor);
        await exporter.ExportAsync(destination, DiagnosticExportContext.Empty, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        var archiveContent = await ReadAllAsync(archive);
        Assert.DoesNotContain(marker, archiveContent, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var fields = document.RootElement.GetProperty("events")[0].GetProperty("fields");
        Assert.False(fields.TryGetProperty("DecoderFailureReason", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OmittedPathAndHostFieldsDoNotConsumeTheFinalFieldLimit(bool includeHosts)
    {
        var diagnosticEvent = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            "EVENT_FIELD_LIMIT_TEST",
            Guid.NewGuid().ToString("N"),
            "ignored",
            [
                new SafeDiagnosticField("privateKeyPath", @"C:\private\key", DiagnosticFieldCategory.Path),
                new SafeDiagnosticField("endpoint", "safe.example", DiagnosticFieldCategory.Host),
                new SafeDiagnosticField("Count", "1", DiagnosticFieldCategory.Public),
                new SafeDiagnosticField("Encrypted", "True", DiagnosticFieldCategory.Public),
            ],
            null);

        using var document = await ExportSingleEventAsync(
            diagnosticEvent,
            includeHosts,
            $"final-field-limit-{includeHosts}.zip",
            new DiagnosticExportLimits(MaxFieldsPerEvent: 2));

        var exported = document.RootElement.GetProperty("events")[0].GetProperty("fields");
        Assert.Equal(2, exported.EnumerateObject().Count());
        Assert.Equal("1", exported.GetProperty("Count").GetString());
        Assert.Equal("True", exported.GetProperty("Encrypted").GetString());
        Assert.False(exported.TryGetProperty("endpoint", out _));
    }

    [Fact]
    public async Task CharacterLimitDoesNotSplitASurrogatePair()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "rune-limit.zip");
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(
            new InMemorySafeDiagnosticSink(redactor),
            redactor,
            new DiagnosticExportLimits(MaxFieldLength: 1, MaxStringUtf8Bytes: 16));
        var context = DiagnosticExportContext.Empty with
        {
            Application = DiagnosticExportContext.Empty.Application with { Application = "😀" },
        };

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var applicationName = document.RootElement
            .GetProperty("application")
            .GetProperty("name")
            .GetString();
        Assert.NotNull(applicationName);
        Assert.DoesNotContain('\uFFFD', applicationName);
    }

    [Fact]
    public async Task InvalidDestinationDoesNotLeakExportGate()
    {
        Directory.CreateDirectory(_directory);
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new InMemorySafeDiagnosticSink(redactor), redactor);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => exporter.ExportAsync(
            Path.Combine(_directory, "bad\0name.zip"),
            DiagnosticExportContext.Empty,
            CancellationToken.None));

        var valid = Path.Combine(_directory, "valid.zip");
        await exporter.ExportAsync(valid, DiagnosticExportContext.Empty, CancellationToken.None);
        Assert.True(File.Exists(valid));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task LimitsAllCollectionCountsAndUtf8StringBytes()
    {
        Directory.CreateDirectory(_directory);
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        DiagnosticField[] safeFields =
        [
            new("Kind", "Keyboard"),
            new("Boundary", "UiCaptured"),
            new("Count", "1"),
            new("Reason", "SessionClosing"),
            new("Encrypted", "True"),
            new("Sampled", "True"),
        ];
        sink.Write(new SafeDiagnosticEventInput(
            "EVENT",
            "corr",
            "密密密密密密",
            safeFields));
        string[] encodingNames =
        [
            "Raw", "CopyRect", "ZRLE", "DesktopSize", "Cursor", "ARD.DisplayInfo",
        ];
        var profiles = Enumerable.Range(0, 6).Select(index => new DiagnosticProfileSummary(
            $"profile-{index}",
            $"host-{index}",
            5900,
            "operator",
            "3.8",
            "30",
            encodingNames.ToDictionary(value => value, value => (long)Array.IndexOf(encodingNames, value))))
            .ToArray();
        string[] counterNames =
        [
            "Session.RefreshMode", "Session.TargetFps", "Session.ActualFps",
            "Session.ReceiveBytesPerSecond", "Session.ResponseMilliseconds",
            "Session.PresentationMilliseconds",
        ];
        var context = new DiagnosticExportContext(
            DiagnosticExportContext.Empty.Application,
            profiles,
            counterNames.ToDictionary(value => value, value => (long)Array.IndexOf(counterNames, value)),
            IncludeHosts: false);
        using var exporter = new DiagnosticExporter(
            sink,
            redactor,
            new DiagnosticExportLimits(
                MaxEvents: 2,
                MaxFieldLength: 100,
                MaxArchiveBytes: 128 * 1024,
                MaxProfiles: 2,
                MaxEncodingStatisticsPerProfile: 2,
                MaxPerformanceCounters: 2,
                MaxFieldsPerEvent: 2,
                MaxStringUtf8Bytes: 7,
                MaxUncompressedBytes: 64 * 1024,
                MaxEntryUncompressedBytes: 48 * 1024));
        var destination = Path.Combine(_directory, "bounded.zip");

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        using var document = JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("profiles").GetArrayLength());
        Assert.Equal(2, root.GetProperty("profiles")[0].GetProperty("encodingStatistics").EnumerateObject().Count());
        Assert.Equal(2, root.GetProperty("performanceCounters").EnumerateObject().Count());
        Assert.Equal(2, root.GetProperty("events")[0].GetProperty("fields").EnumerateObject().Count());
        Assert.True(Encoding.UTF8.GetByteCount(root.GetProperty("events")[0].GetProperty("message").GetString()!) <= 7);
    }

    [Theory]
    [InlineData(512, 128 * 1024)]
    [InlineData(128 * 1024, 1)]
    public async Task SizeLimitFailurePreservesExistingDestinationAndCleansTemporaryFiles(
        long maxUncompressedBytes,
        long maxArchiveBytes)
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, $"existing-{maxUncompressedBytes}-{maxArchiveBytes}.zip");
        await File.WriteAllTextAsync(destination, "existing");
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor, maxFieldLength: 256 * 1024);
        sink.Write(new SafeDiagnosticEventInput("HUGE", "corr", new string('x', 200_000)));
        using var exporter = new DiagnosticExporter(
            sink,
            redactor,
            new DiagnosticExportLimits(
                MaxEvents: 2,
                MaxFieldLength: 256 * 1024,
                MaxArchiveBytes: maxArchiveBytes,
                MaxUncompressedBytes: maxUncompressedBytes,
                MaxEntryUncompressedBytes: maxUncompressedBytes));

        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(
            destination,
            DiagnosticExportContext.Empty,
            CancellationToken.None));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ExceptionMetadataNeverStoresRawMessageAndClassifiedFieldsReachExporter()
    {
        const string host = "private-host.internal";
        const string address = "10.23.45.67";
        const string path = @"C:\Users\private\credential.key";
        const string query = "?token=private-query-value";
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        var exception = new InvalidOperationException($"host={host} ip={address} path={path} {query}");
        sink.Write(new SafeDiagnosticEventInput(
            "CONNECT_FAILED",
            "corr-private",
            "Connection failed.",
            [
                new("endpoint", host, DiagnosticFieldCategory.Host),
                new("credentialPath", path, DiagnosticFieldCategory.Path),
                new("fingerprint", "SHA256:safe", DiagnosticFieldCategory.Public),
            ],
            exception));

        var stored = sink.Snapshot().Single();
        Assert.Equal(nameof(InvalidOperationException), stored.Exception?.Type);
        Assert.Equal($"0x{exception.HResult:X8}", stored.Exception?.HResult);
        Assert.Equal(DiagnosticFieldCategory.Host, stored.Fields.Single(field => field.Name == "endpoint").Category);
        var exceptionMetadata = JsonSerializer.Serialize(stored.Exception);
        Assert.DoesNotContain(host, exceptionMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain(address, exceptionMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain(path, exceptionMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain(query, exceptionMetadata, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportAppliesHostAndPathPrivacyWithoutDictionaryHash(bool includeHosts)
    {
        Directory.CreateDirectory(_directory);
        const string host = "private-host.internal";
        const string path = @"C:\Users\private\credential.key";
        var oldFingerprint = $"SHA256:{new string('A', 43)}";
        var newFingerprint = $"SHA256:{new string('B', 43)}";
        var hostHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(host))).ToLowerInvariant();
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        sink.Write(new SafeDiagnosticEventInput(
            "SSH_HOST_KEY_CHANGED",
            "corr-host-policy",
            "Host key changed.",
            [
                new("endpoint", host, DiagnosticFieldCategory.Host),
                new("privateKeyPath", path, DiagnosticFieldCategory.Path),
                new("oldFingerprint", oldFingerprint, DiagnosticFieldCategory.Public),
                new("newFingerprint", newFingerprint, DiagnosticFieldCategory.Public),
            ],
            new InvalidOperationException($"{host}|{path}|?query=private")));
        var context = new DiagnosticExportContext(
            DiagnosticExportContext.Empty.Application,
            [new DiagnosticProfileSummary("Office Mac", host, 5900, "operator", "3.8", "30")],
            new Dictionary<string, long>(),
            includeHosts);
        using var exporter = new DiagnosticExporter(sink, redactor);
        var destination = Path.Combine(_directory, $"privacy-{includeHosts}.zip");

        await exporter.ExportAsync(destination, context, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        var content = await ReadAllAsync(archive);
        Assert.DoesNotContain(path, content, StringComparison.Ordinal);
        Assert.DoesNotContain("?query=private", content, StringComparison.Ordinal);
        Assert.DoesNotContain(hostHash, content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(oldFingerprint, content, StringComparison.Ordinal);
        Assert.Contains(newFingerprint, content, StringComparison.Ordinal);
        Assert.DoesNotContain(host, content, StringComparison.Ordinal);

        using var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));
        var privacy = manifest.RootElement.GetProperty("privacy");
        Assert.True(privacy.GetProperty("pathFieldsOmitted").GetInt32() >= 1);
        Assert.Equal(2, privacy.GetProperty("hostFieldsOmitted").GetInt32());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task<string> ReadAllAsync(ZipArchive archive)
    {
        var builder = new StringBuilder();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            builder.Append(await reader.ReadToEndAsync());
        }

        return builder.ToString();
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task<JsonDocument> ExportSingleEventAsync(
        SafeDiagnosticEvent diagnosticEvent,
        bool includeHosts,
        string archiveName,
        DiagnosticExportLimits? limits = null)
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, archiveName);
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new StaticSnapshotSink([diagnosticEvent]), redactor, limits);
        var context = DiagnosticExportContext.Empty with { IncludeHosts = includeHosts };
        await exporter.ExportAsync(destination, context, CancellationToken.None);
        using var archive = ZipFile.OpenRead(destination);
        return JsonDocument.Parse(await ReadEntryAsync(archive, "diagnostics.json"));
    }

    private sealed class BlockingSnapshotSink : ISafeDiagnosticSink
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Write(SafeDiagnosticEventInput diagnosticEvent)
        {
        }

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot()
        {
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return [];
        }
    }

    private sealed class CancellingSnapshotSink(CancellationTokenSource cancellation) : ISafeDiagnosticSink
    {
        private int _snapshots;

        public void Write(SafeDiagnosticEventInput diagnosticEvent)
        {
        }

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot()
        {
            if (Interlocked.Increment(ref _snapshots) == 1)
            {
                cancellation.Cancel();
            }

            return [];
        }
    }

    private sealed class StaticSnapshotSink(IReadOnlyList<SafeDiagnosticEvent> events) : ISafeDiagnosticSink
    {
        public void Write(SafeDiagnosticEventInput diagnosticEvent)
        {
        }

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => events;
    }
}
