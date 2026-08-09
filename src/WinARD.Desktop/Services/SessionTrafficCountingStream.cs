namespace WinARD.Desktop.Services;

internal sealed class SessionTrafficCountingStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private long _bytesRead;
    private long _bytesWritten;
    private int _disposed;

    public SessionTrafficCountingStream(
        Stream inner,
        bool leaveOpen,
        long initialBytesRead = 0,
        long initialBytesWritten = 0)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(initialBytesRead);
        ArgumentOutOfRangeException.ThrowIfNegative(initialBytesWritten);
        _inner = inner;
        _leaveOpen = leaveOpen;
        _bytesRead = initialBytesRead;
        _bytesWritten = initialBytesWritten;
    }

    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public override bool CanRead => !IsDisposed && _inner.CanRead;
    public override bool CanSeek => !IsDisposed && _inner.CanSeek;
    public override bool CanTimeout => !IsDisposed && _inner.CanTimeout;
    public override bool CanWrite => !IsDisposed && _inner.CanWrite;
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _inner.Length;
        }
    }
    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _inner.Position;
        }
        set
        {
            ThrowIfDisposed();
            _inner.Position = value;
        }
    }
    public override int ReadTimeout
    {
        get
        {
            ThrowIfDisposed();
            return _inner.ReadTimeout;
        }
        set
        {
            ThrowIfDisposed();
            _inner.ReadTimeout = value;
        }
    }
    public override int WriteTimeout
    {
        get
        {
            ThrowIfDisposed();
            return _inner.WriteTimeout;
        }
        set
        {
            ThrowIfDisposed();
            _inner.WriteTimeout = value;
        }
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        _inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        var read = _inner.Read(buffer, offset, count);
        AddReadBytes(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        var read = _inner.Read(buffer);
        AddReadBytes(read);
        return read;
    }

    public override int ReadByte()
    {
        ThrowIfDisposed();
        var value = _inner.ReadByte();
        if (value >= 0)
        {
            SaturatingAdd(ref _bytesRead, 1);
        }

        return value;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        AddReadBytes(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        AddReadBytes(read);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        _inner.Write(buffer, offset, count);
        AddWrittenBytes(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        _inner.Write(buffer);
        AddWrittenBytes(buffer.Length);
    }

    public override void WriteByte(byte value)
    {
        ThrowIfDisposed();
        _inner.WriteByte(value);
        SaturatingAdd(ref _bytesWritten, 1);
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        AddWrittenBytes(count);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        AddWrittenBytes(buffer.Length);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        return _inner.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        ThrowIfDisposed();
        _inner.SetLength(value);
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0 && !_leaveOpen)
            {
                _inner.Dispose();
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && !_leaveOpen)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private void AddReadBytes(int count)
    {
        if (count != 0)
        {
            SaturatingAdd(ref _bytesRead, count);
        }
    }

    private void AddWrittenBytes(int count)
    {
        if (count != 0)
        {
            SaturatingAdd(ref _bytesWritten, count);
        }
    }

    private static void SaturatingAdd(ref long target, int count)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            var updated = current > long.MaxValue - count
                ? long.MaxValue
                : current + count;
            if (Interlocked.CompareExchange(ref target, updated, current) == current)
            {
                return;
            }
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
