using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WinARD.Testing.Rfb;

public sealed class ArdServerFixture : IAsyncDisposable
{
    private const string ModulusHex =
        "D2652EF10104A3DDC1219700EDFBD1E19F7678B4A4F6D5952634BD8BF1D60326322B5D32366DC25CB4E8E73AF4312A70D2DCAF2747EB89D7E88553EECD6A283D";

    private static readonly BigInteger ServerPrivateExponent = new(3);

    private readonly byte[] _modulus;
    private readonly ScriptedAuthenticationStream _stream;

    private ArdServerFixture(
        byte[] challengeBytes,
        byte[] resultBytes,
        byte[] modulus,
        int expectedResponseLength,
        int maxReadChunk)
    {
        _modulus = modulus;
        _stream = new ScriptedAuthenticationStream(
            challengeBytes,
            resultBytes,
            expectedResponseLength,
            maxReadChunk);
    }

    private ArdServerFixture(byte[] serverBytes, int maxReadChunk)
    {
        _modulus = Array.Empty<byte>();
        _stream = new ScriptedAuthenticationStream(serverBytes, maxReadChunk);
    }

    public Stream ClientStream => _stream;

    public int FlushCount => _stream.FlushCount;

    public bool IsDisposed => _stream.IsDisposed;

    public int ServerBytesRead => _stream.BytesRead;

    public byte[] ReceivedBytes => _stream.WrittenBytes;

    public byte[] Modulus => (byte[])_modulus.Clone();

    public int KeyLength => _modulus.Length;

    public static ArdServerFixture Create(
        uint securityResult = 0,
        byte[]? reasonBytes = null,
        uint? declaredReasonLength = null,
        int maxReadChunk = int.MaxValue)
    {
        var modulus = Convert.FromHexString(ModulusHex);
        var generator = new BigInteger(5);
        var prime = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var serverPublicKey = ToFixedWidth(BigInteger.ModPow(generator, ServerPrivateExponent, prime), modulus.Length);

        var challengeBytes = new List<byte>(4 + (2 * modulus.Length));
        AddUInt16(challengeBytes, 5);
        AddUInt16(challengeBytes, checked((ushort)modulus.Length));
        challengeBytes.AddRange(modulus);
        challengeBytes.AddRange(serverPublicKey);

        var resultBytes = new List<byte>(sizeof(uint) + (reasonBytes?.Length ?? 0) + sizeof(uint));
        AddUInt32(resultBytes, securityResult);
        if (reasonBytes is not null)
        {
            AddUInt32(resultBytes, declaredReasonLength ?? checked((uint)reasonBytes.Length));
            resultBytes.AddRange(reasonBytes);
        }

        return new ArdServerFixture(
            challengeBytes.ToArray(),
            resultBytes.ToArray(),
            modulus,
            128 + modulus.Length,
            maxReadChunk);
    }

    public static ArdServerFixture ForServerBytes(byte[] serverBytes, int maxReadChunk = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(serverBytes);
        return new ArdServerFixture((byte[])serverBytes.Clone(), maxReadChunk);
    }

    public byte[] GetClientPublicKey()
    {
        EnsureCompleteResponse();
        return ReceivedBytes.AsSpan(128, KeyLength).ToArray();
    }

    public byte[] GetSharedSecret()
    {
        var clientPublicKey = new BigInteger(GetClientPublicKey(), isUnsigned: true, isBigEndian: true);
        var prime = new BigInteger(_modulus, isUnsigned: true, isBigEndian: true);
        return ToFixedWidth(BigInteger.ModPow(clientPublicKey, ServerPrivateExponent, prime), KeyLength);
    }

