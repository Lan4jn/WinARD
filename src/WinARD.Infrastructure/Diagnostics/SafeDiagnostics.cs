using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using WinARD.Domain.Settings;

namespace WinARD.Infrastructure.Diagnostics;

public enum DiagnosticFieldCategory
{
    Public,
    Host,
    Path,
    ClipboardContent,
    Password,
    Secret,
    PrivateKey,
    Credential,
    VaultMaster,
}

public sealed record DiagnosticField(
    string Name,
    string? Value,
    DiagnosticFieldCategory Category = DiagnosticFieldCategory.Public);

public sealed record SafeDiagnosticEventInput(
    string Code,
    string CorrelationId,
    string Message,
    IReadOnlyList<DiagnosticField>? Fields = null,
    Exception? Exception = null);

public sealed record SafeDiagnosticField(
    string Name,
    string Value,
    DiagnosticFieldCategory Category);

public sealed record SafeDiagnosticFailureMetadata(
    string Type,
    string HResult);

public sealed record SafeDiagnosticLimits(
    int MaxEvents = 500,
    int MaxRawTextUtf8Bytes = 64 * 1_024,
    int MaxFieldsPerEvent = 64,
    int MaxFieldUtf8Bytes = 4_096,
    long MaxRingApproximateBytes = 4 * 1_024 * 1_024);

public sealed record SafeDiagnosticEvent(
    DateTimeOffset Timestamp,
    string Code,
    string CorrelationId,
    string Message,
    IReadOnlyList<SafeDiagnosticField> Fields,
    SafeDiagnosticFailureMetadata? Exception);

public interface ISafeDiagnosticSink
{
    void Write(SafeDiagnosticEventInput diagnosticEvent);

    IReadOnlyList<SafeDiagnosticEvent> Snapshot();
}

public sealed class SafeDiagnosticLevelController
{
    private int _level = (int)SafeDiagnosticLevel.Standard;

    public SafeDiagnosticLevel Level
    {
        get => (SafeDiagnosticLevel)Volatile.Read(ref _level);
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            Volatile.Write(ref _level, (int)value);
        }
    }

    internal int MaximumFields => Level switch
    {
        SafeDiagnosticLevel.Minimal => 0,
        SafeDiagnosticLevel.Standard => 16,
        SafeDiagnosticLevel.Verbose => int.MaxValue,
        _ => throw new InvalidOperationException("Diagnostic level is invalid."),
    };
}

