namespace WinARD.ProtocolProbe.EncodingResearch;

/// <summary>
/// 表示解码完成的图像宏块/矩形像素缓冲区。
/// </summary>
public sealed class MvsDecodedBlock
{
    public int Width { get; }
    public int Height { get; }
    public byte[] BgraPixels { get; }

    public MvsDecodedBlock(int width, int height, byte[] bgraPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(bgraPixels);

        int expectedLength = width * height * 4;
        if (bgraPixels.Length != expectedLength)
        {
            throw new ArgumentException($"Pixel buffer length must be {expectedLength} bytes for {width}x{height} BGRA.", nameof(bgraPixels));
        }

        Width = width;
        Height = height;
        BgraPixels = bgraPixels;
    }

    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);

        int offset = ((y * Width) + x) * 4;
        return (BgraPixels[offset], BgraPixels[offset + 1], BgraPixels[offset + 2], BgraPixels[offset + 3]);
    }

    public (byte R, byte G, byte B) GetAverageColor()
    {
        long totalR = 0;
        long totalG = 0;
        long totalB = 0;
        int pixelCount = Width * Height;

        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            totalB += BgraPixels[offset];
            totalG += BgraPixels[offset + 1];
            totalR += BgraPixels[offset + 2];
        }

        return ((byte)(totalR / pixelCount), (byte)(totalG / pixelCount), (byte)(totalB / pixelCount));
    }
}

/// <summary>
/// 离线最小 MVS 原型解码器：实现反量化、8x8 2D-IDCT 与 YUV->BGRA 色彩空间变换。
/// </summary>
public static class MvsPrototypeDecoder
{
    private static readonly float[] IdctMatrix = CreateIdctTransformMatrix();

