using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

namespace WinARD.Remote.Protocol.Encodings;

public interface IRfbEncodingDecoder
{
    int EncodingId { get; }

    ValueTask<IReadOnlyList<FramebufferRect>> DecodeAsync(
        RfbReader reader,
        FramebufferModel framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken);
}

public sealed record EncodingDecodeResult(
    FramebufferRect? DirtyRect = null,
    RemoteCursor? Cursor = null,
    bool DesktopResized = false);
