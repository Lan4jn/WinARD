using WinARD.Remote.Protocol.IO;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.IO;

public sealed class RfbWriterTests
{
    [Fact]
    public async Task Integer_writes_use_big_endian_order()
    {
        await using var stream = new MemoryStream();
        var writer = new RfbWriter(stream);

        await writer.WriteByteAsync(0xA5, CancellationToken.None);
        await writer.WriteUInt16Async(0x1234, CancellationToken.None);
        await writer.WriteUInt32Async(0x01020304, CancellationToken.None);

        Assert.Equal([0xA5, 0x12, 0x34, 0x01, 0x02, 0x03, 0x04], stream.ToArray());
    }

    [Fact]
    public async Task WriteInt32_writes_signed_big_endian_value()
    {
        await using var stream = new MemoryStream();
        var writer = new RfbWriter(stream);

        await writer.WriteInt32Async(-239, CancellationToken.None);

        Assert.Equal([0xFF, 0xFF, 0xFF, 0x11], stream.ToArray());
    }

    [Fact]
    public async Task WriteBytes_appends_value_unchanged()
    {
        await using var stream = new MemoryStream();
        stream.WriteByte(0x01);
        var writer = new RfbWriter(stream);

        await writer.WriteBytesAsync(new byte[] { 0x02, 0x03 }, CancellationToken.None);

        Assert.Equal([0x01, 0x02, 0x03], stream.ToArray());
    }

    [Fact]
    public async Task Writes_do_not_flush_stream()
    {
        await using var stream = new FlushCountingStream();
        var writer = new RfbWriter(stream);

        await writer.WriteByteAsync(1, CancellationToken.None);

        Assert.Equal(0, stream.FlushCount);
    }

    [Fact]
    public async Task Write_propagates_cancellation()
    {
        var writer = new RfbWriter(new CancellationAwareWriteStream());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.WriteByteAsync(1, cancellation.Token).AsTask());
    }

    [Fact]
    public void Constructor_rejects_null_stream()
    {
        Assert.Throws<ArgumentNullException>(() => new RfbWriter(null!));
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int FlushCount { get; private set; }

        public override void Flush() => FlushCount++;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CancellationAwareWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
