using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class CopyRectEncoding : IRfbEncodingDecoder
{
    public int EncodingId => (int)RfbEncodingType.CopyRect;

    public async ValueTask<IReadOnlyList<FramebufferRect>> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        rectangle.ValidateWithin(framebuffer.Width, framebuffer.Height);
        reader.ReserveFramebufferUpdateBytes(sizeof(ushort) * 2);
        var sourceX = await reader.ReadUInt16Async(cancellationToken);
        var sourceY = await reader.ReadUInt16Async(cancellationToken);
        framebuffer.CopyRect(rectangle, sourceX, sourceY);
        return new[] { rectangle };
    }
}
