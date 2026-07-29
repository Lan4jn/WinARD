using System.Security.Cryptography;

namespace WinARD.Remote.Protocol.Authentication;

public sealed class ArdAuthenticationResult : IDisposable
{
    private readonly object _sync = new();
    private byte[]? _key;

    internal ArdAuthenticationResult(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 16)
        {
            throw new ArgumentException("The ARD authentication key must be 16 bytes.", nameof(key));
        }

        _key = key;
    }

    internal void DecryptEcb(ReadOnlySpan<byte> ciphertext, Span<byte> plaintext)
    {
        if (ciphertext.IsEmpty || ciphertext.Length % 16 != 0)
        {
            throw new ArgumentException("ARD AES-ECB ciphertext must contain complete 16-byte blocks.", nameof(ciphertext));
        }

        if (plaintext.Length < ciphertext.Length)
        {
            throw new ArgumentException("The plaintext destination is too small.", nameof(plaintext));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
#pragma warning disable CA5358 // AES-ECB is required by the Apple Remote Desktop protocol.
            using var aes = Aes.Create();
            aes.Key = _key;
            _ = aes.DecryptEcb(ciphertext, plaintext, PaddingMode.None);
#pragma warning restore CA5358
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_key is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
