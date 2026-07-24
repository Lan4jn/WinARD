namespace WinARD.Testing.Streams;

public sealed class ChunkedReadStream : Stream
{
    private readonly byte[] _buffer;
    private readonly int _chunkSize;
    private int _position;

    public ChunkedReadStream(byte[] buffer, int chunkSize)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);

        _buffer = buffer;
        _chunkSize = chunkSize;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _buffer.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var bytesToRead = Math.Min(Math.Min(buffer.Length, _chunkSize), _buffer.Length - _position);
        if (bytesToRead == 0)
        {
            return 0;
        }

        _buffer.AsSpan(_position, bytesToRead).CopyTo(buffer);
        _position += bytesToRead;
        return bytesToRead;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
