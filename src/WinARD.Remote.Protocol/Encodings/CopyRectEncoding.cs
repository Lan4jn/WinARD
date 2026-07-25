using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class CopyRectEncoding : IRfbEncodingDecoder
{
    public RfbEncodingType EncodingType => RfbEncodingType.CopyRect;

    public async ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        ushort x,
        ushort y,
        ushort width,
        ushort height,
        PixelFormat pixelFormat,
        CancellationToken cancellationToken)
    {
        var destination = new FramebufferRect(x, y, width, height);
        destination.ValidateWithin(framebuffer.Width, framebuffer.Height);
        var sourceX = await reader.ReadUInt16Async(cancellationToken);
        var sourceY = await reader.ReadUInt16Async(cancellationToken);
        framebuffer.CopyRect(destination, sourceX, sourceY);
        return new EncodingDecodeResult(destination);
    }
}
