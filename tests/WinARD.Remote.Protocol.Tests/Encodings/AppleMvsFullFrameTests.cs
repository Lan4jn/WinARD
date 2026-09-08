using System.Buffers.Binary;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Encodings.Mvs;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Encodings;

public sealed class AppleMvsFullFrameTests
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

    private static readonly byte[] RealRedSlicePayload27 =
    [
        0x00, 0x0F, 0x19, 0x00, 0x00, 0x0A,
        0x41, 0x41, 0x36, 0x80, 0x80, 0x60, 0x81, 0xA1, 0x7D,
        0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x06, 0x85, 0xF7, 0x6D
    ];

    private static readonly byte[] RealGreenSlicePayload27 =
    [
        0x00, 0x0F, 0x19, 0x00, 0x00, 0x0A,
        0x41, 0x41, 0x36, 0x80, 0x80, 0x60, 0x82, 0xFD, 0x04,
        0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x0B, 0xF4, 0x13, 0x6D
    ];

    private static readonly byte[] RealBlueSlicePayload27 =
    [
        0x00, 0x0F, 0x19, 0x00, 0x00, 0x0A,
        0x41, 0x41, 0x36, 0x80, 0x80, 0x60, 0x80, 0x73, 0xF6,
        0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0xCF, 0xDB, 0x6D
    ];

    private static readonly byte[] RealWhiteSlicePayload13 =
    [
        0x00, 0x0F, 0x19, 0x00, 0x00, 0x0A,
        0x31, 0x08, 0x36, 0x80, 0x7F, 0x00, 0x6D
    ];

    /// <summary>
    /// 明确标注（依据通用解码 Spec 第 4 节与开发计划 G2.3）：
    /// 此测试验证 FramebufferUpdateSession 对多个 16x16 切片小矩形坐标的写入位置与脏矩形合并组装能力，
    /// 证明客户端能够正确拼装多个独立的 RFB 切片矩形，但不作为真实全屏大切片单载荷语法的证明。
    /// </summary>
    [Fact]
    public async Task ApplyAsync_MultipleConsecutiveMacroblocks_DecodesAndComposesAccurately()
    {
        using FramebufferModel fb = new(640, 480, ProtocolLimits.Default);
        await using FramebufferUpdateSession session = FramebufferUpdateReader.CreateSession(
            fb,
            PixelFormat.WinArdBgra32);

        AppleMvsDecoder decoder = new();
        session.RegisterResearchDecoder(decoder);

        // 构造包含 1 个 Setup 矩形与 4 个切片矩形的 RFB FramebufferUpdate 消息包
        // 4 个切片并排分布在坐标:
        // 切片 1: (0, 0, 16, 16)   -> 红
        // 切片 2: (16, 0, 16, 16)  -> 绿
        // 切片 3: (32, 0, 16, 16)  -> 蓝
        // 切片 4: (48, 0, 16, 16)  -> 白
        byte[] packetData = BuildUpdatePacket(
            [
                (0, 0, 0, 0, RealSetupPayload129),
                (0, 0, 16, 16, RealRedSlicePayload27),
                (16, 0, 16, 16, RealGreenSlicePayload27),
                (32, 0, 16, 16, RealBlueSlicePayload27),
                (48, 0, 16, 16, RealWhiteSlicePayload13),
            ]);

        using MemoryStream stream = new(packetData);
        FramebufferUpdateResult result = await session.ApplyAsync(stream, CancellationToken.None);

        Assert.Equal(4, result.DirtyRects.Count);
        Assert.Contains(new FramebufferRect(0, 0, 16, 16), result.DirtyRects);
        Assert.Contains(new FramebufferRect(16, 0, 16, 16), result.DirtyRects);
        Assert.Contains(new FramebufferRect(32, 0, 16, 16), result.DirtyRects);
        Assert.Contains(new FramebufferRect(48, 0, 16, 16), result.DirtyRects);

        // 验证切片 1 (红)
        uint redPixel = fb.GetBgra32(8, 8);
        Assert.True((byte)((redPixel >> 16) & 0xFF) > 200, "Block 1 must be red");
        Assert.True((byte)((redPixel >> 8) & 0xFF) < 60, "Block 1 must have low green");

        // 验证切片 2 (绿)
        uint greenPixel = fb.GetBgra32(16 + 8, 8);
        Assert.True((byte)((greenPixel >> 8) & 0xFF) > 200, "Block 2 must be green");
        Assert.True((byte)((greenPixel >> 16) & 0xFF) < 60, "Block 2 must have low red");

        // 验证切片 3 (蓝)
        uint bluePixel = fb.GetBgra32(32 + 8, 8);
        Assert.True((byte)(bluePixel & 0xFF) > 200, "Block 3 must be blue");
        Assert.True((byte)((bluePixel >> 16) & 0xFF) < 60, "Block 3 must have low red");

        // 验证切片 4 (白)
        uint whitePixel = fb.GetBgra32(48 + 8, 8);
        Assert.True((byte)((whitePixel >> 16) & 0xFF) > 200, "Block 4 must be white");
        Assert.True((byte)((whitePixel >> 8) & 0xFF) > 200, "Block 4 must be white");
        Assert.True((byte)(whitePixel & 0xFF) > 200, "Block 4 must be white");
    }

    [Fact]
    public async Task ApplyAsync_EdgeCroppedMacroblock_ClipsAndRendersSafely()
    {
        using FramebufferModel fb = new(640, 480, ProtocolLimits.Default);
        await using FramebufferUpdateSession session = FramebufferUpdateReader.CreateSession(
            fb,
            PixelFormat.WinArdBgra32);

        AppleMvsDecoder decoder = new();
        session.RegisterResearchDecoder(decoder);

        // 模拟屏幕边缘裁剪：宽度 16，高度仅 8 像素 (16x8 边缘切片)
        byte[] packetData = BuildUpdatePacket(
            [
                (0, 0, 0, 0, RealSetupPayload129),
                (100, 200, 16, 8, RealRedSlicePayload27),
            ]);

        using MemoryStream stream = new(packetData);
        FramebufferUpdateResult result = await session.ApplyAsync(stream, CancellationToken.None);

        Assert.Single(result.DirtyRects);
        Assert.Equal(new FramebufferRect(100, 200, 16, 8), result.DirtyRects[0]);

        // 验证有效内部像素 (100+4, 200+4) 处被赋予红色
        uint insidePixel = fb.GetBgra32(104, 204);
        Assert.True((byte)((insidePixel >> 16) & 0xFF) > 200, "Inside clipped rect must be red");

        // 验证矩形外部 (104, 200+10) 没有被越界改写 (保持未写入状态: Red=0, Alpha=255)
        uint outsidePixel = fb.GetBgra32(104, 210);
        Assert.Equal(0xFF000000u, outsidePixel);
        Assert.Equal(0, (byte)((outsidePixel >> 16) & 0xFF));
    }

    [Fact]
    public async Task ApplyAsync_SetupReusedAcrossMultipleUpdates_StatePersists()
    {
        using FramebufferModel fb = new(640, 480, ProtocolLimits.Default);
        await using FramebufferUpdateSession session = FramebufferUpdateReader.CreateSession(
            fb,
            PixelFormat.WinArdBgra32);

        AppleMvsDecoder decoder = new();
        session.RegisterResearchDecoder(decoder);

        // 第 1 帧更新：只发 Setup
        byte[] setupFrame = BuildUpdatePacket([(0, 0, 0, 0, RealSetupPayload129)]);
        using (MemoryStream stream1 = new(setupFrame))
        {
            FramebufferUpdateResult r1 = await session.ApplyAsync(stream1, CancellationToken.None);
            Assert.Empty(r1.DirtyRects);
        }

        // 第 2 帧更新：只发 Slice（复用上一帧的 Setup 状态）
        byte[] sliceFrame = BuildUpdatePacket([(0, 0, 16, 16, RealBlueSlicePayload27)]);
        using (MemoryStream stream2 = new(sliceFrame))
        {
            FramebufferUpdateResult r2 = await session.ApplyAsync(stream2, CancellationToken.None);
            Assert.Single(r2.DirtyRects);
            Assert.Equal(new FramebufferRect(0, 0, 16, 16), r2.DirtyRects[0]);

            uint bluePixel = fb.GetBgra32(8, 8);
            Assert.True((byte)(bluePixel & 0xFF) > 200, "Reused setup must decode blue accurately");
        }
    }

    [Fact]
    public void FastIdct8x8_NonZeroAcCoefficients_ProducesSpatialVariation()
    {
        // 依据通用解码 Spec 第 2 节：仅有 DC 重建不能宣称支持纹理模式。
        // 当频域输入存在交流分量 (AC) 时，2D-IDCT 输出空域像素必须呈现显著的空间变化，杜绝常量平面。
        Span<float> dctCoeffs = stackalloc float[64];
        dctCoeffs.Clear();
        dctCoeffs[0] = 128.0f * 8.0f; // DC 基准
        dctCoeffs[1] = 50.0f;          // 水平一阶 AC 交流分量
        dctCoeffs[8] = 50.0f;          // 垂直一阶 AC 交流分量

        Span<float> spatialPixels = stackalloc float[64];
        MvsMacroblockParser.FastIdct8x8(dctCoeffs, spatialPixels);

        // 验证空间不同位置存在显著阶跃/差异，而非单一常数
        float topLeft = spatialPixels[0];
        float topRight = spatialPixels[7];
        float bottomLeft = spatialPixels[56];
        float bottomRight = spatialPixels[63];

        Assert.NotEqual(topLeft, topRight);
        Assert.NotEqual(topLeft, bottomLeft);
        Assert.True(MathF.Abs(topLeft - bottomRight) > 10.0f, "AC coefficients must produce measurable spatial variation across the block.");
    }

    [Fact]
    public async Task DecodeAsync_LargeMultiBlockRectangle_StreamsAndAppliesAllMacroblocks()
    {
        AppleMvsDecoder decoder = new();
        using FramebufferModel fb = new(64, 64, ProtocolLimits.Default);

        // 先发送 Setup 初始化双量化表
        byte[] setupWire = BuildStreamWithLength(RealSetupPayload129);
        using MemoryStream setupStream = new(setupWire);
        RfbReader setupReader = new(setupStream, ProtocolLimits.Default);
        await decoder.DecodeAsync(setupReader, fb, FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0), CancellationToken.None);

        // 构造一个 32x32 的大矩形，内部包含 4 个 16x16 宏块切片:
        // (0,0): Red, (1,0): Green, (0,1): Blue, (1,1): White
        byte[] redWire = BuildStreamWithLength(RealRedSlicePayload27);
        byte[] greenWire = BuildStreamWithLength(RealGreenSlicePayload27);
        byte[] blueWire = BuildStreamWithLength(RealBlueSlicePayload27);
        byte[] whiteWire = BuildStreamWithLength(RealWhiteSlicePayload13);

        byte[] multiBlockPayload = new byte[redWire.Length + greenWire.Length + blueWire.Length + whiteWire.Length];
        int offset = 0;
        redWire.CopyTo(multiBlockPayload.AsSpan(offset)); offset += redWire.Length;
        greenWire.CopyTo(multiBlockPayload.AsSpan(offset)); offset += greenWire.Length;
        blueWire.CopyTo(multiBlockPayload.AsSpan(offset)); offset += blueWire.Length;
        whiteWire.CopyTo(multiBlockPayload.AsSpan(offset));

        byte[] largeRectWire = BuildStreamWithLength(multiBlockPayload);
        using MemoryStream largeRectStream = new(largeRectWire);
        RfbReader largeReader = new(largeRectStream, ProtocolLimits.Default);

        FramebufferRect largeRect = new(0, 0, 32, 32);
        EncodingDecodeResult result = await decoder.DecodeAsync(largeReader, fb, largeRect, CancellationToken.None);

        Assert.Single(result.DirtyRects);
        Assert.Equal(largeRect, result.DirtyRects[0]);

        // 验证 4 个象限的像素
        // 象限 1 (Top-Left 8,8): Red
        uint pixelTL = fb.GetBgra32(8, 8);
        Assert.True((byte)((pixelTL >> 16) & 0xFF) > 200, "Top-Left quadrant must be dominant Red");

        // 象限 2 (Top-Right 24,8): Green
        uint pixelTR = fb.GetBgra32(24, 8);
        Assert.True((byte)((pixelTR >> 8) & 0xFF) > 200, "Top-Right quadrant must be dominant Green");

        // 象限 3 (Bottom-Left 8,24): Blue
        uint pixelBL = fb.GetBgra32(8, 24);
        Assert.True((byte)(pixelBL & 0xFF) > 200, "Bottom-Left quadrant must be dominant Blue");

        // 象限 4 (Bottom-Right 24,24): White
        uint pixelBR = fb.GetBgra32(24, 24);
        Assert.True((byte)((pixelBR >> 16) & 0xFF) > 200, "Bottom-Right quadrant must be dominant White");
    }

    [Fact]
    public async Task DecodeAsync_ClippedEdgeMultiBlockRectangle_ClipsCorrectlyAndPreservesPixels()
    {
        AppleMvsDecoder decoder = new();
        using FramebufferModel fb = new(64, 64, ProtocolLimits.Default);

        byte[] setupWire = BuildStreamWithLength(RealSetupPayload129);
        using MemoryStream setupStream = new(setupWire);
        RfbReader setupReader = new(setupStream, ProtocolLimits.Default);
        await decoder.DecodeAsync(setupReader, fb, FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0), CancellationToken.None);

        // 构造一个 28x20 矩形 (横向 16+12, 纵向 16+4)
        byte[] redWire = BuildStreamWithLength(RealRedSlicePayload27);
        byte[] greenWire = BuildStreamWithLength(RealGreenSlicePayload27);
        byte[] blueWire = BuildStreamWithLength(RealBlueSlicePayload27);
        byte[] whiteWire = BuildStreamWithLength(RealWhiteSlicePayload13);

        byte[] multiBlockPayload = new byte[redWire.Length + greenWire.Length + blueWire.Length + whiteWire.Length];
        int offset = 0;
        redWire.CopyTo(multiBlockPayload.AsSpan(offset)); offset += redWire.Length;
        greenWire.CopyTo(multiBlockPayload.AsSpan(offset)); offset += greenWire.Length;
        blueWire.CopyTo(multiBlockPayload.AsSpan(offset)); offset += blueWire.Length;
        whiteWire.CopyTo(multiBlockPayload.AsSpan(offset));

        byte[] largeRectWire = BuildStreamWithLength(multiBlockPayload);
        using MemoryStream largeRectStream = new(largeRectWire);
        RfbReader largeReader = new(largeRectStream, ProtocolLimits.Default);

        FramebufferRect clippedRect = new(0, 0, 28, 20);
        EncodingDecodeResult result = await decoder.DecodeAsync(largeReader, fb, clippedRect, CancellationToken.None);

        Assert.Single(result.DirtyRects);
        Assert.Equal(clippedRect, result.DirtyRects[0]);

        // 边缘内测试点: (27, 19) 落在右下角 White 切片被裁剪后的有效区域内
        uint pixelEdge = fb.GetBgra32(27, 19);
        Assert.True((byte)((pixelEdge >> 16) & 0xFF) > 200, "Clipped bottom-right point (27,19) must be White");

        // 边界外测试点: (28, 20) 应保持默认黑未被涂覆 (RGB = 0)
        uint pixelOutside = fb.GetBgra32(28, 20);
        Assert.Equal(0, (byte)((pixelOutside >> 16) & 0xFF));
        Assert.Equal(0, (byte)((pixelOutside >> 8) & 0xFF));
        Assert.Equal(0, (byte)(pixelOutside & 0xFF));
    }

    private static byte[] BuildStreamWithLength(byte[] payload)
    {
        byte[] buffer = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, sizeof(uint)), (uint)payload.Length);
        payload.CopyTo(buffer.AsSpan(sizeof(uint)));
        return buffer;
    }

    private static byte[] BuildUpdatePacket(
        IReadOnlyList<(ushort X, ushort Y, ushort W, ushort H, byte[] Payload)> rects)
    {
        using MemoryStream ms = new();
        ms.WriteByte(0); // MessageType = 0 (FramebufferUpdate)
        ms.WriteByte(0); // Padding
        Span<byte> countSpan = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(countSpan, (ushort)rects.Count);
        ms.Write(countSpan);

        Span<byte> rectHeader = stackalloc byte[12];
        Span<byte> lengthSpan = stackalloc byte[4];

        foreach (var (x, y, w, h, payload) in rects)
        {
            BinaryPrimitives.WriteUInt16BigEndian(rectHeader.Slice(0, 2), x);
            BinaryPrimitives.WriteUInt16BigEndian(rectHeader.Slice(2, 2), y);
            BinaryPrimitives.WriteUInt16BigEndian(rectHeader.Slice(4, 2), w);
            BinaryPrimitives.WriteUInt16BigEndian(rectHeader.Slice(6, 2), h);
            BinaryPrimitives.WriteInt32BigEndian(rectHeader.Slice(8, 4), 1011); // Encoding 1011
            ms.Write(rectHeader);

            BinaryPrimitives.WriteUInt32BigEndian(lengthSpan, (uint)payload.Length);
            ms.Write(lengthSpan);
            ms.Write(payload);
        }

        return ms.ToArray();
    }
}
