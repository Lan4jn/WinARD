using System.Security.Cryptography;
using WinARD.Application.Ports;

namespace WinARD.Security.Secrets;

public sealed class SecretBuffer : ISecret
{
    private readonly object _sync = new();
    private byte[]? _bytes;

    private SecretBuffer(byte[] bytes)
    {
        _bytes = bytes;
    }

    public int Length
    {
        get
        {
            lock (_sync)
            {
                return Bytes.Length;
            }
        }
    }

    public static SecretBuffer CopyFrom(ReadOnlySpan<byte> source) =>
        new(source.ToArray());

    public void CopyTo(Span<byte> destination)
    {
        lock (_sync)
        {
            var bytes = Bytes;
            if (destination.Length < bytes.Length)
            {
                throw new ArgumentException(
                    "Destination is smaller than the secret.",
                    nameof(destination));
            }

            bytes.CopyTo(destination);
        }
    }

    public SecretBuffer Clone()
    {
        lock (_sync)
        {
            return new SecretBuffer(Bytes.ToArray());
        }
    }

    ISecret ISecret.Clone() => Clone();

    public void Dispose()
    {
        lock (_sync)
        {
            if (_bytes is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_bytes);
            _bytes = null;
        }
    }

    public override string ToString() => nameof(SecretBuffer);

    private byte[] Bytes => _bytes ??
        throw new ObjectDisposedException(nameof(SecretBuffer));
}
