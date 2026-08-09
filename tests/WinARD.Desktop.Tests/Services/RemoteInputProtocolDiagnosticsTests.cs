using WinARD.Desktop.Input;
using WinARD.Desktop.Services;
using WinARD.Infrastructure.Diagnostics;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Services;

public sealed class RemoteInputProtocolDiagnosticsTests
{
    [Fact]
    public async Task Pointer_write_records_started_and_completed_without_content()
    {
        var sink = new RecordingSink();
        await using var client = new RfbClient(
            new MemoryStream(),
            diagnosticSink: sink);

        await client.SendPointerAsync(1, 12, 34, CancellationToken.None);

        AssertBoundaries(
            sink.Events,
            RemoteInputKind.Pointer,
            RemoteInputBoundary.ProtocolWriteStarted,
            RemoteInputBoundary.ProtocolWriteCompleted);
    }

    [Fact]
    public async Task Key_write_records_started_and_completed_without_content()
    {
        var sink = new RecordingSink();
        await using var client = new RfbClient(
            new MemoryStream(),
            diagnosticSink: sink);

        await client.SendKeyAsync(0xff0d, true, CancellationToken.None);

        AssertBoundaries(
            sink.Events,
            RemoteInputKind.Keyboard,
            RemoteInputBoundary.ProtocolWriteStarted,
            RemoteInputBoundary.ProtocolWriteCompleted);
    }

    [Fact]
    public async Task Failed_write_records_started_only_and_preserves_exception()
    {
        var sink = new RecordingSink();
        await using var client = new RfbClient(
            new ThrowingWriteStream(),
            diagnosticSink: sink);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            client.SendPointerAsync(0, 12, 34, CancellationToken.None).AsTask());

        Assert.Equal("write failed", exception.Message);
        var diagnostic = Assert.Single(sink.Events);
        Assert.Equal(
            nameof(RemoteInputBoundary.ProtocolWriteStarted),
            Field(diagnostic, "Boundary"));
    }

    private static void AssertBoundaries(
        List<SafeDiagnosticEventInput> events,
        RemoteInputKind kind,
        params RemoteInputBoundary[] boundaries)
    {
        Assert.Equal(boundaries.Length, events.Count);
        Assert.Equal(
            boundaries.Select(boundary => boundary.ToString()),
            events.Select(diagnostic => Field(diagnostic, "Boundary")));
        Assert.All(
            events,
            diagnostic =>
            {
                Assert.Equal(kind.ToString(), Field(diagnostic, "Kind"));
                Assert.Equal(bool.FalseString, Field(diagnostic, "Encrypted"));
                Assert.DoesNotContain(
                    diagnostic.Fields!,
                    field => field.Name is "Keysym" or "Text" or "Coordinate" or
                        "Buttons" or "Payload");
            });
    }

    private static string Field(SafeDiagnosticEventInput diagnostic, string name) =>
        Assert.Single(diagnostic.Fields!, field => field.Name == name).Value!;

    private sealed class RecordingSink : ISafeDiagnosticSink
    {
        public List<SafeDiagnosticEventInput> Events { get; } = [];

        public void Write(SafeDiagnosticEventInput diagnosticEvent) =>
            Events.Add(diagnosticEvent);

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
    }

    private sealed class ThrowingWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("write failed"));
    }
}
