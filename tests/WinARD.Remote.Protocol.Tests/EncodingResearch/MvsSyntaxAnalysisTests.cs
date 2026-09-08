using System.Buffers.Binary;
using WinARD.ProtocolProbe.EncodingResearch;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.EncodingResearch;

public sealed class MvsSyntaxAnalysisTests
{
    private static readonly byte[] RealSetupPayload129 =
    [
        0x02,
        // Luminance (64 bytes)
        0x0C, 0x09, 0x09, 0x0B, 0x12, 0x11, 0x12, 0x19,
        0x09, 0x09, 0x09, 0x09, 0x14, 0x0E, 0x0F, 0x12,
        0x0A, 0x0A, 0x0B, 0x12, 0x0F, 0x10, 0x11, 0x15,
        0x0A, 0x0D, 0x0B, 0x10, 0x12, 0x13, 0x1A, 0x1A,
        0x0D, 0x11, 0x0E, 0x13, 0x18, 0x18, 0x1A, 0x21,
        0x12, 0x0F, 0x0D, 0x15, 0x1B, 0x1E, 0x21, 0x29,
        0x17, 0x1B, 0x15, 0x17, 0x1A, 0x21, 0x29, 0x30,
        0x1B, 0x17, 0x19, 0x1D, 0x22, 0x2A, 0x31, 0x39,
        // Chrominance (64 bytes)
        0x0F, 0x0F, 0x12, 0x24, 0x39, 0x4B, 0x4B, 0x4B,
        0x0F, 0x10, 0x14, 0x32, 0x4B, 0x4B, 0x4B, 0x4B,
        0x12, 0x14, 0x2A, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B,
        0x24, 0x32, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B,
        0x39, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B,
        0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B,
        0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B,
        0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B, 0x4B
    ];

    private static readonly byte[] RealSlicePayload18 =
    [
        0x00, 0x00, 0x00, 0x0E, // length = 14
        0x00, 0x0F,             // block dimension = 15 (16x16)
        0x19, 0x00,             // magic = 0x1900
        0x00, 0x09,             // QP = 9
        0x59, 0x36, 0x80, 0x1C, 0xB5, 0xEA, 0x2E, 0xDA
    ];

    [Fact]
    public void ParseSetup_Valid129Bytes_ExtractsCorrectTables()
    {
        MvsSetupTable table = MvsSetupTable.Parse(RealSetupPayload129);

        Assert.Equal(2, table.TableCount);
        Assert.Equal(64, table.LuminanceMatrix.Length);
        Assert.Equal(64, table.ChrominanceMatrix.Length);

        // Luma DC and corners
        Assert.Equal(12, table.GetLuminanceStep(0, 0));
        Assert.Equal(9, table.GetLuminanceStep(0, 1));
        Assert.Equal(57, table.GetLuminanceStep(7, 7));

        // Chroma clamping to 75 (0x4B)
        Assert.Equal(15, table.GetChrominanceStep(0, 0));
        Assert.Equal(75, table.GetChrominanceStep(4, 4));
        Assert.Equal(75, table.GetChrominanceStep(7, 7));
    }

    [Fact]
    public void ParseSetup_WithLengthPrefix133Bytes_AutoStripsPrefix()
    {
        byte[] withPrefix = new byte[133];
        BinaryPrimitives.WriteUInt32BigEndian(withPrefix.AsSpan(0, 4), 129);
        RealSetupPayload129.CopyTo(withPrefix.AsSpan(4));

        MvsSetupTable table = MvsSetupTable.Parse(withPrefix);

        Assert.Equal(2, table.TableCount);
        Assert.Equal(12, table.GetLuminanceStep(0, 0));
    }

    [Fact]
    public void ParseSetup_InvalidLength_ThrowsArgumentException()
    {
        byte[] invalid = new byte[50];
        Assert.Throws<ArgumentException>(() => MvsSetupTable.Parse(invalid));
    }

    [Fact]
    public void ParseSetup_UnsupportedTableCount_ThrowsFormatException()
    {
        byte[] invalid = (byte[])RealSetupPayload129.Clone();
        invalid[0] = 3; // Unsupported count

        Assert.Throws<FormatException>(() => MvsSetupTable.Parse(invalid));
    }

    [Fact]
    public void ParseSliceHeader_ValidSlice_ExtractsExpectedFields()
    {
        MvsSliceHeader header = MvsSliceHeader.Parse(RealSlicePayload18);

        Assert.Equal(15, header.BlockDimension);
        Assert.Equal(0x1900, header.FormatMagic);
        Assert.Equal(9, header.QualityParameter);
    }

    [Fact]
    public void ParseSliceHeader_TooShort_ThrowsArgumentException()
    {
        byte[] shortData = [0x00, 0x0F];
        Assert.Throws<ArgumentException>(() => MvsSliceHeader.Parse(shortData));
    }

