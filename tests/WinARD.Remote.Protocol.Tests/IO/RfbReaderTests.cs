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
    public async Task ReadBytes_wraps_premature_end_of_stream()
    {
        await using var stream = new ChunkedReadStream([0x01], 1);
        var reader = new RfbReader(stream, ProtocolLimits.Default);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            reader.ReadBytesAsync(2, CancellationToken.None).AsTask());

        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
        Assert.IsType<EndOfStreamException>(exception.InnerException);
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
}
