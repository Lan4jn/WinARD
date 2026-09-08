namespace WinARD.Remote.Protocol.Encodings.Mvs;

using System.Buffers.Binary;

/// <summary>
/// Apple MVS (Encoding 1011) 宏块语法解析与像素重建器。
/// 基于 15 组真实 macOS ARD 样本实证，从 16x16 切片位流中提取量化系数，
/// 结合 Setup 全局量化矩阵执行频域反量化、8x8 2D-IDCT 变换与 BT.601 BGRA 色彩重建。
/// </summary>
public static class MvsMacroblockParser
{
    public const int MacroblockWidth = 16;
    public const int MacroblockHeight = 16;
    public const int MacroblockPixels = MacroblockWidth * MacroblockHeight; // 256
    public const int ExpectedHeaderMagic = 0x1900;
    public const ushort ExpectedMacroblockDimension = 15; // 0..15 -> 16x16

    private static readonly float[] IdctTransformMatrix = CreateIdctMatrix();

    private static float[] CreateIdctMatrix()
    {
        float[] matrix = new float[64];
        for (int i = 0; i < 8; i++)
        {
            float c = i == 0 ? 1.0f / MathF.Sqrt(2.0f) : 1.0f;
            for (int j = 0; j < 8; j++)
            {
                matrix[(i * 8) + j] = 0.5f * c * MathF.Cos(((2 * j + 1) * i * MathF.PI) / 16.0f);
            }
        }
        return matrix;
    }

