using System.Text;

namespace WinARD.Testing.Rfb;

public sealed class FakeRfbServer : IAsyncDisposable
{
    private readonly ScriptedDuplexStream _stream;

    private FakeRfbServer(byte[] serverBytes)
    {
        _stream = new ScriptedDuplexStream(serverBytes);
    }

    public Stream ClientStream => _stream;

    public int FlushCount => _stream.FlushCount;

    public bool IsDisposed => _stream.IsDisposed;

    public byte[] ReceivedBytes => _stream.WrittenBytes;

    public int ServerBytesRead => _stream.BytesRead;

    public string ReceivedVersion => Encoding.ASCII.GetString(ReceivedBytes, 0, Math.Min(12, ReceivedBytes.Length));

    public static FakeRfbServer ForVersion(string banner, params byte[] securityTypes)
    {
        ArgumentNullException.ThrowIfNull(banner);
        ArgumentNullException.ThrowIfNull(securityTypes);

        var bytes = new List<byte>(Encoding.ASCII.GetBytes(banner));
        if (banner is "RFB 003.003\n")
        {
            var securityType = securityTypes.Length == 0 ? 0u : securityTypes[0];
            bytes.AddRange([(byte)(securityType >> 24), (byte)(securityType >> 16), (byte)(securityType >> 8), (byte)securityType]);
        }
        else
        {
            bytes.Add((byte)securityTypes.Length);
            bytes.AddRange(securityTypes);
        }

        return new FakeRfbServer(bytes.ToArray());
    }

    public static FakeRfbServer ForBytes(params byte[] serverBytes)
    {
        ArgumentNullException.ThrowIfNull(serverBytes);
        return new FakeRfbServer(serverBytes);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private sealed class ScriptedDuplexStream : Stream
    {
        private readonly byte[] _serverBytes;
        private readonly MemoryStream _written = new();
        private int _position;

        public ScriptedDuplexStream(byte[] serverBytes)
        {
            _serverBytes = serverBytes;
        }

        public int FlushCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public int BytesRead => _position;

        public byte[] WrittenBytes => _written.ToArray();

        public override bool CanRead => !IsDisposed;

        public override bool CanSeek => false;

        public override bool CanWrite => !IsDisposed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            ThrowIfDisposed();
            FlushCount++;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            FlushCount++;
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();
            var bytesToRead = Math.Min(buffer.Length, _serverBytes.Length - _position);
            if (bytesToRead == 0)
            {
                return 0;
            }

            _serverBytes.AsSpan(_position, bytesToRead).CopyTo(buffer);
            _position += bytesToRead;
            return bytesToRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            _written.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            _written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            _written.Dispose();
            base.Dispose(disposing);
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
}
