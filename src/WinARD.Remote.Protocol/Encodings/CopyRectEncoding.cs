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
        var result = await DecodeWithContextAsync(
            reader,
            framebuffer,
            checked((ushort)rectangle.X),
            checked((ushort)rectangle.Y),
            checked((ushort)rectangle.Width),
            checked((ushort)rectangle.Height),
            PixelFormat.WinArdBgra32,
            cancellationToken);
        return result.DirtyRect is { } dirtyRect ? new[] { dirtyRect } : Array.Empty<FramebufferRect>();
    }

    internal static async ValueTask<EncodingDecodeResult> DecodeWithContextAsync(
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
