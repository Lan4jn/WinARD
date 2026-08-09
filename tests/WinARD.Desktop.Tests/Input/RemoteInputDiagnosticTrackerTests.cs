using System.Collections.Concurrent;
using System.Globalization;
using WinARD.Desktop.Input;
using WinARD.Infrastructure.Diagnostics;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Input;

public sealed class RemoteInputDiagnosticTrackerTests
{
    [Fact]
    public void Activity_samples_first_and_every_eighth_count()
    {
        var sink = new RecordingSink();
        var tracker = new RemoteInputDiagnosticTracker(sink);

        for (var index = 0; index < 17; index++)
        {
            tracker.Record(
                RemoteInputKind.Keyboard,
                RemoteInputBoundary.UiCaptured);
        }

        Assert.Equal([1L, 8L, 16L], Counts(sink.Events));
    }

    [Fact]
    public void Pointer_activity_uses_bounded_high_frequency_samples()
    {
        var sink = new RecordingSink();
        var tracker = new RemoteInputDiagnosticTracker(sink);

        for (var index = 0; index < 512; index++)
        {
            tracker.Record(
                RemoteInputKind.Pointer,
                RemoteInputBoundary.ProtocolWriteCompleted,
                encrypted: true);
        }

        Assert.Equal([1L, 8L, 64L, 256L, 512L], Counts(sink.Events));
    }

    [Fact]
    public void Dropped_activity_samples_first_and_every_fourth_count()
    {
        var sink = new RecordingSink();
        var tracker = new RemoteInputDiagnosticTracker(sink);

        for (var index = 0; index < 9; index++)
        {
            tracker.RecordDropped(
                RemoteInputKind.Keyboard,
                RemoteInputDropReason.InvalidTransform);
        }

        Assert.Equal([1L, 4L, 8L], Counts(sink.Events));
        Assert.All(
            sink.Events,
            diagnostic => Assert.Equal(
                nameof(RemoteInputDropReason.InvalidTransform),
                Field(diagnostic, "Reason")));
    }

    [Fact]
    public void Diagnostic_fields_never_include_input_content()
    {
        var sink = new RecordingSink();
        var tracker = new RemoteInputDiagnosticTracker(sink);

        tracker.Record(
            RemoteInputKind.Keyboard,
            RemoteInputBoundary.ProtocolWriteCompleted,
            encrypted: true);

        var diagnostic = Assert.Single(sink.Events);
        Assert.Equal("REMOTE_INPUT_ACTIVITY", diagnostic.Code);
        Assert.Equal(
            ["Kind", "Boundary", "Count", "Encrypted", "Sampled"],
            diagnostic.Fields!.Select(field => field.Name));
        Assert.DoesNotContain(
            diagnostic.Fields!,
            field => field.Name is "Keysym" or "VirtualKey" or "ScanCode" or
                "Text" or "Coordinate" or "Buttons" or "Payload");
    }

    [Fact]
    public void Throwing_sink_never_changes_input_diagnostic_control_flow()
    {
        var tracker = new RemoteInputDiagnosticTracker(new ThrowingSink());

        var exception = Record.Exception(() => tracker.Record(
            RemoteInputKind.Pointer,
            RemoteInputBoundary.ProtocolWriteStarted,
            encrypted: true));

        Assert.Null(exception);
    }

    [Fact]
    public void Null_sink_never_changes_input_diagnostic_control_flow()
    {
        var tracker = new RemoteInputDiagnosticTracker(null);

        var exception = Record.Exception(() => tracker.RecordDropped(
            RemoteInputKind.Keyboard,
            RemoteInputDropReason.SessionClosing));

        Assert.Null(exception);
    }

    [Fact]
    public void Concurrent_activity_produces_unique_monotonic_samples()
    {
        var sink = new RecordingSink();
        var tracker = new RemoteInputDiagnosticTracker(sink);

        Parallel.For(
            0,
            64,
            _ => tracker.Record(
                RemoteInputKind.Pointer,
                RemoteInputBoundary.ProtocolWriteStarted,
                encrypted: true));

        var counts = Counts(sink.Events).Order().ToArray();
        Assert.Equal([1L, 8L, 64L], counts);
        Assert.Equal(counts.Length, counts.Distinct().Count());
    }

    [Fact]
    public async Task Concurrent_samples_are_written_in_count_order()
    {
        var sink = new DelayedFirstSampleSink();
        var tracker = new RemoteInputDiagnosticTracker(sink);
        var first = Task.Factory.StartNew(
            () => tracker.Record(
                RemoteInputKind.Keyboard,
                RemoteInputBoundary.ProtocolWriteCompleted,
                encrypted: true),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(sink.FirstSampleEntered.Wait(TimeSpan.FromSeconds(5)));

        var remaining = Enumerable.Range(0, 7)
            .Select(_ => Task.Factory.StartNew(
                () => tracker.Record(
                    RemoteInputKind.Keyboard,
                    RemoteInputBoundary.ProtocolWriteCompleted,
                    encrypted: true),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        _ = sink.LaterSampleEntered.Wait(TimeSpan.FromSeconds(1));
        sink.ReleaseFirstSample.Set();

        await Task.WhenAll([first, .. remaining]);

        Assert.Equal([1L, 8L], Counts(sink.Events));
    }

    private static long[] Counts(IEnumerable<SafeDiagnosticEventInput> events) =>
        events
            .Select(diagnostic => long.Parse(
                Field(diagnostic, "Count"),
                CultureInfo.InvariantCulture))
            .ToArray();

    private static string Field(SafeDiagnosticEventInput diagnostic, string name) =>
        Assert.Single(diagnostic.Fields!, field => field.Name == name).Value!;

    private sealed class RecordingSink : ISafeDiagnosticSink
    {
        private readonly ConcurrentQueue<SafeDiagnosticEventInput> _events = new();

        public IReadOnlyList<SafeDiagnosticEventInput> Events => _events.ToArray();

        public void Write(SafeDiagnosticEventInput diagnosticEvent) =>
            _events.Enqueue(diagnosticEvent);

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
    }

    private sealed class ThrowingSink : ISafeDiagnosticSink
    {
        public void Write(SafeDiagnosticEventInput diagnosticEvent) =>
            throw new InvalidOperationException("sink failed");

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
    }

    private sealed class DelayedFirstSampleSink : ISafeDiagnosticSink
    {
        private readonly ConcurrentQueue<SafeDiagnosticEventInput> _events = new();

        public ManualResetEventSlim FirstSampleEntered { get; } = new(false);

        public ManualResetEventSlim LaterSampleEntered { get; } = new(false);

        public ManualResetEventSlim ReleaseFirstSample { get; } = new(false);

        public IReadOnlyList<SafeDiagnosticEventInput> Events => _events.ToArray();

        public void Write(SafeDiagnosticEventInput diagnosticEvent)
        {
            var count = long.Parse(
                Field(diagnosticEvent, "Count"),
                CultureInfo.InvariantCulture);
            if (count == 1)
            {
                FirstSampleEntered.Set();
                Assert.True(ReleaseFirstSample.Wait(TimeSpan.FromSeconds(5)));
            }
            else
            {
                LaterSampleEntered.Set();
            }

            _events.Enqueue(diagnosticEvent);
        }

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
    }
}
