using System.Buffers.Binary;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

internal static class PixelConverter
{
    public static int CheckedWireLength(int width, int height, PixelFormat format, ProtocolLimits limits)
    {
        return CheckedPayloadLength(
            width,
            height,
            format.BytesPerPixel,
            limits.MaxMessageBytes,
            "message");
    }

    public static int CheckedBgraLength(int width, int height, ProtocolLimits limits)
    {
        return CheckedPayloadLength(width, height, 4, limits.MaxFramebufferBytes, "framebuffer");
    }

    public static byte[] ToBgra32(ReadOnlySpan<byte> wirePixels, int width, int height, PixelFormat format, ProtocolLimits limits)
    {
        var expectedWireLength = CheckedWireLength(width, height, format, limits);
        if (wirePixels.Length != expectedWireLength)
        {
            throw new RfbProtocolException(
                $"Encoded pixel payload contains {wirePixels.Length} bytes; expected {expectedWireLength}.");
        }

        var bgra = new byte[CheckedBgraLength(width, height, limits)];
        var bytesPerPixel = format.BytesPerPixel;
        var pixelCount = wirePixels.Length / bytesPerPixel;
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var source = wirePixels.Slice(pixelIndex * bytesPerPixel, bytesPerPixel);
            var value = ReadPixel(source, format.BigEndian);
            var destination = bgra.AsSpan(pixelIndex * 4, 4);
            destination[0] = Scale((value >> format.BlueShift) & format.BlueMax, format.BlueMax);
            destination[1] = Scale((value >> format.GreenShift) & format.GreenMax, format.GreenMax);
            destination[2] = Scale((value >> format.RedShift) & format.RedMax, format.RedMax);
            destination[3] = 255;
        }

        return bgra;
    }

    private static int CheckedPayloadLength(int width, int height, int bytesPerPixel, int limit, string limitName)
    {
        long byteLength;
        try
        {
            byteLength = checked((long)width * height * bytesPerPixel);
        }
        catch (OverflowException exception)
        {
            throw new RfbProtocolException("Encoded pixel payload length overflowed.", exception);
        }

        if (byteLength > limit || byteLength > int.MaxValue)
        {
            throw new RfbProtocolException(
                $"Encoded pixel payload length {byteLength} exceeds the configured {limitName} limit of {limit} bytes.");
        }

        return checked((int)byteLength);
    }

    private static uint ReadPixel(ReadOnlySpan<byte> bytes, bool bigEndian) => bytes.Length switch
    {
        1 => bytes[0],
        2 when bigEndian => BinaryPrimitives.ReadUInt16BigEndian(bytes),
        2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        4 when bigEndian => BinaryPrimitives.ReadUInt32BigEndian(bytes),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        _ => throw new RfbProtocolException($"Unsupported encoded pixel width {bytes.Length} bytes."),
    };

    private static byte Scale(uint component, uint maximum) =>
        checked((byte)(((component * 255u) + (maximum / 2u)) / maximum));
}
