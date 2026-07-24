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

    private ArdServerFixture(byte[] serverBytes, byte[] modulus, int maxReadChunk)
    {
        _modulus = modulus;
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
        int maxReadChunk = int.MaxValue)
    {
        var modulus = Convert.FromHexString(ModulusHex);
        var generator = new BigInteger(5);
        var prime = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var serverPublicKey = ToFixedWidth(BigInteger.ModPow(generator, ServerPrivateExponent, prime), modulus.Length);

        var serverBytes = new List<byte>(4 + (2 * modulus.Length) + sizeof(uint) + (reasonBytes?.Length ?? 0) + sizeof(uint));
        AddUInt16(serverBytes, 5);
        AddUInt16(serverBytes, checked((ushort)modulus.Length));
        serverBytes.AddRange(modulus);
        serverBytes.AddRange(serverPublicKey);
        AddUInt32(serverBytes, securityResult);
        if (reasonBytes is not null)
        {
            AddUInt32(serverBytes, checked((uint)reasonBytes.Length));
            serverBytes.AddRange(reasonBytes);
        }

        return new ArdServerFixture(serverBytes.ToArray(), modulus, maxReadChunk);
    }

    public static ArdServerFixture ForServerBytes(byte[] serverBytes, int maxReadChunk = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(serverBytes);
        return new ArdServerFixture((byte[])serverBytes.Clone(), Array.Empty<byte>(), maxReadChunk);
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
        private readonly int _maxReadChunk;
        private readonly byte[] _serverBytes;
        private readonly MemoryStream _written = new();
        private int _position;

        public ScriptedAuthenticationStream(byte[] serverBytes, int maxReadChunk)
        {
            ArgumentNullException.ThrowIfNull(serverBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReadChunk);
            _serverBytes = serverBytes;
            _maxReadChunk = maxReadChunk;
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
            var count = Math.Min(Math.Min(buffer.Length, _maxReadChunk), _serverBytes.Length - _position);
            if (count == 0)
            {
                return 0;
            }

            _serverBytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfDisposed();
            _written.Write(buffer);
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
                _written.Dispose();
            }

            base.Dispose(disposing);
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
}
