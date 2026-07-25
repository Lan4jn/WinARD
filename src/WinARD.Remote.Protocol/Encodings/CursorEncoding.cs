using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class CursorEncoding : IRfbEncodingDecoder
{
    private readonly PixelFormat _pixelFormat;

    public CursorEncoding()
        : this(PixelFormat.WinArdBgra32)
    {
    }

    public CursorEncoding(PixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(pixelFormat);
        _pixelFormat = pixelFormat;
    }

    public int EncodingId => (int)RfbEncodingType.Cursor;

    public async ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        var x = rectangle.X;
        var y = rectangle.Y;
        var width = rectangle.Width;
        var height = rectangle.Height;
        if (width == 0 || height == 0)
        {
            if (width != 0 || height != 0)
            {
                throw new RfbProtocolException("An empty cursor must have both width and height set to zero.");
            }

            framebuffer.SetCursor(new RemoteCursor(x, y, 0, 0, []));
            return EncodingDecodeResult.Empty;
        }

        if (x >= width || y >= height)
        {
            throw new RfbProtocolException("Cursor hotspot lies outside the cursor image.");
        }

        var pixelLength = PixelConverter.CheckedWireLength(width, height, _pixelFormat, framebuffer.Limits);
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

        var bgraLength = PixelConverter.CheckedBgraLength(width, height, framebuffer.Limits);
        if (bgraLength > framebuffer.Limits.MaxCursorBytes)
        {
            throw new RfbProtocolException(
                $"Cursor requires {bgraLength} BGRA bytes, exceeding the configured cursor limit of " +
                $"{framebuffer.Limits.MaxCursorBytes} bytes.");
        }

        int payloadLength;
        try
        {
            payloadLength = checked(pixelLength + maskLength);
        }
        catch (OverflowException exception)
        {
            throw new RfbProtocolException("Cursor payload length overflowed.", exception);
        }

        reader.ReserveFramebufferUpdateWorkBytes(
            checked((long)pixelLength + maskLength + bgraLength + bgraLength));
        reader.ReserveFramebufferUpdateBytes(payloadLength);

        var wirePixels = await reader.ReadFramebufferPayloadBytesAsync(pixelLength, cancellationToken);
        byte[]? mask = null;
        byte[]? bgraPixels = null;
        try
        {
            mask = await reader.ReadFramebufferPayloadBytesAsync(maskLength, cancellationToken);
            bgraPixels = PixelConverter.ToBgra32(wirePixels, width, height, _pixelFormat, framebuffer.Limits);
            ApplyMask(bgraPixels, mask, width, height, maskStride);
            framebuffer.SetCursor(new RemoteCursor(x, y, width, height, bgraPixels));
            return EncodingDecodeResult.Empty;
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
