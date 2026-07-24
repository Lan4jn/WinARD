using System.Security.Cryptography;

namespace WinARD.Remote.Protocol.Authentication;

public sealed class CryptoRandomSource : IRandomSource
{
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}
