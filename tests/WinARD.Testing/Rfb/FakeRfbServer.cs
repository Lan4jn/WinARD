using System.Text;

namespace WinARD.Testing.Rfb;

public sealed class FakeRfbServer : IAsyncDisposable
{
    private const byte Sentinel = 0xa5;
    private readonly ScriptedDuplexStream _stream;

    private FakeRfbServer(
        byte[] serverBanner,
        byte[] expectedClientBanner,
        byte[] securityBytes,
        bool expectsSecuritySelection,
        bool exposesSentinel)
    {
        _stream = new ScriptedDuplexStream(
            serverBanner,
            expectedClientBanner,
            securityBytes,
            expectsSecuritySelection,
            exposesSentinel);
    }

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

    public static FakeRfbServer ForVersion(string banner, uint securityType)
    {
        ArgumentNullException.ThrowIfNull(banner);
        if (banner is not "RFB 003.003\n")
        {
            throw new ArgumentException("The UInt32 security type overload is only valid for RFB 3.3.", nameof(banner));
        }

        return new FakeRfbServer(
            Encoding.ASCII.GetBytes(banner),
            Encoding.ASCII.GetBytes(banner),
            [(byte)(securityType >> 24), (byte)(securityType >> 16), (byte)(securityType >> 8), (byte)securityType],
            expectsSecuritySelection: false,
            exposesSentinel: true);
    }

    public static FakeRfbServer ForVersion(string banner, params byte[] securityTypes)
    {
        ArgumentNullException.ThrowIfNull(banner);
        ArgumentNullException.ThrowIfNull(securityTypes);
        if (banner is not "RFB 003.007\n" and not "RFB 003.008\n" and not "RFB 003.889\n")
        {
            throw new ArgumentException("The byte security type list overload is only valid for RFB 3.7 or 3.8.", nameof(banner));
        }

        if (securityTypes.Length is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(securityTypes), "RFB 3.7 and 3.8 require between one and 255 security types.");
        }

