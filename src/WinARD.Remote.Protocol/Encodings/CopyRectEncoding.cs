using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class CopyRectEncoding : IRfbEncodingDecoder
{
    public int EncodingId => (int)RfbEncodingType.CopyRect;

    public async ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        rectangle.ValidateWithin(framebuffer.Width, framebuffer.Height);
        var copyBytes = checked((long)rectangle.Width * rectangle.Height * 4);
        reader.ReserveFramebufferUpdateWorkBytes(copyBytes);
        reader.ReserveFramebufferUpdateBytes(sizeof(ushort) * 2);
        var sourceX = await reader.ReadUInt16Async(cancellationToken);
        var sourceY = await reader.ReadUInt16Async(cancellationToken);
        framebuffer.CopyRect(rectangle, sourceX, sourceY);
        return new EncodingDecodeResult([rectangle], []);
    }
}
