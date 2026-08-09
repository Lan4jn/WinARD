using System.Buffers.Binary;
using WinARD.Remote.Protocol.Framebuffer;
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

    [Fact]
    public async Task Queued_pointer_records_boundaries_only_when_its_protocol_write_runs()
    {
        var sink = new RecordingSink();
        await using var stream = new GatedRuntimeWriteStream(
            [.. System.Text.Encoding.ASCII.GetBytes("RFB 003.008\n"), 1, 30, .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream, diagnosticSink: sink);
        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        sink.Events.Clear();
        var gate = stream.BlockNextWrite();
        var background = client.SendClipboardTextAsync("block", CancellationToken.None).AsTask();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pointer = client.SendPointerAsync(0, 1, 1, CancellationToken.None).AsTask();

        try
        {
            Assert.Empty(sink.Events);
            Assert.False(pointer.IsCompleted);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        await Task.WhenAll(background, pointer).WaitAsync(TimeSpan.FromSeconds(5));
        AssertBoundaries(
            sink.Events,
            RemoteInputKind.Pointer,
            RemoteInputBoundary.ProtocolWriteStarted,
            RemoteInputBoundary.ProtocolWriteCompleted);
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

    private static byte[] ServerInit(ushort width, ushort height)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), height);
        PixelFormat.WinArdBgra32.ToWireBytes().CopyTo(bytes, 4);
        return bytes;
    }

    private sealed class GatedRuntimeWriteStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly MemoryStream _output = new();
        private WriteGate? _nextGate;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public WriteGate BlockNextWrite()
        {
            var gate = new WriteGate();
            if (Interlocked.CompareExchange(ref _nextGate, gate, null) is not null)
            {
                throw new InvalidOperationException("A write is already blocked.");
            }

            return gate;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            _output.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var gate = Interlocked.Exchange(ref _nextGate, null);
            if (gate is not null)
            {
                gate.Started.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
            }

            await _output.WriteAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public sealed class WriteGate
        {
            public TaskCompletionSource Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
