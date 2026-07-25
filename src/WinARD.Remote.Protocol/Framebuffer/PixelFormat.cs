using System.Buffers.Binary;
using System.Numerics;
using WinARD.Remote.Protocol.Errors;

namespace WinARD.Remote.Protocol.Framebuffer;

public sealed record PixelFormat
{
    public PixelFormat(
        byte bitsPerPixel,
        byte depth,
        byte bigEndianFlag,
        byte trueColorFlag,
        ushort redMax,
        ushort greenMax,
        ushort blueMax,
        byte redShift,
        byte greenShift,
        byte blueShift)
    {
        Validate(bitsPerPixel, depth, bigEndianFlag, trueColorFlag, redMax, greenMax, blueMax, redShift, greenShift, blueShift);
        BitsPerPixel = bitsPerPixel;
        Depth = depth;
        BigEndian = bigEndianFlag == 1;
        TrueColor = trueColorFlag == 1;
        RedMax = redMax;
        GreenMax = greenMax;
        BlueMax = blueMax;
        RedShift = redShift;
        GreenShift = greenShift;
        BlueShift = blueShift;
    }

    public static PixelFormat WinArdBgra32 { get; } = new(32, 24, 0, 1, 255, 255, 255, 16, 8, 0);

    public byte BitsPerPixel { get; }
    public byte Depth { get; }
    public bool BigEndian { get; }
    public bool TrueColor { get; }
    public ushort RedMax { get; }
    public ushort GreenMax { get; }
    public ushort BlueMax { get; }
    public byte RedShift { get; }
    public byte GreenShift { get; }
    public byte BlueShift { get; }
    public int BytesPerPixel => BitsPerPixel / 8;

    public static PixelFormat Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
        {
            throw new RfbProtocolException("An RFB pixel format must contain exactly 16 bytes.");
        }

        return new PixelFormat(
            bytes[0],
            bytes[1],
            bytes[2],
            bytes[3],
            BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(bytes[8..]),
            bytes[10],
            bytes[11],
            bytes[12]);
    }

    public byte[] ToWireBytes()
    {
        var bytes = new byte[16];
        bytes[0] = BitsPerPixel;
        bytes[1] = Depth;
        bytes[2] = BigEndian ? (byte)1 : (byte)0;
        bytes[3] = TrueColor ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), RedMax);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), GreenMax);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), BlueMax);
        bytes[10] = RedShift;
        bytes[11] = GreenShift;
        bytes[12] = BlueShift;
        return bytes;
    }

    private static void Validate(
        byte bitsPerPixel,
        byte depth,
        byte bigEndianFlag,
        byte trueColorFlag,
        ushort redMax,
        ushort greenMax,
        ushort blueMax,
        byte redShift,
        byte greenShift,
        byte blueShift)
    {
        if (bitsPerPixel is not (8 or 16 or 32))
        {
            throw new RfbProtocolException($"Unsupported RFB bits-per-pixel value {bitsPerPixel}.");
        }

        if (depth == 0 || depth > bitsPerPixel)
        {
            throw new RfbProtocolException($"RFB pixel depth {depth} is invalid for {bitsPerPixel} bits per pixel.");
        }

        if (bigEndianFlag > 1 || trueColorFlag > 1)
        {
            throw new RfbProtocolException("RFB pixel format flags must be either zero or one.");
        }

        if (trueColorFlag != 1)
        {
            throw new RfbProtocolException("Indexed-color RFB pixel formats are not supported.");
        }

        ValidateMaximum(redMax, "red");
        ValidateMaximum(greenMax, "green");
        ValidateMaximum(blueMax, "blue");

        var redMask = ComponentMask(redMax, redShift, bitsPerPixel, "red");
        var greenMask = ComponentMask(greenMax, greenShift, bitsPerPixel, "green");
        var blueMask = ComponentMask(blueMax, blueShift, bitsPerPixel, "blue");
        if ((redMask & greenMask) != 0 || (redMask & blueMask) != 0 || (greenMask & blueMask) != 0)
        {
            throw new RfbProtocolException("RFB true-color component masks overlap.");
        }
    }

    private static void ValidateMaximum(ushort maximum, string component)
    {
        var range = (uint)maximum + 1;
        if (maximum == 0 || (range & (range - 1)) != 0)
        {
            throw new RfbProtocolException($"RFB {component} maximum {maximum} is not a valid bit mask.");
        }
    }

    private static uint ComponentMask(ushort maximum, byte shift, byte bitsPerPixel, string component)
    {
        var componentBits = 32 - BitOperations.LeadingZeroCount(maximum);
        if (shift >= bitsPerPixel || shift + componentBits > bitsPerPixel)
        {
            throw new RfbProtocolException($"RFB {component} component mask exceeds the pixel width.");
        }

        return (uint)maximum << shift;
    }
}
