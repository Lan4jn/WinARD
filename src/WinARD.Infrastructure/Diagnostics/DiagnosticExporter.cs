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
    long MaxArchiveBytes = 5 * 1024 * 1024,
    int MaxProfiles = 100,
    int MaxEncodingStatisticsPerProfile = 100,
    int MaxPerformanceCounters = 200,
    int MaxFieldsPerEvent = 100,
    int MaxStringUtf8Bytes = 16 * 1024,
    long MaxUncompressedBytes = 20 * 1024 * 1024,
    long MaxEntryUncompressedBytes = 16 * 1024 * 1024);

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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxProfiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxEncodingStatisticsPerProfile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxPerformanceCounters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxFieldsPerEvent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxStringUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxUncompressedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxEntryUncompressedBytes);
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

        string? temporaryPath = null;
        try
        {
            var fullDestination = Path.GetFullPath(destinationPath);
            var directory = Path.GetDirectoryName(fullDestination) ??
                throw new ArgumentException("The destination must have a parent directory.", nameof(destinationPath));
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
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
            if (temporaryPath is not null)
            {
                TryDelete(temporaryPath);
            }

            _gate.Release();
        }
    }

    private async Task WriteArchiveAsync(
        string temporaryPath,
        DiagnosticExportContext context,
        CancellationToken cancellationToken)
    {
        var events = _sink.Snapshot().TakeLast(_limits.MaxEvents).Select(SafeEvent).ToArray();
        var profiles = context.Profiles.Take(_limits.MaxProfiles).Select(profile => new
        {
            displayName = Safe(profile.DisplayName),
            host = context.IncludeHosts ? Safe(profile.Host) : null,
            hostSha256 = Hash(Safe(profile.Host) ?? string.Empty),
            profile.Port,
            username = Safe(profile.Username),
            protocolVersion = Safe(profile.ProtocolVersion),
            securityType = Safe(profile.SecurityType),
            encodingStatistics = profile.EncodingStatistics is null
                ? null
                : SafeDictionary(profile.EncodingStatistics, _limits.MaxEncodingStatisticsPerProfile),
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
            performanceCounters = SafeDictionary(context.PerformanceCounters, _limits.MaxPerformanceCounters),
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
        long totalUncompressedBytes = 0;
        totalUncompressedBytes += await WriteJsonEntryAsync(
            archive,
            "manifest.json",
            manifest,
            _limits.MaxEntryUncompressedBytes,
            _limits.MaxUncompressedBytes - totalUncompressedBytes,
            cancellationToken).ConfigureAwait(false);
        totalUncompressedBytes += await WriteJsonEntryAsync(
            archive,
            "diagnostics.json",
            diagnostics,
            _limits.MaxEntryUncompressedBytes,
            _limits.MaxUncompressedBytes - totalUncompressedBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private object SafeEvent(SafeDiagnosticEvent item) => new
    {
        item.Timestamp,
        code = Safe(item.Code),
        correlationId = Safe(item.CorrelationId),
        message = Safe(item.Message),
        fields = SafeStringDictionary(item.Fields, _limits.MaxFieldsPerEvent),
        exception = Safe(item.RedactedException),
    };

    private string? Safe(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var redacted = _redactor.Redact(value);
        var characterLimited = redacted.Length <= _limits.MaxFieldLength
            ? redacted
            : redacted[.._limits.MaxFieldLength];
        return LimitUtf8(characterLimited, _limits.MaxStringUtf8Bytes);
    }

    private Dictionary<string, TValue> SafeDictionary<TValue>(
        IReadOnlyDictionary<string, TValue> values,
        int maxCount)
    {
        var safe = new Dictionary<string, TValue>(StringComparer.Ordinal);
        foreach (var pair in values.Take(maxCount))
        {
            var key = Safe(pair.Key) ?? string.Empty;
            safe[key] = pair.Value;
        }

        return safe;
    }

    private Dictionary<string, string> SafeStringDictionary(
        IReadOnlyDictionary<string, string> values,
        int maxCount)
    {
        var safe = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values.Take(maxCount))
        {
            var key = Safe(pair.Key) ?? string.Empty;
            safe[key] = Safe(pair.Value) ?? string.Empty;
        }

        return safe;
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string LimitUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxBytes));
        var written = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var bytes = rune.Utf8SequenceLength;
            if (written + bytes > maxBytes)
            {
                break;
            }

            builder.Append(rune);
            written += bytes;
        }

        return builder.ToString();
    }

    private static async Task<long> WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        long maxEntryBytes,
        long remainingTotalBytes,
        CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(entryName) || entryName.Contains("..", StringComparison.Ordinal) ||
            entryName.Contains('\\'))
        {
            throw new InvalidOperationException("Unsafe diagnostic archive entry name.");
        }

        var limit = Math.Min(maxEntryBytes, remainingTotalBytes);
        if (limit <= 0)
        {
            throw new InvalidOperationException("The diagnostic archive exceeded its uncompressed size limit.");
        }

        await using var buffer = new MemoryStream();
        await using (var bounded = new BoundedWriteStream(buffer, limit))
        {
            await JsonSerializer.SerializeAsync(bounded, value, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        await using var entryStream = entry.Open();
        buffer.Position = 0;
        await buffer.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
        return buffer.Length;
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

    private sealed class BoundedWriteStream(Stream inner, long maxBytes) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            inner.Write(buffer);
            _written += buffer.Length;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _written += buffer.Length;
        }

        private void EnsureCapacity(int count)
        {
            if (_written + count > maxBytes)
            {
                throw new InvalidOperationException(
                    "The diagnostic archive exceeded its uncompressed size limit.");
            }
        }
    }
}
