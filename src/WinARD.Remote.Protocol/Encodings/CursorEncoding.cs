using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class CursorEncoding : IRfbEncodingDecoder
{
    public RfbEncodingType EncodingType => RfbEncodingType.Cursor;

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
        if (width == 0 || height == 0)
        {
            if (width != 0 || height != 0)
            {
                throw new RfbProtocolException("An empty cursor must have both width and height set to zero.");
            }

            return new EncodingDecodeResult(Cursor: new RemoteCursor(x, y, 0, 0, []));
        }

        if (x >= width || y >= height)
        {
            throw new RfbProtocolException("Cursor hotspot lies outside the cursor image.");
        }

        var pixelLength = PixelConverter.CheckedWireLength(width, height, pixelFormat, framebuffer.Limits);
        int maskStride;
        int maskLength;
        try
        {
            maskStride = checked((width + 7) / 8);
            maskLength = checked(maskStride * height);
        }
        catch (OverflowException exception)
        {
            throw new RfbProtocolException("Cursor mask length overflowed.", exception);
        }

        if ((long)pixelLength + maskLength > framebuffer.Limits.MaxMessageBytes)
        {
            throw new RfbProtocolException("Cursor pixel and mask payload exceeds the configured message limit.");
        }

        var wirePixels = await reader.ReadBytesAsync(pixelLength, cancellationToken);
        byte[]? mask = null;
        byte[]? bgraPixels = null;
        try
        {
            mask = await reader.ReadBytesAsync(maskLength, cancellationToken);
            bgraPixels = PixelConverter.ToBgra32(wirePixels, width, height, pixelFormat, framebuffer.Limits);
            ApplyMask(bgraPixels, mask, width, height, maskStride);
            return new EncodingDecodeResult(Cursor: new RemoteCursor(x, y, width, height, bgraPixels));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wirePixels);
            if (mask is not null)
            {
                CryptographicOperations.ZeroMemory(mask);
            }

            if (bgraPixels is not null)
            {
                CryptographicOperations.ZeroMemory(bgraPixels);
            }
        }
    }

    private static void ApplyMask(byte[] bgraPixels, byte[] mask, int width, int height, int maskStride)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var isVisible = (mask[(y * maskStride) + (x / 8)] & (0x80 >> (x % 8))) != 0;
                bgraPixels[((y * width) + x) * 4 + 3] = isVisible ? (byte)255 : (byte)0;
            }
        }
    }
}