        var securityBytes = new byte[securityTypes.Length + 1];
        securityBytes[0] = (byte)securityTypes.Length;
        securityTypes.CopyTo(securityBytes, 1);
        return new FakeRfbServer(
            Encoding.ASCII.GetBytes(banner),
            Encoding.ASCII.GetBytes(banner is "RFB 003.889\n" ? "RFB 003.008\n" : banner),
            securityBytes,
            expectsSecuritySelection: true,
            exposesSentinel: true);
    }

    public static FakeRfbServer ForBytes(params byte[] serverBytes)
    {
        ArgumentNullException.ThrowIfNull(serverBytes);
        if (serverBytes.Length >= 12)
        {
            var banner = Encoding.ASCII.GetString(serverBytes, 0, 12);
            if (banner is "RFB 003.003\n" or "RFB 003.007\n" or "RFB 003.008\n" or "RFB 003.889\n")
            {
                return new FakeRfbServer(
                    serverBytes[..12],
                    Encoding.ASCII.GetBytes(banner is "RFB 003.889\n" ? "RFB 003.008\n" : banner),
                    serverBytes[12..],
                    expectsSecuritySelection: false,
                    exposesSentinel: false);
            }
        }

        return new FakeRfbServer(serverBytes);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private sealed class ScriptedDuplexStream : Stream
    {
        private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(2);

        private readonly TaskCompletionSource _clientBannerAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _securitySelectionAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] _securityBytes;
        private readonly byte[] _expectedClientBanner;
        private readonly byte[] _serverBanner;
        private readonly byte[] _serverBytes;
        private readonly MemoryStream _written = new();
        private readonly bool _expectsSecuritySelection;
        private readonly bool _exposesSentinel;
        private int _bannerPosition;
        private int _securityPosition;
        private int _selectionPosition;
        private int _sentinelBytesRead;
        private int _serverPosition;
        private Phase _phase;

        public ScriptedDuplexStream(
            byte[] serverBanner,
            byte[] expectedClientBanner,
            byte[] securityBytes,
            bool expectsSecuritySelection,
            bool exposesSentinel)
        {
            _serverBanner = serverBanner;
            _expectedClientBanner = expectedClientBanner;
            _securityBytes = securityBytes;
            _expectsSecuritySelection = expectsSecuritySelection;
            _exposesSentinel = exposesSentinel;
            _serverBytes = Array.Empty<byte>();
            _phase = Phase.Banner;
        }

        public ScriptedDuplexStream(byte[] serverBytes)
        {
            _serverBanner = Array.Empty<byte>();
            _expectedClientBanner = Array.Empty<byte>();
            _securityBytes = Array.Empty<byte>();
            _serverBytes = serverBytes;
            _phase = Phase.Raw;
        }

        public int FlushCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public int BytesRead => _bannerPosition + _securityPosition + _serverPosition + _sentinelBytesRead;

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
            var copy = new byte[buffer.Length];
            var count = ReadAsync(copy, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            copy.AsSpan(0, count).CopyTo(buffer);
            return count;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0)
            {
                return 0;
            }

            while (true)
            {
                switch (_phase)
                {
                    case Phase.Banner:
                        return ReadAvailable(_serverBanner, ref _bannerPosition, buffer, Phase.AwaitClientBanner);
                    case Phase.AwaitClientBanner:
                        await _clientBannerAccepted.Task.WaitAsync(StageTimeout, cancellationToken);
                        ThrowIfDisposed();
                        _phase = Phase.Security;
                        continue;
                    case Phase.Security:
                        return ReadAvailable(
                            _securityBytes,
                            ref _securityPosition,
                            buffer,
                            _expectsSecuritySelection ? Phase.AwaitSecuritySelection : _exposesSentinel ? Phase.Sentinel : Phase.Finished);
                    case Phase.AwaitSecuritySelection:
                        await _securitySelectionAccepted.Task.WaitAsync(StageTimeout, cancellationToken);
                        ThrowIfDisposed();
                        _phase = Phase.Sentinel;
                        continue;
                    case Phase.Sentinel:
                        _phase = Phase.Finished;
                        buffer.Span[0] = Sentinel;
                        _sentinelBytesRead++;
                        return 1;
                    case Phase.Finished:
                        return 0;
                    case Phase.Raw:
                        return ReadAvailable(_serverBytes, ref _serverPosition, buffer, Phase.Finished);
                    default:
                        throw new InvalidOperationException("The fake server reached an unknown protocol stage.");
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfDisposed();
            WriteCore(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            WriteCore(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                IsDisposed = true;
                _clientBannerAccepted.TrySetResult();
                _securitySelectionAccepted.TrySetResult();
                _written.Dispose();
            }

            base.Dispose(disposing);
        }

        private int ReadAvailable(byte[] source, ref int position, Memory<byte> destination, Phase nextPhase)
        {
            var available = source.Length - position;
            if (available == 0)
            {
                _phase = nextPhase;
                return 0;
            }

            var count = Math.Min(available, destination.Length);
            source.AsSpan(position, count).CopyTo(destination.Span);
            position += count;
            if (position == source.Length)
            {
                _phase = nextPhase;
            }

            return count;
        }

        private void WriteCore(ReadOnlySpan<byte> buffer)
        {
            foreach (var value in buffer)
            {
                if (_bannerPosition < _serverBanner.Length)
                {
                    throw new InvalidOperationException("The client attempted to write before reading the complete server banner.");
                }

                if (_serverBanner.Length > 0 && _written.Length < _serverBanner.Length)
                {
                    var index = (int)_written.Length;
                    if (value != _expectedClientBanner[index])
                    {
                        throw new InvalidOperationException("The client wrote an unexpected RFB version banner.");
                    }

                    _written.WriteByte(value);
                    if (_written.Length == _serverBanner.Length)
                    {
                        _clientBannerAccepted.TrySetResult();
                    }

                    continue;
                }

                if (!_expectsSecuritySelection)
                {
                    throw new InvalidOperationException("The client wrote an unexpected security selection.");
                }

                if (_phase != Phase.AwaitSecuritySelection || _selectionPosition != 0 || value != 30)
                {
                    throw new InvalidOperationException("The client wrote an invalid or premature security selection.");
                }

                _written.WriteByte(value);
                _selectionPosition++;
                _securitySelectionAccepted.TrySetResult();
            }
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

        private enum Phase
        {
            Banner,
            AwaitClientBanner,
            Security,
            AwaitSecuritySelection,
            Sentinel,
            Finished,
            Raw,
        }
    }
}
