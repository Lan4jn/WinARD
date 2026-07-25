using System.Security.Cryptography;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class RawEncoding : IRfbEncodingDecoder
{
    private readonly PixelFormat _pixelFormat;

    public RawEncoding()
        : this(PixelFormat.WinArdBgra32)
    {
    }

    public RawEncoding(PixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(pixelFormat);
        _pixelFormat = pixelFormat;
    }

    public int EncodingId => (int)RfbEncodingType.Raw;

    public async ValueTask<IReadOnlyList<FramebufferRect>> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        rectangle.ValidateWithin(framebuffer.Width, framebuffer.Height);
        var wireLength = PixelConverter.CheckedWireLength(
            rectangle.Width,
            rectangle.Height,
            _pixelFormat,
            framebuffer.Limits);
        var wirePixels = await reader.ReadBytesAsync(wireLength, cancellationToken);
        byte[]? bgraPixels = null;
        try
        {
            bgraPixels = PixelConverter.ToBgra32(
                wirePixels,
                rectangle.Width,
                rectangle.Height,
                _pixelFormat,
                framebuffer.Limits);
            framebuffer.ApplyRaw(rectangle, bgraPixels);
            return new[] { rectangle };
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
