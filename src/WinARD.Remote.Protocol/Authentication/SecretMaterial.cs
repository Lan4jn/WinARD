using System.Security.Cryptography;
using System.Text;

namespace WinARD.Remote.Protocol.Authentication;

public sealed class SecretMaterial : ISecretMaterial
{
    private readonly object _sync = new();
    private readonly byte[] _bytes;
    private bool _disposed;

    private SecretMaterial(byte[] bytes)
    {
        _bytes = bytes;
    }

    public int Length
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return _bytes.Length;
            }
        }
    }

    public static SecretMaterial FromBytes(ReadOnlySpan<byte> value) => new(value.ToArray());

    public static SecretMaterial FromUtf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return FromUtf8(value.AsSpan());
    }

    public static SecretMaterial FromUtf8(ReadOnlySpan<char> value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value)];
        try
        {
            _ = Encoding.UTF8.GetBytes(value, bytes);
            return FromBytes(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void CopyTo(Span<byte> destination)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (destination.Length < _bytes.Length)
            {
                throw new ArgumentException("The destination is too small for the secret material.", nameof(destination));
            }

            _bytes.CopyTo(destination);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_bytes);
            _disposed = true;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