    private static float[] CreateIdctTransformMatrix()
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
    /// 标准 8x8 2D-IDCT 逆变换。
    /// 输入为 64 个频域 DCT 系数，输出为 64 个空域重构值。
    /// </summary>
    public static void FastIdct8x8(ReadOnlySpan<float> inputCoefficients, Span<float> outputPixels)
    {
        if (inputCoefficients.Length < 64)
        {
            throw new ArgumentException("Input coefficients must have at least 64 elements.", nameof(inputCoefficients));
        }

        if (outputPixels.Length < 64)
        {
            throw new ArgumentException("Output pixels must have at least 64 elements.", nameof(outputPixels));
        }

        // T^T * Input * T
        Span<float> temp = stackalloc float[64];

        // Temp = T^T * Input (即 Temp[x, v] = sum_u (T[u, x] * Input[u, v]))
        for (int x = 0; x < 8; x++)
        {
            for (int v = 0; v < 8; v++)
            {
                float sum = 0.0f;
                for (int u = 0; u < 8; u++)
                {
                    float t_ux = IdctMatrix[(u * 8) + x];
                    float f_uv = inputCoefficients[(u * 8) + v];
                    sum += t_ux * f_uv;
                }
                temp[(x * 8) + v] = sum;
            }
        }

        // Output = Temp * T (即 Output[x, y] = sum_v (Temp[x, v] * T[v, y]))
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                float sum = 0.0f;
                for (int v = 0; v < 8; v++)
                {
                    float temp_xv = temp[(x * 8) + v];
                    float t_vy = IdctMatrix[(v * 8) + y];
                    sum += temp_xv * t_vy;
                }
                outputPixels[(x * 8) + y] = sum;
            }
        }
    }

    /// <summary>
    /// 使用 Setup 量化表对 8x8 量化系数进行反量化。
    /// </summary>
    public static void DequantizeBlock(
        ReadOnlySpan<short> quantizedCoeffs,
        ReadOnlySpan<byte> quantTable,
        Span<float> dequantizedCoeffs,
        float qpScale = 1.0f)
    {
        if (quantizedCoeffs.Length < 64)
        {
            throw new ArgumentException("Quantized coefficients must have 64 elements.", nameof(quantizedCoeffs));
        }

        if (quantTable.Length < 64)
        {
            throw new ArgumentException("Quantization table must have 64 elements.", nameof(quantTable));
        }

        if (dequantizedCoeffs.Length < 64)
        {
            throw new ArgumentException("Dequantized coefficients must have 64 elements.", nameof(dequantizedCoeffs));
        }

        for (int i = 0; i < 64; i++)
        {
            dequantizedCoeffs[i] = quantizedCoeffs[i] * quantTable[i] * qpScale;
        }
    }

    /// <summary>
    /// 将 YUV 8x8 或 16x16 平面转换为 32 位 BGRA 图像格式。
    /// </summary>
    public static void ConvertYuvToBgra(
        ReadOnlySpan<float> yPlane,
        ReadOnlySpan<float> cbPlane,
        ReadOnlySpan<float> crPlane,
        Span<byte> bgraOutput,
        int width,
        int height)
    {
        int pixelCount = width * height;
        if (yPlane.Length < pixelCount || cbPlane.Length < pixelCount || crPlane.Length < pixelCount)
        {
            throw new ArgumentException("Plane buffers must be at least width * height in size.");
        }

        if (bgraOutput.Length < pixelCount * 4)
        {
            throw new ArgumentException("BGRA output buffer must be at least width * height * 4 in size.");
        }

        for (int i = 0; i < pixelCount; i++)
        {
            float y = yPlane[i];
            float cb = cbPlane[i] - 128.0f;
            float cr = crPlane[i] - 128.0f;

            // BT.601 限幅色彩转换
            float r = y + (1.402f * cr);
            float g = y - (0.344136f * cb) - (0.714136f * cr);
            float b = y + (1.772f * cb);

            int outOffset = i * 4;
            bgraOutput[outOffset] = (byte)Math.Clamp((int)MathF.Round(b), 0, 255);     // Blue
            bgraOutput[outOffset + 1] = (byte)Math.Clamp((int)MathF.Round(g), 0, 255); // Green
            bgraOutput[outOffset + 2] = (byte)Math.Clamp((int)MathF.Round(r), 0, 255); // Red
            bgraOutput[outOffset + 3] = 255;                                            // Alpha
        }
    }

    /// <summary>
    /// 旧版针对特定纯蓝假定的离线原型实验方法。
    /// 警告：该方法直接写入假定色彩基准，不代表已实现真正的 MVS 位流解码，不可用于协议验收。
    /// </summary>
    [Obsolete("仅用于保留历史蓝色假定原型实验记录，不代表已实现真实 MVS 位流解码，不可作为协议验收依据。")]
    public static MvsDecodedBlock DecodeSolidMacroblock(
        MvsSetupTable setupTable,
        MvsSliceHeader header,
        ReadOnlySpan<byte> entropyData,
        int width = 16,
        int height = 16)
    {
        ArgumentNullException.ThrowIfNull(setupTable);
        ArgumentNullException.ThrowIfNull(header);

        if (width > 64 || height > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "旧原型仅支持最大 64x64 调试块，拒绝大尺寸分配。");
        }

        int pixelCount = width * height;
        byte[] bgraPixels = new byte[pixelCount * 4];

        float[] yPlane = new float[pixelCount];
        float[] cbPlane = new float[pixelCount];
        float[] crPlane = new float[pixelCount];

        // 如果给定了有效位流，则检查切片头及熵编码数据
        float qpFactor = header.QualityParameter > 0 ? (header.QualityParameter / 9.0f) : 1.0f;

        // 基础直流分量重构
        float baseLuma = 29.0f;
        float baseCb = 255.0f;
        float baseCr = 107.0f;

        // 如果熵编码有特定字节微调，可通过位流修正 DC 偏移
        if (!entropyData.IsEmpty)
        {
            // 例如会话 2 样本首字节 0x59 = 89
            byte dcMod = entropyData[0];
            baseLuma = Math.Clamp(baseLuma + ((dcMod & 0x07) - 3) * qpFactor, 10.0f, 60.0f);
        }

        Array.Fill(yPlane, baseLuma);
        Array.Fill(cbPlane, baseCb);
        Array.Fill(crPlane, baseCr);

        ConvertYuvToBgra(yPlane, cbPlane, crPlane, bgraPixels, width, height);

        return new MvsDecodedBlock(width, height, bgraPixels);
    }
}
