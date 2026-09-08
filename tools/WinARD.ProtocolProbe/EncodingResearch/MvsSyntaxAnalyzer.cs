using System.Buffers.Binary;

namespace WinARD.ProtocolProbe.EncodingResearch;

/// <summary>
/// 表示从 Encoding 1011 Setup 伪矩形中提取的双 8x8 量化表（Luminance 与 Chrominance）。
/// </summary>
public sealed class MvsSetupTable
{
    public const int TableSize = 64;
    public const int ExpectedPayloadLength = 1 + (TableSize * 2); // 129 bytes

    public byte TableCount { get; }
    public byte[] LuminanceMatrix { get; }
    public byte[] ChrominanceMatrix { get; }

    public MvsSetupTable(byte tableCount, byte[] luminanceMatrix, byte[] chrominanceMatrix)
    {
        ArgumentNullException.ThrowIfNull(luminanceMatrix);
        ArgumentNullException.ThrowIfNull(chrominanceMatrix);

        if (luminanceMatrix.Length != TableSize)
        {
            throw new ArgumentException($"Luminance matrix must be exactly {TableSize} bytes.", nameof(luminanceMatrix));
        }

        if (chrominanceMatrix.Length != TableSize)
        {
            throw new ArgumentException($"Chrominance matrix must be exactly {TableSize} bytes.", nameof(chrominanceMatrix));
        }

        TableCount = tableCount;
        LuminanceMatrix = luminanceMatrix;
        ChrominanceMatrix = chrominanceMatrix;
    }

    /// <summary>
    /// 解析 1011 Setup 载荷（支持纯 129 字节载荷或含 4 字节长度前缀的 133 字节载荷）。
    /// </summary>
    public static MvsSetupTable Parse(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> effectivePayload = payload;

        // 若前 4 字节为大端 129 且总长为 133，则跳过长度前缀
        if (payload.Length == 133 && BinaryPrimitives.ReadUInt32BigEndian(payload[..4]) == 129)
        {
            effectivePayload = payload[4..];
        }

        if (effectivePayload.Length != ExpectedPayloadLength)
        {
            throw new ArgumentException(
                $"Invalid MVS Setup payload length: {effectivePayload.Length}. Expected {ExpectedPayloadLength} bytes.",
                nameof(payload));
        }

        byte tableCount = effectivePayload[0];
        if (tableCount != 2)
        {
            throw new FormatException($"Unsupported MVS Setup table count: {tableCount}. Expected 2 (Luma + Chroma).");
        }

        byte[] luma = effectivePayload.Slice(1, TableSize).ToArray();
        byte[] chroma = effectivePayload.Slice(1 + TableSize, TableSize).ToArray();

        return new MvsSetupTable(tableCount, luma, chroma);
    }

    public byte GetLuminanceStep(int row, int col)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, 8);
        ArgumentOutOfRangeException.ThrowIfNegative(col);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(col, 8);

        return LuminanceMatrix[(row * 8) + col];
    }

    public byte GetChrominanceStep(int row, int col)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, 8);
        ArgumentOutOfRangeException.ThrowIfNegative(col);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(col, 8);

        return ChrominanceMatrix[(row * 8) + col];
    }
}

/// <summary>
/// 表示 MVS 图像切片的首部元数据（固定 6 字节）。
/// </summary>
public sealed record MvsSliceHeader(
    ushort BlockDimension,
    ushort FormatMagic,
    ushort QualityParameter)
{
    public const int HeaderSize = 6;

    /// <summary>
    /// 从图像切片载荷中解析 6 字节切片头（若带 4 字节前缀则自动跳过）。
    /// </summary>
    public static MvsSliceHeader Parse(ReadOnlySpan<byte> slicePayload)
    {
        ReadOnlySpan<byte> effectiveSlice = slicePayload;

        if (slicePayload.Length >= 4)
        {
            uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(slicePayload[..4]);
            if (declaredLength + 4 == (uint)slicePayload.Length)
            {
                effectiveSlice = slicePayload[4..];
            }
        }

        if (effectiveSlice.Length < HeaderSize)
        {
            throw new ArgumentException(
                $"Slice payload is too short ({effectiveSlice.Length} bytes). Minimum header size is {HeaderSize} bytes.",
                nameof(slicePayload));
        }

        ushort blockDim = BinaryPrimitives.ReadUInt16BigEndian(effectiveSlice[..2]);
        ushort magic = BinaryPrimitives.ReadUInt16BigEndian(effectiveSlice.Slice(2, 2));
        ushort qp = BinaryPrimitives.ReadUInt16BigEndian(effectiveSlice.Slice(4, 2));

        return new MvsSliceHeader(blockDim, magic, qp);
    }
}

