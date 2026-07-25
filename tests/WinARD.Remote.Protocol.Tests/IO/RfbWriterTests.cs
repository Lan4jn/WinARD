using WinARD.Remote.Protocol.IO;
using WinARD.Remote.Protocol.Errors;
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

    [Fact]
    public async Task Writers_over_the_same_stream_serialize_complete_messages()
    {
        await using var stream = new ConcurrencyDetectingWriteStream();
        var first = new RfbWriter(stream);
        var second = new RfbWriter(stream);

        await Task.WhenAll(
            first.WriteBytesAsync(new byte[] { 1, 1, 1 }, CancellationToken.None).AsTask(),
            second.WriteBytesAsync(new byte[] { 2, 2, 2 }, CancellationToken.None).AsTask());

        Assert.Equal(1, stream.MaximumConcurrentWrites);
        Assert.True(
            stream.Bytes.SequenceEqual(new byte[] { 1, 1, 1, 2, 2, 2 }) ||
            stream.Bytes.SequenceEqual(new byte[] { 2, 2, 2, 1, 1, 1 }));
    }

    [Fact]
    public async Task Partial_write_failure_permanently_poisons_all_writers_for_stream()
    {
        await using var stream = new PartialFailureWriteStream();
        var first = new RfbWriter(stream);
        var second = new RfbWriter(stream);

        await Assert.ThrowsAsync<IOException>(() =>
            first.WriteBytesAsync(new byte[] { 1, 2, 3, 4 }, CancellationToken.None).AsTask());
        var lengthAfterFailure = stream.Bytes.Count;

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            second.WriteBytesAsync(new byte[] { 5, 6 }, CancellationToken.None).AsTask());

        Assert.Contains("faulted", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lengthAfterFailure, stream.Bytes.Count);
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

    private sealed class ConcurrencyDetectingWriteStream : Stream
    {
        private int _activeWrites;

        public List<byte> Bytes { get; } = [];
        public int MaximumConcurrentWrites { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes.Count;
        public override long Position { get => Bytes.Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeWrites);
            MaximumConcurrentWrites = Math.Max(MaximumConcurrentWrites, active);
            try
            {
                await Task.Delay(20, cancellationToken);
                Bytes.AddRange(buffer.ToArray());
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }
        }
    }

    private sealed class PartialFailureWriteStream : Stream
    {
        public List<byte> Bytes { get; } = [];
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes.Count;
        public override long Position { get => Bytes.Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bytes.AddRange(buffer.Span[..Math.Min(2, buffer.Length)].ToArray());
            throw new IOException("Injected partial write failure.");
        }
    }
}
