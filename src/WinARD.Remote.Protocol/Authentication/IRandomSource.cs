namespace WinARD.Remote.Protocol.Authentication;

public interface IRandomSource
{
    void Fill(Span<byte> destination);
}