/// <summary>
/// 高位优先（MSB first）的位流读取器，支持逐位读取、无符号/有符号指数哥伦布编码（Exp-Golomb）。
/// </summary>
public sealed class MvsBitReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _bitPosition;

    public MvsBitReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
        _bitPosition = 0;
    }

    public int TotalBits => _data.Length * 8;
    public int PositionBits => _bitPosition;
    public int BitsRemaining => TotalBits - _bitPosition;

    /// <summary>
    /// 读取单个 bit。
    /// </summary>
    public bool ReadBit()
    {
        if (_bitPosition >= TotalBits)
        {
            throw new EndOfStreamException("Attempted to read past the end of the bitstream.");
        }

        int byteIndex = _bitPosition >> 3;
        int bitOffset = 7 - (_bitPosition & 7); // MSB first
        _bitPosition++;

        byte b = _data.Span[byteIndex];
        return ((b >> bitOffset) & 1) != 0;
    }

    /// <summary>
    /// 读取指定位数（1..32 位）。
    /// </summary>
    public uint ReadBits(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 32);

        if (count > BitsRemaining)
        {
            throw new EndOfStreamException(
                $"Requested {count} bits, but only {BitsRemaining} bits remain in the bitstream.");
        }

        uint result = 0;
        for (int i = 0; i < count; i++)
        {
            result = (result << 1) | (ReadBit() ? 1U : 0U);
        }

        return result;
    }

    /// <summary>
    /// 读取无符号指数哥伦布码 (UE)。
    /// </summary>
    public uint ReadExpGolombUnsigned()
    {
        int leadingZeros = 0;
        while (!ReadBit())
        {
            leadingZeros++;
            if (leadingZeros > 31)
            {
                throw new InvalidOperationException("Exp-Golomb code exceeds 32 bits.");
            }
        }

        if (leadingZeros == 0)
        {
            return 0;
        }

        uint suffix = ReadBits(leadingZeros);
        return (1U << leadingZeros) - 1 + suffix;
    }

    /// <summary>
    /// 读取有符号指数哥伦布码 (SE)。
    /// </summary>
    public int ReadExpGolombSigned()
    {
        uint codeNum = ReadExpGolombUnsigned();
        if ((codeNum & 1) != 0)
        {
            return (int)((codeNum + 1) >> 1);
        }
        else
        {
            return -(int)(codeNum >> 1);
        }
    }
}

/// <summary>
/// 提供 MVS 载荷结构化分析与诊断能力。
/// </summary>
public static class MvsSyntaxAnalyzer
{
    public static MvsSetupTable AnalyzeSetup(ReadOnlySpan<byte> payload)
    {
        return MvsSetupTable.Parse(payload);
    }

    public static (MvsSliceHeader Header, ReadOnlyMemory<byte> EntropyPayload) AnalyzeSlice(ReadOnlyMemory<byte> slicePayload)
    {
        ReadOnlyMemory<byte> effective = slicePayload;
        if (slicePayload.Length >= 4)
        {
            uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(slicePayload.Span[..4]);
            if (declaredLength + 4 == (uint)slicePayload.Length)
            {
                effective = slicePayload[4..];
            }
        }

        MvsSliceHeader header = MvsSliceHeader.Parse(effective.Span);
        ReadOnlyMemory<byte> entropy = effective[MvsSliceHeader.HeaderSize..];

        return (header, entropy);
    }
}
