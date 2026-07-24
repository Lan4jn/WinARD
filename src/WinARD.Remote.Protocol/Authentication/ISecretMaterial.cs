namespace WinARD.Remote.Protocol.Authentication;

public interface ISecretMaterial : IDisposable
{
    int Length { get; }

    void CopyTo(Span<byte> destination);
}
