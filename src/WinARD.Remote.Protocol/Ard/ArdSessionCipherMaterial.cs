using System.Security.Cryptography;

namespace WinARD.Remote.Protocol.Ard;

internal sealed class ArdSessionCipherMaterial : IDisposable
{
    private byte[]? _key;
    private byte[]? _initialIv;

    public ArdSessionCipherMaterial(byte[] key, byte[] initialIv)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(initialIv);
        if (key.Length != 16)
        {
            throw new ArgumentException("The ARD session key must be 16 bytes.", nameof(key));
        }

        if (initialIv.Length != 16)
        {
            throw new ArgumentException("The ARD session IV must be 16 bytes.", nameof(initialIv));
        }

        _key = key;
        _initialIv = initialIv;
    }

    internal byte[] Key => _key ?? throw new ObjectDisposedException(nameof(ArdSessionCipherMaterial));

    internal byte[] InitialIv =>
        _initialIv ?? throw new ObjectDisposedException(nameof(ArdSessionCipherMaterial));

    public void Dispose()
    {
        var key = Interlocked.Exchange(ref _key, null);
        var initialIv = Interlocked.Exchange(ref _initialIv, null);
        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        if (initialIv is not null)
        {
            CryptographicOperations.ZeroMemory(initialIv);
        }
    }
}