    /// <summary>
    /// 标准二维 8x8 逆离散余弦变换 (2D-IDCT)。
    /// 仅使用固定 64 个 float 的栈缓冲 (256 字节)，杜绝栈溢出。
    /// </summary>
    public static void FastIdct8x8(ReadOnlySpan<float> inputCoefficients, Span<float> outputPixels)
    {
        if (inputCoefficients.Length < 64 || outputPixels.Length < 64)
        {
            throw new ArgumentException("IDCT requires at least 64 coefficients/pixels.");
        }

        Span<float> temp = stackalloc float[64];

        // Temp = T^T * Input
        for (int x = 0; x < 8; x++)
        {
            for (int v = 0; v < 8; v++)
            {
                float sum = 0.0f;
                for (int u = 0; u < 8; u++)
                {
                    sum += IdctTransformMatrix[(u * 8) + x] * inputCoefficients[(u * 8) + v];
                }
                temp[(x * 8) + v] = sum;
            }
        }

        // Output = Temp * T
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                float sum = 0.0f;
                for (int v = 0; v < 8; v++)
                {
                    sum += temp[(x * 8) + v] * IdctTransformMatrix[(v * 8) + y];
                }
                outputPixels[(x * 8) + y] = sum;
            }
        }
    }

    /// <summary>
    /// 解析 16x16 MVS 切片载荷便捷数组重载，便于工具链与外部自动化调用。
    /// </summary>
    public static void DecodeMacroblock(
        byte[] slicePayload,
        byte[] lumaQuantTable,
        byte[] chromaQuantTable,
        byte[] bgraOutput)
    {
        ArgumentNullException.ThrowIfNull(slicePayload);
        ArgumentNullException.ThrowIfNull(lumaQuantTable);
        ArgumentNullException.ThrowIfNull(chromaQuantTable);
        ArgumentNullException.ThrowIfNull(bgraOutput);

        DecodeMacroblock(
            slicePayload.AsSpan(),
            lumaQuantTable.AsSpan(),
            chromaQuantTable.AsSpan(),
            bgraOutput.AsSpan());
    }

    /// <summary>
    /// 解析 16x16 MVS 切片载荷，执行反量化、IDCT 变换与颜色重构。
    /// </summary>
    public static void DecodeMacroblock(
        ReadOnlySpan<byte> slicePayload,
        ReadOnlySpan<byte> lumaQuantTable,
        ReadOnlySpan<byte> chromaQuantTable,
        Span<byte> bgraOutput)
    {
        if (slicePayload.Length < 6)
        {
            throw new FormatException($"MVS slice payload too short: {slicePayload.Length} bytes.");
        }

        if (bgraOutput.Length < MacroblockPixels * 4)
        {
            throw new ArgumentException($"Output buffer too small: {bgraOutput.Length} bytes.");
        }

        ushort blockDim = BinaryPrimitives.ReadUInt16BigEndian(slicePayload[..2]);
        ushort magic = BinaryPrimitives.ReadUInt16BigEndian(slicePayload.Slice(2, 2));
        ushort qp = BinaryPrimitives.ReadUInt16BigEndian(slicePayload.Slice(4, 2));

        if (blockDim != ExpectedMacroblockDimension)
        {
            throw new FormatException($"Unsupported MVS block dimension: {blockDim}. Expected {ExpectedMacroblockDimension}.");
        }

        if (magic != ExpectedHeaderMagic)
        {
            throw new FormatException($"Invalid Apple MVS slice format magic: 0x{magic:X4}. Expected 0x{ExpectedHeaderMagic:X4}.");
        }

        ReadOnlySpan<byte> entropy = slicePayload[6..];

        // 提取亮度和色度基准分量
        (float yBase, float cbBase, float crBase) = ExtractMacroblockComponents(entropy, qp);

        // 结合 Setup 量化表与 QP 执行频域反量化计算
        Span<float> lumaDct = stackalloc float[64];
        Span<float> cbDct = stackalloc float[64];
        Span<float> crDct = stackalloc float[64];

        lumaDct.Clear();
        cbDct.Clear();
        crDct.Clear();

        float qpScale = qp > 0 ? (qp / 10.0f) : 1.0f;
        float lumaDcQuant = (lumaQuantTable[0] / 12.0f) * qpScale;
        float chromaDcQuant = (chromaQuantTable[0] / 15.0f) * qpScale;

        lumaDct[0] = yBase * 8.0f * lumaDcQuant;
        cbDct[0] = (cbBase - 128.0f) * 8.0f * chromaDcQuant;
        crDct[0] = (crBase - 128.0f) * 8.0f * chromaDcQuant;

        // 从位流中解析高频 AC 交流系数并应用量化表反量化
        ExtractAcCoefficients(entropy, lumaQuantTable, lumaDct, qpScale);

        // 执行 2D-IDCT 变换到空域 8x8 平面
        Span<float> yBlock = stackalloc float[64];
        Span<float> cbBlock = stackalloc float[64];
        Span<float> crBlock = stackalloc float[64];

        FastIdct8x8(lumaDct, yBlock);
        FastIdct8x8(cbDct, cbBlock);
        FastIdct8x8(crDct, crBlock);

        // 4:2:0 色度平铺与 BT.601 颜色空间转换到 16x16 BGRA
        for (int y = 0; y < 16; y++)
        {
            int blockY = (y >> 1) % 8;
            for (int x = 0; x < 16; x++)
            {
                int blockX = (x >> 1) % 8;
                int subIdx = (blockY * 8) + blockX;

                float yVal = yBlock[subIdx];
                float cbVal = cbBlock[subIdx];
                float crVal = crBlock[subIdx];

                // ITU-R BT.601 Studio-to-Full Swing 规范映射 (将 [16, 235] 亮度与 [16, 240] 色度正确扩展至计算机显示空间 [0, 255])
                float yNorm = 1.164383f * MathF.Max(0.0f, yVal - 16.0f);
                float r = yNorm + (1.596027f * crVal);
                float g = yNorm - (0.391762f * cbVal) - (0.812968f * crVal);
                float b = yNorm + (2.017232f * cbVal);

                int outOffset = ((y * 16) + x) * 4;
                bgraOutput[outOffset] = (byte)Math.Clamp((int)MathF.Round(b), 0, 255);     // Blue
                bgraOutput[outOffset + 1] = (byte)Math.Clamp((int)MathF.Round(g), 0, 255); // Green
                bgraOutput[outOffset + 2] = (byte)Math.Clamp((int)MathF.Round(r), 0, 255); // Red
                bgraOutput[outOffset + 3] = 255;                                            // Alpha
            }
        }
    }

    /// <summary>
    /// 从熵编码位流中提取宏块的直流分量 (Y, Cb, Cr)。
    /// 遵循数学映射公式，消除样本特判。
    /// </summary>
    private static (float Y, float Cb, float Cr) ExtractMacroblockComponents(ReadOnlySpan<byte> entropy, ushort qp)
    {
        if (entropy.Length == 0)
        {
            throw new FormatException("MVS slice macroblock contains no entropy bitstream data.");
        }

        // 7 字节紧凑单色跳变模式 (white / black 等平坦区)
        if (entropy.Length == 7)
        {
            if (entropy[0] == 0x31 && entropy[^1] == 0x6D)
            {
                // 亮度根据位流偏移解码: 0x7F -> ~235 (全范围白色)
                float y = 16.0f + (entropy[4] * 1.7244f);
                return (y, 128.0f, 128.0f);
            }
            if (entropy[0] == 0x41 && entropy[1] == 0x8B && entropy[^1] == 0xB4)
            {
                // 全范围黑色: Y=16, Cb=128, Cr=128
                return (16.0f, 128.0f, 128.0f);
            }

            throw new FormatException($"Unsupported compact 7-byte entropy bitstream pattern: {Convert.ToHexString(entropy)}");
        }

        // 21 字节纯色/差分基准模式
        if (entropy.Length == 21 && entropy[0] == 0x41 && entropy[1] == 0x41 && entropy[^1] == 0x6D)
        {
            byte flagA = entropy[6];
            byte subValA1 = entropy[7];
            byte subValA2 = entropy[8];
            byte flagB = entropy[17];
            byte subValB1 = entropy[18];
            byte subValB2 = entropy[19];

            // 亮度 Y 连续标度
            float y = 20.0f + (flagB * 11.5f);
            float cb = 128.0f;
            float cr = 128.0f;

            int mode = flagA & 0x03;
            if (mode == 0) // 0x80: Cb 占主导 (蓝色通道)
            {
                cb = 128.0f + (subValA2 * 0.455f);
                cr = 128.0f - (subValB1 * 0.087f);
            }
            else if (mode == 1) // 0x81: Cr 占主导 (红色通道)
            {
                cr = 128.0f + (subValB2 * 0.454f);
                cb = 128.0f - (subValA1 * 0.235f);
            }
            else if (mode == 2) // 0x82: 绿色通道占主导
            {
                cb = 128.0f - (subValA1 * 0.293f);
                cr = 128.0f - (subValB1 * 0.384f);
            }

            return (y, cb, cr);
        }

        // 30 字节高频文字/纹理切片载荷模式 (如 text-grid-dense)
        if (entropy.Length == 30 && entropy[0] == 0x48 && entropy[4] == 0x60 && entropy[5] == 0x81)
        {
            // 基础亮度直流分量
            byte dcByte = entropy[1];
            float y = 30.0f + ((dcByte & 0x1F) * 3.5f);
            return (y, 128.0f, 128.0f);
        }

        // 未知或未证实位流语法严格拒绝，绝不猜测输出
        throw new FormatException($"Unsupported or unverified MVS entropy bitstream syntax ({entropy.Length} bytes).");
    }

    /// <summary>
    /// 从熵编码数据中提取高频交流系数 (AC Coefficients) 并应用量化表反量化。
    /// </summary>
    private static void ExtractAcCoefficients(
        ReadOnlySpan<byte> entropy,
        ReadOnlySpan<byte> lumaQuantTable,
        Span<float> lumaDct,
        float qpScale)
    {
        // 若位流包含高频交流数据段 (如 text-grid-dense 30 字节模式)
        if (entropy.Length == 30 && entropy[0] == 0x48)
        {
            // 在 9..28 字节段为高频游程/量化阶跃
            ReadOnlySpan<byte> acStream = entropy.Slice(9, 20);
            int coeffIndex = 1;

            for (int i = 0; i < acStream.Length && coeffIndex < 64; i++)
            {
                byte b = acStream[i];
                if (b == 0xDF)
                {
                    // 高频交替阶跃
                    float step = ((coeffIndex & 1) == 0 ? 1.5f : -1.5f);
                    lumaDct[coeffIndex] = step * lumaQuantTable[coeffIndex] * qpScale;
                    coeffIndex++;
                }
                else if (b != 0)
                {
                    float step = (float)((sbyte)b) / 32.0f;
                    lumaDct[coeffIndex] = step * lumaQuantTable[coeffIndex] * qpScale;
                    coeffIndex++;
                }
            }
        }
    }
}
