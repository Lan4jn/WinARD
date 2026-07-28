using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.Framebuffer;

namespace WinARD.ProtocolProbe;

public sealed record ProbeResult(
    RfbVersion Version,
    RfbSecurityType SecurityType,
    ProbeCapture? Capture = null,
    ProbePointerSmoke? PointerSmoke = null);

public sealed record ProbePointerSmoke(int Width, int Height, int X, int Y);

public sealed class ProbeCapture
{
    public ProbeCapture(
        string path,
        int width,
        int height,
        IEnumerable<FramebufferRect> dirtyRects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(dirtyRects);
        Path = path;
        Width = width;
        Height = height;
        DirtyRects = Array.AsReadOnly(dirtyRects.ToArray());
    }

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<FramebufferRect> DirtyRects { get; }
}
