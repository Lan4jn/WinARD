using System.IO.Compression;
using System.Text;
using WinARD.Infrastructure.Diagnostics;
using Xunit;

namespace WinARD.Infrastructure.Tests;

public sealed class DiagnosticExporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"winard-diag-{Guid.NewGuid():N}");

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
        Assert.Contains("hostSha256", content, StringComparison.Ordinal);
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
                $"corr-{index}",
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
        Assert.DoesNotContain("corr-16", json, StringComparison.Ordinal);
        Assert.Contains("corr-19", json, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 41), json, StringComparison.Ordinal);
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
}
