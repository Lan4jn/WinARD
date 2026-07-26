using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

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

public sealed record SafeDiagnosticEvent(
    DateTimeOffset Timestamp,
    string Code,
    string CorrelationId,
    string Message,
    IReadOnlyDictionary<string, string> Fields,
    string? RedactedException);

public interface ISafeDiagnosticSink
{
    void Write(SafeDiagnosticEventInput diagnosticEvent);

    IReadOnlyList<SafeDiagnosticEvent> Snapshot();
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
    private readonly int _capacity;
    private readonly int _maxFieldLength;
    private readonly Queue<SafeDiagnosticEvent> _events;

    public InMemorySafeDiagnosticSink(SecretRedactor redactor, int capacity = 500, int maxFieldLength = 4_096)
    {
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFieldLength);
        _capacity = capacity;
        _maxFieldLength = maxFieldLength;
        _events = new Queue<SafeDiagnosticEvent>(capacity);
    }

    public void Write(SafeDiagnosticEventInput diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in diagnosticEvent.Fields ?? [])
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                continue;
            }

            fields[Limit(field.Name)] = IsSensitive(field)
                ? SecretRedactor.RedactedValue
                : Sanitize(field.Value ?? string.Empty);
        }

        var stored = new SafeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            Limit(diagnosticEvent.Code),
            Limit(diagnosticEvent.CorrelationId),
            Sanitize(diagnosticEvent.Message),
            new ReadOnlyDictionary<string, string>(fields),
            diagnosticEvent.Exception is null ? null : Sanitize(diagnosticEvent.Exception.ToString()));
        lock (_sync)
        {
            while (_events.Count >= _capacity)
            {
                _events.Dequeue();
            }

            _events.Enqueue(stored);
        }
    }

    public IReadOnlyList<SafeDiagnosticEvent> Snapshot()
    {
        lock (_sync)
        {
            return _events.ToArray();
        }
    }

    private static bool IsSensitive(DiagnosticField field) =>
        SensitiveCategories.Contains(field.Category) || IsSensitiveName(field.Name);

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

    private string Sanitize(string value)
    {
        if (SensitiveAssignment().IsMatch(value))
        {
            return SecretRedactor.RedactedValue;
        }

        return Limit(_redactor.Redact(value));
    }

    private string Limit(string value) => value.Length <= _maxFieldLength
        ? value
        : value[.._maxFieldLength];

    [GeneratedRegex("(?:vault[ _-]?master|clipboard(?:content)?|password|passphrase|secret|private[_-]?key|credential)\\s*[:=]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignment();

}