    [Fact]
    public void BitReader_BasicBitsAndExpGolomb_DecodesCorrectly()
    {
        // 0xA0 = 1010 0000 -> bit0=1, bit1=0, bit2=1, bit3=0
        byte[] data = [0xA0];
        MvsBitReader reader = new(data);

        Assert.True(reader.ReadBit());
        Assert.False(reader.ReadBit());
        Assert.True(reader.ReadBit());
        Assert.False(reader.ReadBit());
        Assert.Equal(4, reader.PositionBits);
        Assert.Equal(4, reader.BitsRemaining);

        // Test Exp-Golomb UE:
        // '1' -> 0
        // '010' -> 1
        // '011' -> 2
        // '00100' -> 3
        // Sequence: 1 | 010 | 011 | 0010 0 (1 + 3 + 3 + 5 = 12 bits)
        // Binary: 1 010 011 0 | 0100 0000 -> 0xA6, 0x40
        byte[] ueData = [0xA6, 0x40];
        MvsBitReader ueReader = new(ueData);

        Assert.Equal(0U, ueReader.ReadExpGolombUnsigned());
        Assert.Equal(1U, ueReader.ReadExpGolombUnsigned());
        Assert.Equal(2U, ueReader.ReadExpGolombUnsigned());
        Assert.Equal(3U, ueReader.ReadExpGolombUnsigned());
    }

    [Fact]
    public void BitReader_ExpGolombSigned_DecodesCorrectValues()
    {
        // SE: 0 -> 0; 1 -> 1; 2 -> -1; 3 -> 2; 4 -> -2
        // UE sequence: 0, 1, 2, 3, 4
        // Binary: 1 (0) | 010 (1) | 011 (-1) | 00100 (2) | 00101 (-2)
        // Total bits = 1 + 3 + 3 + 5 + 5 = 17 bits
        // Bits: 1 010 011 0 | 0100 0010 | 1 0000000 -> 0xA6, 0x42, 0x80
        byte[] seData = [0xA6, 0x42, 0x80];
        MvsBitReader seReader = new(seData);

        Assert.Equal(0, seReader.ReadExpGolombSigned());
        Assert.Equal(1, seReader.ReadExpGolombSigned());
        Assert.Equal(-1, seReader.ReadExpGolombSigned());
        Assert.Equal(2, seReader.ReadExpGolombSigned());
        Assert.Equal(-2, seReader.ReadExpGolombSigned());
    }

    [Fact]
    public void BitReader_ReadPastEnd_ThrowsEndOfStreamException()
    {
        byte[] data = [0xFF];
        MvsBitReader reader = new(data);

        reader.ReadBits(8);
        Assert.Equal(0, reader.BitsRemaining);
        Assert.Throws<EndOfStreamException>(() => reader.ReadBit());
    }

    [Fact]
    public void FastIdct8x8_DcCoefficient_ReconstructsUniformBlock()
    {
        // 8x8 DC only: F[0,0] = 8 * 128 = 1024.0
        float[] coeffs = new float[64];
        coeffs[0] = 1024.0f;

        float[] output = new float[64];
        MvsPrototypeDecoder.FastIdct8x8(coeffs, output);

        for (int i = 0; i < 64; i++)
        {
            Assert.InRange(output[i], 127.9f, 128.1f);
        }
    }

    [Fact]
    public void DequantizeBlock_AppliesTableAndScaleFactor()
    {
        short[] quantCoeffs = new short[64];
        quantCoeffs[0] = 10;
        quantCoeffs[1] = 5;

        byte[] quantTable = new byte[64];
        quantTable[0] = 12;
        quantTable[1] = 9;

        float[] dequant = new float[64];
        MvsPrototypeDecoder.DequantizeBlock(quantCoeffs, quantTable, dequant, 1.5f);

        Assert.Equal(10 * 12 * 1.5f, dequant[0]);
        Assert.Equal(5 * 9 * 1.5f, dequant[1]);
        Assert.Equal(0.0f, dequant[2]);
    }

    [Fact]
    public void ConvertYuvToBgra_PureBlueParameters_ProducesDominantBlue()
    {
        float[] y = new float[64];
        float[] cb = new float[64];
        float[] cr = new float[64];

        // Pure blue approximate BT.601 representation:
        // Y ~ 29, Cb ~ 255, Cr ~ 107
        Array.Fill(y, 29.0f);
        Array.Fill(cb, 255.0f);
        Array.Fill(cr, 107.0f);

        byte[] bgra = new byte[64 * 4];
        MvsPrototypeDecoder.ConvertYuvToBgra(y, cb, cr, bgra, 8, 8);

        // Check pixel (B, G, R, A)
        byte b = bgra[0];
        byte g = bgra[1];
        byte r = bgra[2];
        byte a = bgra[3];

        Assert.Equal(255, a);
        Assert.True(b > 200, $"Expected dominant blue, got {b}");
        Assert.True(g < 50, $"Expected low green, got {g}");
        Assert.True(r < 50, $"Expected low red, got {r}");
    }

