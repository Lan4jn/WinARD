using System.Security.Cryptography;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class RawEncoding : IRfbEncodingDecoder
{
    public int EncodingId => (int)RfbEncodingType.Raw;

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
        var rectangle = new FramebufferRect(x, y, width, height);
        rectangle.ValidateWithin(framebuffer.Width, framebuffer.Height);
        var wireLength = PixelConverter.CheckedWireLength(width, height, pixelFormat, framebuffer.Limits);
        var wirePixels = await reader.ReadBytesAsync(wireLength, cancellationToken);
        byte[]? bgraPixels = null;
        try
        {
            bgraPixels = PixelConverter.ToBgra32(wirePixels, width, height, pixelFormat, framebuffer.Limits);
            framebuffer.ApplyRaw(rectangle, bgraPixels);
            return new EncodingDecodeResult(rectangle);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wirePixels);
            if (bgraPixels is not null)
            {
                CryptographicOperations.ZeroMemory(bgraPixels);
            }
        }
    }
}
