using System.Runtime.InteropServices;
using System.Text;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;
using WinARD.Testing.Streams;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.IO;

public sealed class RfbReaderTests
{
    [Fact]
    public async Task ReadExactly_handles_one_byte_chunks()
    {
        await using var stream = new ChunkedReadStream([0x00, 0x02, 0x12, 0x34], 1);
        var reader = new RfbReader(stream, ProtocolLimits.Default);

        Assert.Equal((ushort)2, await reader.ReadUInt16Async(CancellationToken.None));
        Assert.Equal((ushort)0x1234, await reader.ReadUInt16Async(CancellationToken.None));
    }

    [Fact]
    public async Task ReadUInt32_reads_big_endian_value()
    {
        await using var stream = new ChunkedReadStream([0x01, 0x02, 0x03, 0x04], 1);
        var reader = new RfbReader(stream, ProtocolLimits.Default);

        Assert.Equal(0x01020304u, await reader.ReadUInt32Async(CancellationToken.None));
    }

    [Fact]
    public async Task ReadUInt16_rejects_width_above_message_limit_without_reading()
    {
        var stream = new ThrowOnReadStream();
        var reader = new RfbReader(stream, new ProtocolLimits(1, 1024));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            reader.ReadUInt16Async(CancellationToken.None).AsTask());

        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1", exception.Message, StringComparison.Ordinal);
        Assert.False(stream.ReadAttempted);
    }

    [Fact]
    public async Task ReadUInt32_rejects_width_above_message_limit_without_reading()
    {
        var stream = new ThrowOnReadStream();
        var reader = new RfbReader(stream, new ProtocolLimits(1, 1024));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            reader.ReadUInt32Async(CancellationToken.None).AsTask());

        Assert.Contains("4", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1", exception.Message, StringComparison.Ordinal);
        Assert.False(stream.ReadAttempted);
    }

    [Fact]
    public async Task ReadUInt16_accepts_width_equal_to_message_limit()
    {
        await using var stream = new ChunkedReadStream([0x12, 0x34], 1);
        var reader = new RfbReader(stream, new ProtocolLimits(2, 1024));

        Assert.Equal((ushort)0x1234, await reader.ReadUInt16Async(CancellationToken.None));
    }

    [Fact]
    public async Task ReadUInt32_accepts_width_equal_to_message_limit()
    {
        await using var stream = new ChunkedReadStream([0x01, 0x02, 0x03, 0x04], 1);
        var reader = new RfbReader(stream, new ProtocolLimits(4, 1024));

        Assert.Equal(0x01020304u, await reader.ReadUInt32Async(CancellationToken.None));
    }

    [Fact]
    public async Task ReadByte_reads_value()
    {
        await using var stream = new ChunkedReadStream([0xA5], 1);
        var reader = new RfbReader(stream, ProtocolLimits.Default);

        Assert.Equal((byte)0xA5, await reader.ReadByteAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadBytes_returns_shared_empty_array_for_zero_count()
    {
        var reader = new RfbReader(Stream.Null, ProtocolLimits.Default);

        var bytes = await reader.ReadBytesAsync(0, CancellationToken.None);

        Assert.Same(Array.Empty<byte>(), bytes);
    }

    [Fact]
    public async Task ReadBytes_rejects_negative_count()
    {
        var reader = new RfbReader(Stream.Null, ProtocolLimits.Default);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            reader.ReadBytesAsync(-1, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ReadBytes_rejects_allocation_above_limit_without_reading()
    {
        var stream = new ThrowOnReadStream();
        var reader = new RfbReader(stream, new ProtocolLimits(1024, 1024 * 1024));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            reader.ReadBytesAsync(1025, CancellationToken.None).AsTask());

        Assert.Contains("1025", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1024", exception.Message, StringComparison.Ordinal);
        Assert.False(stream.ReadAttempted);
    }

    [Fact]
    public async Task ReadBytes_accepts_count_equal_to_message_limit()
    {
        await using var stream = new ChunkedReadStream([0xA5], 1);
        var reader = new RfbReader(stream, new ProtocolLimits(1, 1024));

        Assert.Equal([0xA5], await reader.ReadBytesAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task ReadBytes_wraps_premature_end_of_stream()
    {
        await using var stream = new ChunkedReadStream([0x01], 1);
        var reader = new RfbReader(stream, ProtocolLimits.Default);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            reader.ReadBytesAsync(2, CancellationToken.None).AsTask());

        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
        var innerException = Assert.IsType<EndOfStreamException>(exception.InnerException);
        Assert.Contains("Expected 2 bytes", innerException.Message, StringComparison.Ordinal);
        Assert.Contains("after 1 bytes", innerException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadBytes_zeroes_partially_filled_buffer_when_failure_reason_ends_early()
    {
        var echoedSecret = Encoding.UTF8.GetBytes("echoed-failure-secret");
        await using var stream = new CapturingPartialReadStream(echoedSecret);
        var reader = new RfbReader(stream, ProtocolLimits.Default);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            reader.ReadBytesAsync(echoedSecret.Length + 5, CancellationToken.None).AsTask());

        var capturedBuffer = Assert.IsType<byte[]>(stream.CapturedBuffer);
        Assert.All(capturedBuffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ReadBytes_zeroes_partially_filled_buffer_before_propagating_cancellation()
    {
        var echoedSecret = Encoding.UTF8.GetBytes("cancelled-failure-secret");
        using var cancellation = new CancellationTokenSource();
        await using var stream = new CapturingPartialReadStream(echoedSecret, cancellation);
        var reader = new RfbReader(stream, ProtocolLimits.Default);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadBytesAsync(echoedSecret.Length + 5, cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var capturedBuffer = Assert.IsType<byte[]>(stream.CapturedBuffer);
        Assert.All(capturedBuffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ReadByte_propagates_cancellation()
    {
        var reader = new RfbReader(new ThrowOnReadStream(), ProtocolLimits.Default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadByteAsync(cancellation.Token).AsTask());
    }

    [Fact]
    public async Task ReadByte_propagates_asynchronous_cancellation_to_stream()
    {
        await using var stream = new BlockingReadStream();
        var reader = new RfbReader(stream, ProtocolLimits.Default);
        using var cancellation = new CancellationTokenSource();

        var readTask = reader.ReadByteAsync(cancellation.Token).AsTask();
        try
        {
            await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                readTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, stream.ReceivedCancellationToken);
        }
        finally
        {
            await stream.DisposeAsync();
            await ObserveCompletionAsync(readTask);
        }
    }

    [Fact]
    public void ChunkedReadStream_rejects_reads_after_disposal()
    {
        using var stream = new ChunkedReadStream([0x01], 1);
        stream.Dispose();

        Assert.False(stream.CanRead);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }

    [Fact]
    public async Task ChunkedReadStream_rejects_operations_after_disposal()
    {
        await using var stream = new ChunkedReadStream([0x01], 1);
        await stream.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => { _ = stream.Length; });
        Assert.Throws<ObjectDisposedException>(() => { _ = stream.Position; });
        Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);
        Assert.Throws<ObjectDisposedException>(() => stream.Flush());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.FlushAsync());
    }

    [Fact]
    public async Task BlockingReadStream_disposal_ends_pending_read()
    {
        await using var stream = new BlockingReadStream();
        var readTask = stream.ReadAsync(new byte[1], CancellationToken.None).AsTask();
        await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));

        await stream.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            readTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(stream.CanRead);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }

    [Fact]
    public void Constructor_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new RfbReader(null!, ProtocolLimits.Default));
        Assert.Throws<ArgumentNullException>(() => new RfbReader(Stream.Null, null!));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void ProtocolLimits_rejects_non_positive_values(int maxMessageBytes, int maxFramebufferBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProtocolLimits(maxMessageBytes, maxFramebufferBytes));
    }

    [Fact]
    public void ProtocolLimits_default_has_required_values()
    {
        Assert.Equal(16 * 1024 * 1024, ProtocolLimits.Default.MaxMessageBytes);
        Assert.Equal(256 * 1024 * 1024, ProtocolLimits.Default.MaxFramebufferBytes);
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public bool ReadAttempted { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Throw();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Throw());
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Throw()
        {
            ReadAttempted = true;
            throw new InvalidOperationException("The stream must not be read.");
        }
    }

    private sealed class CapturingPartialReadStream : Stream
    {
        private readonly byte[] _payload;
        private readonly CancellationTokenSource? _cancelAfterPayload;
        private bool _payloadReturned;

        public CapturingPartialReadStream(
            byte[] payload,
            CancellationTokenSource? cancelAfterPayload = null)
        {
            _payload = payload;
            _cancelAfterPayload = cancelAfterPayload;
        }

        public byte[]? CapturedBuffer { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_payloadReturned)
            {
                return ValueTask.FromResult(0);
            }

            _payloadReturned = true;
            if (!MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment))
            {
                throw new InvalidOperationException("The reader did not provide an array-backed buffer.");
            }

            CapturedBuffer = segment.Array;
            _payload.CopyTo(buffer.Span);
            _cancelAfterPayload?.Cancel();
            return ValueTask.FromResult(_payload.Length);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
