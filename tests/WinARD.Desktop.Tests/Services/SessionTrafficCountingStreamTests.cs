using WinARD.Desktop.Services;
using Xunit;

#pragma warning disable CA1707, CA1835, CA2022, CA2215

namespace WinARD.Desktop.Tests.Services;

public sealed class SessionTrafficCountingStreamTests
{
    [Fact]
    public async Task Counts_read_and_written_bytes_without_copying_payload()
    {
        await using var inner = CreateExpandableStream([1, 2, 3, 4]);
        await using var stream = new SessionTrafficCountingStream(inner, leaveOpen: true);
        var buffer = new byte[3];

        Assert.Equal(3, await stream.ReadAsync(buffer));
        await stream.WriteAsync(new byte[] { 5, 6 });

        Assert.Equal(3, stream.BytesRead);
        Assert.Equal(2, stream.BytesWritten);
    }

    [Fact]
    public void Counts_synchronous_array_span_and_single_byte_operations()
    {
        using var inner = CreateExpandableStream([1, 2, 3, 4]);
        using var stream = new SessionTrafficCountingStream(inner, leaveOpen: true);
        var array = new byte[2];
        Span<byte> span = stackalloc byte[1];

        Assert.Equal(2, stream.Read(array, 0, array.Length));
        Assert.Equal(1, stream.Read(span));
        Assert.Equal(4, stream.ReadByte());
        stream.Write([5, 6], 0, 2);
        stream.Write(new ReadOnlySpan<byte>([7, 8]));
        stream.WriteByte(9);

        Assert.Equal(4, stream.BytesRead);
        Assert.Equal(5, stream.BytesWritten);
    }

    [Fact]
    public async Task Counts_asynchronous_array_and_memory_operations()
    {
        await using var inner = CreateExpandableStream([1, 2, 3, 4]);
        await using var stream = new SessionTrafficCountingStream(inner, leaveOpen: true);
        var array = new byte[2];
        var memory = new byte[2].AsMemory();

        Assert.Equal(2, await stream.ReadAsync(array, 0, array.Length, CancellationToken.None));
        Assert.Equal(2, await stream.ReadAsync(memory, CancellationToken.None));
        await stream.WriteAsync(new byte[] { 5, 6 }, 0, 2, CancellationToken.None);
        await stream.WriteAsync(new byte[] { 7, 8 }.AsMemory(), CancellationToken.None);

        Assert.Equal(4, stream.BytesRead);
        Assert.Equal(4, stream.BytesWritten);
    }

    [Fact]
    public async Task Delegates_capabilities_position_length_seek_flush_and_set_length()
    {
        await using var inner = new MemoryStream(new byte[8], writable: true);
        await using var stream = new SessionTrafficCountingStream(inner, leaveOpen: true);

        Assert.Equal(inner.CanRead, stream.CanRead);
        Assert.Equal(inner.CanSeek, stream.CanSeek);
        Assert.Equal(inner.CanWrite, stream.CanWrite);
        Assert.Equal(inner.CanTimeout, stream.CanTimeout);
        Assert.Equal(8, stream.Length);
        stream.Position = 3;
        Assert.Equal(3, inner.Position);
        Assert.Equal(1, stream.Seek(-2, SeekOrigin.Current));
        stream.SetLength(5);
        Assert.Equal(5, inner.Length);
        stream.Flush();
        await stream.FlushAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Failed_and_cancelled_operations_do_not_increment_counters()
    {
        await using var inner = new FailingStream();
        await using var stream = new SessionTrafficCountingStream(inner, leaveOpen: true);
        var buffer = new byte[1];

        Assert.Throws<IOException>(() => stream.Read(buffer, 0, 1));
        Assert.Throws<IOException>(() => stream.Write(buffer, 0, 1));
        await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(buffer, 0, 1));
        await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(buffer, 0, 1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await stream.ReadAsync(buffer.AsMemory(), new CancellationToken(canceled: true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await stream.WriteAsync(buffer.AsMemory(), new CancellationToken(canceled: true)));

        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(0, stream.BytesWritten);
    }

    [Fact]
    public void Concurrent_operations_accumulate_without_lost_updates()
    {
        using var inner = new ConcurrentProbeStream();
        using var stream = new SessionTrafficCountingStream(inner, leaveOpen: true);

        Parallel.For(0, 10_000, _ =>
        {
            Assert.Equal(1, stream.ReadByte());
            stream.WriteByte(1);
        });

        Assert.Equal(10_000, stream.BytesRead);
        Assert.Equal(10_000, stream.BytesWritten);
    }

    [Fact]
    public async Task Leave_open_preserves_inner_for_sync_and_async_disposal()
    {
        var inner = new TrackingStream();
        var stream = new SessionTrafficCountingStream(inner, leaveOpen: true);

        await stream.DisposeAsync();
        stream.Dispose();

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(1));
        Assert.Equal(0, inner.DisposeCount);
        Assert.Equal(0, inner.DisposeAsyncCount);
        inner.WriteByte(1);
        inner.Dispose();
    }

    [Fact]
    public async Task Owned_inner_is_disposed_once_even_when_wrapper_disposal_repeats()
    {
        var inner = new TrackingStream();
        var stream = new SessionTrafficCountingStream(inner, leaveOpen: false);

        await stream.DisposeAsync();
        stream.Dispose();
        await stream.DisposeAsync();

        Assert.Equal(0, inner.DisposeCount);
        Assert.Equal(1, inner.DisposeAsyncCount);
    }

    [Fact]
    public void Constructor_rejects_null_inner_stream()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionTrafficCountingStream(null!, leaveOpen: false));
    }

    [Fact]
    public async Task Counters_saturate_at_long_max_value_for_all_sync_and_async_paths()
    {
        await using var inner = CreateExpandableStream([1, 2, 3, 4]);
        await using var stream = new SessionTrafficCountingStream(
            inner,
            leaveOpen: true,
            initialBytesRead: long.MaxValue - 2,
            initialBytesWritten: long.MaxValue - 2);
        var oneByte = new byte[1];

        Assert.Equal(1, stream.Read(oneByte, 0, 1));
        Assert.Equal(2, stream.ReadByte());
        Assert.Equal(1, await stream.ReadAsync(oneByte.AsMemory()));
        stream.Write(oneByte, 0, 1);
        stream.WriteByte(1);
        await stream.WriteAsync(oneByte.AsMemory());

        Assert.Equal(long.MaxValue, stream.BytesRead);
        Assert.Equal(long.MaxValue, stream.BytesWritten);
    }

    private static MemoryStream CreateExpandableStream(byte[] content)
    {
        var stream = new MemoryStream();
        stream.Write(content);
        stream.Position = 0;
        return stream;
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new IOException("Injected flush failure.");
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Injected read failure.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled<int>(cancellationToken)
                : ValueTask.FromException<int>(new IOException("Injected read failure."));
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Injected write failure.");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled(cancellationToken)
                : ValueTask.FromException(new IOException("Injected write failure."));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ConcurrentProbeStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0)
            {
                return 0;
            }

            buffer[offset] = 1;
            return 1;
        }
        public override int ReadByte() => 1;
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void WriteByte(byte value) { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class TrackingStream : MemoryStream
    {
        private bool _disposed;

        public int DisposeCount { get; private set; }
        public int DisposeAsyncCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                DisposeCount++;
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                DisposeAsyncCount++;
            }

            return ValueTask.CompletedTask;
        }
    }
}
