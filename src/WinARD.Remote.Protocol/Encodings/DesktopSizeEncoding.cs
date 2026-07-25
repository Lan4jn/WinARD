using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class DesktopSizeEncoding : IRfbEncodingDecoder
{
    public RfbEncodingType EncodingType => RfbEncodingType.DesktopSize;

    public ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        ushort x,
        ushort y,
        ushort width,
        ushort height,
        PixelFormat pixelFormat,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (x != 0 || y != 0)
        {
            throw new RfbProtocolException("DesktopSize rectangle origin must be (0,0).");
        }

        framebuffer.Resize(width, height);
        return ValueTask.FromResult(new EncodingDecodeResult(new FramebufferRect(0, 0, width, height), DesktopResized: true));
    }
}