    [Fact]
    [Obsolete("仅作为旧版蓝色假定原型的历史回归记录，不可作为 E2b/E3 真实位流解码验收依据。")]
#pragma warning disable CS0618 // 类型或成员已过时
    public void DecodeSolidMacroblock_HistoricalBluePrototype_VerifiesLegacyAssumptionOnly()
    {
        MvsSetupTable setup = MvsSetupTable.Parse(RealSetupPayload129);
        (MvsSliceHeader header, ReadOnlyMemory<byte> entropy) = MvsSyntaxAnalyzer.AnalyzeSlice(RealSlicePayload18);

        MvsDecodedBlock block = MvsPrototypeDecoder.DecodeSolidMacroblock(setup, header, entropy.Span, 16, 16);

        Assert.Equal(16, block.Width);
        Assert.Equal(16, block.Height);
        Assert.Equal(16 * 16 * 4, block.BgraPixels.Length);

        (byte r, byte g, byte b) = block.GetAverageColor();

        Assert.True(b > 200, $"Average blue should be > 200, was {b}");
        Assert.True(r < 50, $"Average red should be < 50, was {r}");
        Assert.True(g < 50, $"Average green should be < 50, was {g}");
    }

    [Fact]
    [Obsolete("仅作为旧版蓝色假定原型的历史回归记录，不可作为 E2b/E3 真实位流解码验收依据。")]
#pragma warning disable CS0618 // 类型或成员已过时
    public void EndToEnd_HistoricalDiskSampleArtifacts_VerifiesLegacyAssumptionOnly()
    {
        string setupPath = Path.Combine(
            AppContext.BaseDirectory,
            "../../../../artifacts/protocol-research/samples/solid-blue-complete/setup/payload-prefix.bin");

        string slicePath = Path.Combine(
            AppContext.BaseDirectory,
            "../../../../artifacts/protocol-research/samples/solid-blue-complete-1/slice/payload-prefix.bin");

        if (File.Exists(setupPath) && File.Exists(slicePath))
        {
            byte[] setupBytes = File.ReadAllBytes(setupPath);
            byte[] sliceBytes = File.ReadAllBytes(slicePath);

            MvsSetupTable setup = MvsSetupTable.Parse(setupBytes);
            (MvsSliceHeader header, ReadOnlyMemory<byte> entropy) = MvsSyntaxAnalyzer.AnalyzeSlice(sliceBytes);

            MvsDecodedBlock block = MvsPrototypeDecoder.DecodeSolidMacroblock(setup, header, entropy.Span);
            (byte r, byte g, byte b) = block.GetAverageColor();

            Assert.True(b > 200);
            Assert.True(r < 50);
            Assert.True(g < 50);
        }
    }

    [Fact]
    public void Research_ExamineMultiColorEntropyFields()
    {
        // 5 baseline samples
        byte[] red = Convert.FromHexString("41413680806081A17DE0000000000000000685F76D");
        byte[] green = Convert.FromHexString("41413680806082FD04E0000000000000000BF4136D");
        byte[] blue = Convert.FromHexString("4141368080608073F6E00000000000000001CFDB6D");
        byte[] white = Convert.FromHexString("310836807F006D");
        byte[] black = Convert.FromHexString("418B68006081B4");

        // Verify end marker
        Assert.Equal(0x6D, red[^1]);
        Assert.Equal(0x6D, green[^1]);
        Assert.Equal(0x6D, blue[^1]);
        Assert.Equal(0x6D, white[^1]);

        // Verify common header across primary colors
        Assert.Equal(red.AsSpan(0, 6).ToArray(), green.AsSpan(0, 6).ToArray());
        Assert.Equal(red.AsSpan(0, 6).ToArray(), blue.AsSpan(0, 6).ToArray());

        // Field 1: bytes 6..8
        // Red:   81 A1 7D
        // Green: 82 FD 04
        // Blue:  80 73 F6
        Assert.Equal(0x81, red[6]);
        Assert.Equal(0x82, green[6]);
        Assert.Equal(0x80, blue[6]);

        // Field 2: bytes 17..19
        // Red:   06 85 F7
        // Green: 0B F4 13
        // Blue:  01 CF DB
        Assert.Equal(0x06, red[17]);
        Assert.Equal(0x0B, green[17]);
        Assert.Equal(0x01, blue[17]);
    }
}