public static class SafeDiagnosticWriter
{
    public static bool TryWrite(
        this ISafeDiagnosticSink? sink,
        SafeDiagnosticEventInput diagnosticEvent)
    {
        if (sink is null)
        {
            return false;
        }

        try
        {
            sink.Write(diagnosticEvent);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed partial class InMemorySafeDiagnosticSink : ISafeDiagnosticSink
{
    private static readonly HashSet<DiagnosticFieldCategory> SensitiveCategories =
    [
        DiagnosticFieldCategory.ClipboardContent,
        DiagnosticFieldCategory.Password,
        DiagnosticFieldCategory.Secret,
        DiagnosticFieldCategory.PrivateKey,
        DiagnosticFieldCategory.Credential,
        DiagnosticFieldCategory.VaultMaster,
    ];
    private readonly object _sync = new();
    private readonly SecretRedactor _redactor;
    private readonly SafeDiagnosticLimits _limits;
    private readonly SafeDiagnosticLevelController? _levelController;
    private readonly Queue<StoredEvent> _events;
    private long _approximateSizeBytes;

    public InMemorySafeDiagnosticSink(SecretRedactor redactor, int capacity = 500, int maxFieldLength = 4_096)
        : this(redactor, new SafeDiagnosticLimits(
            MaxEvents: capacity,
            MaxRawTextUtf8Bytes: maxFieldLength,
            MaxFieldsPerEvent: 64,
            MaxFieldUtf8Bytes: maxFieldLength,
            MaxRingApproximateBytes: 4 * 1_024 * 1_024))
    {
    }

    public InMemorySafeDiagnosticSink(
        SecretRedactor redactor,
        SafeDiagnosticLevelController levelController)
        : this(redactor, new SafeDiagnosticLimits()) =>
        _levelController = levelController ?? throw new ArgumentNullException(nameof(levelController));

    public InMemorySafeDiagnosticSink(SecretRedactor redactor, SafeDiagnosticLimits limits)
    {
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxEvents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxRawTextUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxFieldsPerEvent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxFieldUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxRingApproximateBytes);
        _events = new Queue<StoredEvent>(_limits.MaxEvents);
    }

    public long ApproximateSizeBytes
    {
        get
        {
            lock (_sync)
            {
                return _approximateSizeBytes;
            }
        }
    }

    public void Write(SafeDiagnosticEventInput diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        var fields = new Dictionary<string, SafeDiagnosticField>(StringComparer.OrdinalIgnoreCase);
        var inputFields = diagnosticEvent.Fields ?? [];
        var maximumFields = Math.Min(
            _limits.MaxFieldsPerEvent,
            _levelController?.MaximumFields ?? int.MaxValue);
        for (var index = 0; index < Math.Min(inputFields.Count, maximumFields); index++)
        {
            var field = inputFields[index];
            var name = LimitUtf8(field.Name, _limits.MaxFieldUtf8Bytes);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            fields[name] = new SafeDiagnosticField(
                name,
                IsSensitive(field.Category, name)
                    ? LimitUtf8(SecretRedactor.RedactedValue, _limits.MaxFieldUtf8Bytes)
                    : SanitizeBounded(field.Value ?? string.Empty, _limits.MaxFieldUtf8Bytes),
                field.Category);
        }

        var stored = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            LimitUtf8(diagnosticEvent.Code, _limits.MaxFieldUtf8Bytes),
            LimitUtf8(diagnosticEvent.CorrelationId, _limits.MaxFieldUtf8Bytes),
            SanitizeBounded(diagnosticEvent.Message, _limits.MaxRawTextUtf8Bytes),
            fields.Values.ToArray(),
            diagnosticEvent.Exception is null
                ? null
                : new SafeDiagnosticFailureMetadata(
                    LimitUtf8(diagnosticEvent.Exception.GetType().Name, _limits.MaxFieldUtf8Bytes),
                    $"0x{diagnosticEvent.Exception.HResult:X8}"));
        var approximateSizeBytes = GetApproximateSizeBytes(stored);
        lock (_sync)
        {
            if (approximateSizeBytes > _limits.MaxRingApproximateBytes)
            {
                return;
            }

            while (_events.Count >= _limits.MaxEvents ||
                   _approximateSizeBytes + approximateSizeBytes > _limits.MaxRingApproximateBytes)
            {
                _approximateSizeBytes -= _events.Dequeue().ApproximateSizeBytes;
            }

            _events.Enqueue(new StoredEvent(stored, approximateSizeBytes));
            _approximateSizeBytes += approximateSizeBytes;
        }
    }

    public IReadOnlyList<SafeDiagnosticEvent> Snapshot()
    {
        lock (_sync)
        {
            return _events.Select(static item => item.Event).ToArray();
        }
    }

    private static bool IsSensitive(DiagnosticFieldCategory category, string name) =>
        SensitiveCategories.Contains(category) || IsSensitiveName(name);

    private static bool IsSensitiveName(string name)
    {
        var normalized = new string(name
            .Where(static character => character is not ('-' or '_') && !char.IsWhiteSpace(character))
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized.StartsWith("vaultmaster", StringComparison.Ordinal) ||
               normalized.StartsWith("macpassword", StringComparison.Ordinal) ||
               normalized.StartsWith("sshpassword", StringComparison.Ordinal) ||
               normalized.StartsWith("privatekeypassphrase", StringComparison.Ordinal) ||
               normalized.StartsWith("clipboardcontent", StringComparison.Ordinal) ||
               normalized.StartsWith("clipboard", StringComparison.Ordinal) ||
               normalized.StartsWith("password", StringComparison.Ordinal) ||
               normalized.StartsWith("passphrase", StringComparison.Ordinal) ||
               normalized.StartsWith("secret", StringComparison.Ordinal) ||
               normalized.StartsWith("privatekey", StringComparison.Ordinal) ||
               normalized.StartsWith("credential", StringComparison.Ordinal);
    }

    private string SanitizeBounded(string value, int maxUtf8Bytes)
    {
        if (ExceedsUtf8Limit(value, maxUtf8Bytes))
        {
            return LimitUtf8(SecretRedactor.RedactedValue, maxUtf8Bytes);
        }

        if (SensitiveAssignment().IsMatch(value))
        {
            return LimitUtf8(SecretRedactor.RedactedValue, maxUtf8Bytes);
        }

        return LimitUtf8(_redactor.Redact(value), maxUtf8Bytes);
    }

    private static long GetApproximateSizeBytes(SafeDiagnosticEvent diagnosticEvent)
    {
        long size = Encoding.UTF8.GetByteCount(diagnosticEvent.Code) +
                    Encoding.UTF8.GetByteCount(diagnosticEvent.CorrelationId) +
                    Encoding.UTF8.GetByteCount(diagnosticEvent.Message);
        foreach (var field in diagnosticEvent.Fields)
        {
            size += Encoding.UTF8.GetByteCount(field.Name);
            size += Encoding.UTF8.GetByteCount(field.Value);
        }

        if (diagnosticEvent.Exception is not null)
        {
            size += Encoding.UTF8.GetByteCount(diagnosticEvent.Exception.Type);
            size += Encoding.UTF8.GetByteCount(diagnosticEvent.Exception.HResult);
        }

        return size;
    }

    private static string LimitUtf8(string value, int maxBytes)
    {
        var consumedChars = 0;
        var consumedBytes = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var charsConsumed);
            if (status != OperationStatus.Done || consumedBytes + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }

            consumedChars += charsConsumed;
            consumedBytes += rune.Utf8SequenceLength;
            remaining = remaining[charsConsumed..];
        }

        return consumedChars == value.Length ? value : value[..consumedChars];
    }

    private static bool ExceedsUtf8Limit(string value, int maxBytes) =>
        value.Length > maxBytes || Encoding.UTF8.GetByteCount(value) > maxBytes;

    private sealed record StoredEvent(SafeDiagnosticEvent Event, long ApproximateSizeBytes);

    [GeneratedRegex("(?:vault[ _-]?master|clipboard(?:content)?|password|passphrase|secret|private[_-]?key|credential)\\s*[:=]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignment();

}
