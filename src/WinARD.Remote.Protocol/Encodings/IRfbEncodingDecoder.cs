using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public interface IRfbEncodingDecoder
{
    RfbEncodingType EncodingType { get; }

    ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        ushort x,
        ushort y,
        ushort width,
        ushort height,
        PixelFormat pixelFormat,
        CancellationToken cancellationToken);
}

public sealed record EncodingDecodeResult(
    FramebufferRect? DirtyRect = null,
    RemoteCursor? Cursor = null,
    bool DesktopResized = false);
