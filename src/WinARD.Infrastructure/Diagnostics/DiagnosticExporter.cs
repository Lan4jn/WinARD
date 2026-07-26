using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinARD.Infrastructure.Diagnostics;

public sealed record DiagnosticApplicationInfo(
    string Application,
    string ApplicationVersion,
    string OperatingSystem,
    string DotNetVersion,
    string WindowsAppSdkVersion);

public sealed record DiagnosticProfileSummary(
    string DisplayName,
    string Host,
    int Port,
    string Username,
    string ProtocolVersion,
    string SecurityType,
    IReadOnlyDictionary<string, long>? EncodingStatistics = null,
    string? ErrorCode = null,
    string? CorrelationId = null);

public sealed record DiagnosticExportContext(
    DiagnosticApplicationInfo Application,
    IReadOnlyList<DiagnosticProfileSummary> Profiles,
    IReadOnlyDictionary<string, long> PerformanceCounters,
    bool IncludeHosts)
{
    public static DiagnosticExportContext Empty { get; } = new(
        new DiagnosticApplicationInfo(
            "WinARD",
            typeof(DiagnosticExporter).Assembly.GetName().Version?.ToString() ?? "unknown",
            Environment.OSVersion.VersionString,
            Environment.Version.ToString(),
            "unknown"),
        [],
        new Dictionary<string, long>(),
        IncludeHosts: false);
}

public sealed record DiagnosticExportLimits(
    int MaxEvents = 500,
    int MaxFieldLength = 4_096,
    long MaxArchiveBytes = 5 * 1024 * 1024);

public sealed class DiagnosticExporter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ISafeDiagnosticSink _sink;
    private readonly SecretRedactor _redactor;
    private readonly DiagnosticExportLimits _limits;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DiagnosticExporter(
        ISafeDiagnosticSink sink,
        SecretRedactor redactor,
        DiagnosticExportLimits? limits = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _limits = limits ?? new DiagnosticExportLimits();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxEvents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxFieldLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxArchiveBytes);
    }

    public async Task ExportAsync(
        string destinationPath,
        DiagnosticExportContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A diagnostic export is already in progress.");
        }

        var fullDestination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestination) ??
            throw new ArgumentException("The destination must have a parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteArchiveAsync(temporaryPath, context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var length = new FileInfo(temporaryPath).Length;
            if (length > _limits.MaxArchiveBytes)
            {
                throw new InvalidOperationException("The diagnostic archive exceeded its configured size limit.");
            }

            if (File.Exists(fullDestination))
            {
                File.Replace(temporaryPath, fullDestination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullDestination);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
            _gate.Release();
        }
    }

    private async Task WriteArchiveAsync(
        string temporaryPath,
        DiagnosticExportContext context,
        CancellationToken cancellationToken)
    {
        var events = _sink.Snapshot().TakeLast(_limits.MaxEvents).Select(SafeEvent).ToArray();
        var profiles = context.Profiles.Select(profile => new
        {
            displayName = Safe(profile.DisplayName),
            host = context.IncludeHosts ? Safe(profile.Host) : null,
            hostSha256 = Hash(profile.Host),
            profile.Port,
            username = Safe(profile.Username),
            protocolVersion = Safe(profile.ProtocolVersion),
            securityType = Safe(profile.SecurityType),
            encodingStatistics = profile.EncodingStatistics is null
                ? null
                : SafeDictionary(profile.EncodingStatistics),
            errorCode = Safe(profile.ErrorCode),
            correlationId = Safe(profile.CorrelationId),
        }).ToArray();
        var diagnostics = new
        {
            generatedUtc = DateTimeOffset.UtcNow,
            application = new
            {
                name = Safe(context.Application.Application),
                version = Safe(context.Application.ApplicationVersion),
                operatingSystem = Safe(context.Application.OperatingSystem),
                dotNet = Safe(context.Application.DotNetVersion),
                windowsAppSdk = Safe(context.Application.WindowsAppSdkVersion),
            },
            profiles,
            performanceCounters = SafeDictionary(context.PerformanceCounters),
            events,
        };
        var manifest = new
        {
            schemaVersion = 1,
            redactionVersion = SecretRedactor.Version,
            included = new[]
            {
                "Application, OS, .NET and Windows App SDK versions",
                "Correlation IDs and stable error codes",
                "Redacted structured events",
                "Non-secret connection profile summaries",
                "Protocol, security, encoding and performance statistics",
            },
            excluded = new[]
            {
                "Passwords, passphrases, vault master secrets, private keys and credential files",
                "Clipboard content",
                "SQLite databases and SQLite WAL/SHM files",
                "known_hosts and temporary host-key material",
                "Raw exception text before redaction",
            },
            limits = _limits,
        };

        await using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        await WriteJsonEntryAsync(archive, "manifest.json", manifest, cancellationToken).ConfigureAwait(false);
        await WriteJsonEntryAsync(archive, "diagnostics.json", diagnostics, cancellationToken).ConfigureAwait(false);
    }

    private object SafeEvent(SafeDiagnosticEvent item) => new
    {
        item.Timestamp,
        code = Safe(item.Code),
        correlationId = Safe(item.CorrelationId),
        message = Safe(item.Message),
        fields = SafeStringDictionary(item.Fields),
        exception = Safe(item.RedactedException),
    };

    private string? Safe(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var redacted = _redactor.Redact(value);
        return redacted.Length <= _limits.MaxFieldLength
            ? redacted
            : redacted[.._limits.MaxFieldLength];
    }

    private Dictionary<string, TValue> SafeDictionary<TValue>(
        IReadOnlyDictionary<string, TValue> values)
    {
        var safe = new Dictionary<string, TValue>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var key = Safe(pair.Key) ?? string.Empty;
            safe[key] = pair.Value;
        }

        return safe;
    }

    private Dictionary<string, string> SafeStringDictionary(
        IReadOnlyDictionary<string, string> values)
    {
        var safe = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var key = Safe(pair.Key) ?? string.Empty;
            safe[key] = Safe(pair.Value) ?? string.Empty;
        }

        return safe;
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(entryName) || entryName.Contains("..", StringComparison.Ordinal) ||
            entryName.Contains('\\'))
        {
            throw new InvalidOperationException("Unsafe diagnostic archive entry name.");
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, value, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _gate.Dispose();
}
