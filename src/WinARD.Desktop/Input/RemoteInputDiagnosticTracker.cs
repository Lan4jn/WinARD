using System.Globalization;
using WinARD.Infrastructure.Diagnostics;

namespace WinARD.Desktop.Input;

internal enum RemoteInputKind
{
    Keyboard,
    Pointer,
}

internal enum RemoteInputBoundary
{
    UiCaptured,
    UiDropped,
    ProtocolWriteStarted,
    ProtocolWriteCompleted,
}

internal enum RemoteInputDropReason
{
    SessionClosing,
    InvalidTransform,
}

internal sealed class RemoteInputDiagnosticTracker(ISafeDiagnosticSink? diagnosticSink)
{
    private const int BoundaryCount = 4;
    private readonly long[] _counts = new long[2 * BoundaryCount];
    private readonly object[] _boundaryLocks =
    [
        new(), new(), new(), new(),
        new(), new(), new(), new(),
    ];

    public void Record(
        RemoteInputKind kind,
        RemoteInputBoundary boundary,
        bool? encrypted = null)
    {
        var index = Index(kind, boundary);
        lock (_boundaryLocks[index])
        {
            var count = Interlocked.Increment(ref _counts[index]);
            if (!ShouldSample(kind, boundary, count))
            {
                return;
            }

            Write(kind, boundary, count, reason: null, encrypted);
        }
    }

    public void RecordDropped(RemoteInputKind kind, RemoteInputDropReason reason)
    {
        const RemoteInputBoundary boundary = RemoteInputBoundary.UiDropped;
        var index = Index(kind, boundary);
        lock (_boundaryLocks[index])
        {
            var count = Interlocked.Increment(ref _counts[index]);
            if (!ShouldSample(kind, boundary, count))
            {
                return;
            }

            Write(kind, boundary, count, reason, encrypted: null);
        }
    }

    private static int Index(RemoteInputKind kind, RemoteInputBoundary boundary) =>
        (checked((int)kind) * BoundaryCount) + checked((int)boundary);

    private static bool ShouldSample(
        RemoteInputKind kind,
        RemoteInputBoundary boundary,
        long count)
    {
        if (boundary == RemoteInputBoundary.UiDropped)
        {
            return count == 1 || count % 4 == 0;
        }

        if (kind == RemoteInputKind.Keyboard)
        {
            return count == 1 || count % 8 == 0;
        }

        return count is 1 or 8 or 64 || count % 256 == 0;
    }

    private void Write(
        RemoteInputKind kind,
        RemoteInputBoundary boundary,
        long count,
        RemoteInputDropReason? reason,
        bool? encrypted)
    {
        var fields = new List<DiagnosticField>(6)
        {
            new("Kind", kind.ToString()),
            new("Boundary", boundary.ToString()),
            new("Count", count.ToString(CultureInfo.InvariantCulture)),
        };
        if (reason is not null)
        {
            fields.Add(new DiagnosticField("Reason", reason.Value.ToString()));
        }

        if (encrypted is not null)
        {
            fields.Add(new DiagnosticField("Encrypted", encrypted.Value.ToString()));
        }

        fields.Add(new DiagnosticField("Sampled", bool.TrueString));
        diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
            "REMOTE_INPUT_ACTIVITY",
            Guid.NewGuid().ToString("N"),
            "Remote input activity crossed a diagnostic boundary.",
            fields));
    }
}