    public (string Username, string Password) DecryptCredentials()
    {
        EnsureCompleteResponse();
        var sharedSecret = GetSharedSecret();
        Span<byte> aesKey = stackalloc byte[16];
        Span<byte> plaintext = stackalloc byte[128];

        try
        {
#pragma warning disable CA5351 // MD5 is mandated by ARD security type 30 compatibility.
            _ = MD5.HashData(sharedSecret, aesKey);
#pragma warning restore CA5351
            DecryptCredentials(ReceivedBytes.AsSpan(0, 128), aesKey, plaintext);
            return (ReadNullTerminatedUtf8(plaintext[..64]), ReadNullTerminatedUtf8(plaintext[64..]));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(aesKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    public static byte[] EncodeChallenge(ushort generator, ushort keyLength, byte[] modulus, byte[] serverPublicKey)
    {
        ArgumentNullException.ThrowIfNull(modulus);
        ArgumentNullException.ThrowIfNull(serverPublicKey);

        var bytes = new List<byte>(4 + modulus.Length + serverPublicKey.Length);
        AddUInt16(bytes, generator);
        AddUInt16(bytes, keyLength);
        bytes.AddRange(modulus);
        bytes.AddRange(serverPublicKey);
        return bytes.ToArray();
    }

    private static void AddUInt16(List<byte> destination, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        destination.AddRange(bytes.ToArray());
    }

    private static void AddUInt32(List<byte> destination, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        destination.AddRange(bytes.ToArray());
    }

#pragma warning disable CA5358 // AES-ECB is mandated by ARD security type 30 compatibility.
    private static void DecryptCredentials(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key, Span<byte> plaintext)
    {
        var keyBytes = key.ToArray();
        using var aes = Aes.Create();
        try
        {
            aes.Key = keyBytes;
            _ = aes.DecryptEcb(ciphertext, plaintext, PaddingMode.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }
#pragma warning restore CA5358

    private static string ReadNullTerminatedUtf8(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidOperationException("The client credential field was not NUL-terminated.");
        }

        return Encoding.UTF8.GetString(field[..terminator]);
    }

    private static byte[] ToFixedWidth(BigInteger value, int width)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > width)
        {
            throw new InvalidOperationException("The fixture value exceeds the negotiated DH width.");
        }

        var fixedWidth = new byte[width];
        bytes.CopyTo(fixedWidth, width - bytes.Length);
        return fixedWidth;
    }

    private void EnsureCompleteResponse()
    {
        if (_modulus.Length == 0 || ReceivedBytes.Length != 128 + KeyLength)
        {
            throw new InvalidOperationException("The client did not send a complete ARD response.");
        }
    }

    private sealed class ScriptedAuthenticationStream : Stream
    {
        private static readonly TimeSpan StageTimeout = TimeSpan.FromMilliseconds(500);

        private readonly byte[] _challengeBytes;
        private readonly int _expectedResponseLength;
        private readonly int _maxReadChunk;
        private readonly TaskCompletionSource _responseAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] _resultBytes;
        private readonly byte[] _rawServerBytes;
        private readonly MemoryStream _written = new();
        private int _challengePosition;
        private int _rawPosition;
        private int _resultPosition;
        private Phase _phase;

        public ScriptedAuthenticationStream(
            byte[] challengeBytes,
            byte[] resultBytes,
            int expectedResponseLength,
            int maxReadChunk)
        {
            ArgumentNullException.ThrowIfNull(challengeBytes);
            ArgumentNullException.ThrowIfNull(resultBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedResponseLength);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReadChunk);
            _challengeBytes = challengeBytes;
            _resultBytes = resultBytes;
            _expectedResponseLength = expectedResponseLength;
            _maxReadChunk = maxReadChunk;
            _rawServerBytes = Array.Empty<byte>();
            _phase = Phase.Challenge;
        }

        public ScriptedAuthenticationStream(byte[] serverBytes, int maxReadChunk)
        {
            ArgumentNullException.ThrowIfNull(serverBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReadChunk);
            _challengeBytes = Array.Empty<byte>();
            _resultBytes = Array.Empty<byte>();
            _rawServerBytes = serverBytes;
            _maxReadChunk = maxReadChunk;
            _phase = Phase.Raw;
        }

        public int FlushCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public int BytesRead => _challengePosition + _resultPosition + _rawPosition;

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
            if (count != 0)
            {
                copy.AsSpan(0, count).CopyTo(buffer);
            }

            return count;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
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
                    case Phase.Challenge:
                        return ReadAvailable(
                            _challengeBytes,
                            ref _challengePosition,
                            buffer,
                            Phase.AwaitResponse);
                    case Phase.AwaitResponse:
                        try
                        {
                            await _responseAccepted.Task.WaitAsync(StageTimeout, cancellationToken);
                        }
                        catch (TimeoutException exception)
                        {
                            throw new TimeoutException(
                                "The client attempted to read the ARD SecurityResult before sending a complete response.",
                                exception);
                        }

                        ThrowIfDisposed();
                        _phase = Phase.Result;
                        continue;
                    case Phase.Result:
                        return ReadAvailable(_resultBytes, ref _resultPosition, buffer, Phase.Finished);
                    case Phase.Finished:
                        return 0;
                    case Phase.Raw:
                        return ReadAvailable(_rawServerBytes, ref _rawPosition, buffer, Phase.Finished);
                    default:
                        throw new InvalidOperationException("The ARD fixture reached an unknown protocol stage.");
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfDisposed();
            if (_phase == Phase.Raw)
            {
                _written.Write(buffer);
                return;
            }

            if (_phase != Phase.AwaitResponse)
            {
                throw new InvalidOperationException("The client wrote an ARD response before reading the complete challenge.");
            }

            if (_written.Length + buffer.Length > _expectedResponseLength)
            {
                throw new InvalidOperationException("The client ARD response exceeded the negotiated response length.");
            }

            _written.Write(buffer);
            if (_written.Length == _expectedResponseLength)
            {
                _responseAccepted.TrySetResult();
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                IsDisposed = true;
                _responseAccepted.TrySetResult();
                _written.Dispose();
            }

            base.Dispose(disposing);
        }

        private int ReadAvailable(byte[] source, ref int position, Memory<byte> destination, Phase nextPhase)
        {
            var count = Math.Min(Math.Min(destination.Length, _maxReadChunk), source.Length - position);
            if (count == 0)
            {
                _phase = nextPhase;
                return 0;
            }

            source.AsSpan(position, count).CopyTo(destination.Span);
            position += count;
            if (position == source.Length)
            {
                _phase = nextPhase;
            }

            return count;
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

        private enum Phase
        {
            Challenge,
            AwaitResponse,
            Result,
            Finished,
            Raw,
        }
    }
}
