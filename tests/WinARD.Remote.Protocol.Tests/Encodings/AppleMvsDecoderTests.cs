using System.Buffers.Binary;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Encodings.Mvs;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Encodings;

public sealed class AppleMvsDecoderTests
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

    private static readonly byte[] RealSlicePayload14 =
    [
        0x00, 0x0F,             // block dimension = 15 (16x16)
        0x19, 0x00,             // magic = 0x1900
        0x00, 0x09,             // QP = 9
        0x59, 0x36, 0x80, 0x1C, 0xB5, 0xEA, 0x2E, 0xDA
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

    private static readonly byte[] RealBlackSlicePayload13 =
    [
        0x00, 0x0F, 0x19, 0x00, 0x00, 0x09,
        0x41, 0x8B, 0x68, 0x00, 0x60, 0x81, 0xB4
    ];

    private static readonly byte[] RealTextGridDenseSlicePayload36 =
    [
        0x00, 0x0F, 0x19, 0x00, 0x00, 0x09,
        0x48, 0xA3, 0x68, 0x80, 0x60, 0x81, 0xE2, 0x08, 0x20, 0x1F, 0xDF, 0xDF, 0xDF, 0xDF, 0xDF, 0xDF, 0xC0, 0x18, 0x20, 0xC0, 0x1F, 0xDF, 0xDF, 0xDF, 0xDF, 0xDF, 0xDF, 0xDF, 0xDB, 0x40
    ];

    private static byte[] BuildStreamWithLength(byte[] payload)
    {
        byte[] buffer = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, sizeof(uint)), (uint)payload.Length);
        payload.CopyTo(buffer.AsSpan(sizeof(uint)));
        return buffer;
    }

    [Fact]
    public async Task DecodeAsync_ValidSetupControl_InitializesDecoderState()
    {
        AppleMvsDecoder decoder = new();
        byte[] wire = BuildStreamWithLength(RealSetupPayload129);
        using MemoryStream stream = new(wire);
        RfbReader reader = new(stream, ProtocolLimits.Default);
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        FramebufferRect controlRect = FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0);
        EncodingDecodeResult result = await decoder.DecodeAsync(reader, fb, controlRect, CancellationToken.None);

        Assert.True(decoder.HasSetup);
        Assert.Equal(64, decoder.LuminanceTable.Length);
        Assert.Equal(64, decoder.ChrominanceTable.Length);
        Assert.Equal(12, decoder.LuminanceTable[0]);
        Assert.Equal(15, decoder.ChrominanceTable[0]);
        Assert.Equal(75, decoder.ChrominanceTable[63]);

        Assert.Empty(result.DirtyRects);
        Assert.NotNull(result.TransferStatistics);
        Assert.Equal(133, result.TransferStatistics.Value.WirePayloadBytes);
    }

    [Fact]
    public async Task ResetState_ClearsTablesAndResetsSetupFlag()
    {
        AppleMvsDecoder decoder = new();
        byte[] wire = BuildStreamWithLength(RealSetupPayload129);
        using MemoryStream stream = new(wire);
        RfbReader reader = new(stream, ProtocolLimits.Default);
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        await decoder.DecodeAsync(reader, fb, FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0), CancellationToken.None);
        Assert.True(decoder.HasSetup);

        decoder.ResetState();
        Assert.False(decoder.HasSetup);
        Assert.True(decoder.LuminanceTable.IsEmpty);
        Assert.True(decoder.ChrominanceTable.IsEmpty);
    }

    [Fact]
    public async Task DecodeAsync_SetupInvalidLength_ThrowsRfbProtocolException()
    {
        AppleMvsDecoder decoder = new();
        byte[] invalidPayload = new byte[50];
        byte[] wire = BuildStreamWithLength(invalidPayload);
        using MemoryStream stream = new(wire);
        RfbReader reader = new(stream, ProtocolLimits.Default);
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        FramebufferRect controlRect = FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0);
        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            decoder.DecodeAsync(reader, fb, controlRect, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DecodeAsync_SetupUnsupportedTableCount_ThrowsRfbProtocolException()
    {
        AppleMvsDecoder decoder = new();
        byte[] invalidPayload = (byte[])RealSetupPayload129.Clone();
        invalidPayload[0] = 1; // Not 2

        byte[] wire = BuildStreamWithLength(invalidPayload);
        using MemoryStream stream = new(wire);
        RfbReader reader = new(stream, ProtocolLimits.Default);
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        FramebufferRect controlRect = FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0);
        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            decoder.DecodeAsync(reader, fb, controlRect, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DecodeAsync_SliceBeforeSetup_ThrowsRfbProtocolException()
    {
        AppleMvsDecoder decoder = new();
        byte[] wire = BuildStreamWithLength(RealSlicePayload14);
        using MemoryStream stream = new(wire);
        RfbReader reader = new(stream, ProtocolLimits.Default);
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        FramebufferRect sliceRect = new(0, 0, 16, 16);
        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            decoder.DecodeAsync(reader, fb, sliceRect, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DecodeAsync_UnverifiedSliceBitstream_ExplicitlyRejected()
    {
        AppleMvsDecoder decoder = new();
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        // 1. Send Setup
        byte[] setupWire = BuildStreamWithLength(RealSetupPayload129);
        using (MemoryStream setupStream = new(setupWire))
        {
            RfbReader setupReader = new(setupStream, ProtocolLimits.Default);
            FramebufferRect controlRect = FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0);
            await decoder.DecodeAsync(setupReader, fb, controlRect, CancellationToken.None);
        }

        // 2. Send Unverified Slice (8 bytes entropy)
        byte[] sliceWire = BuildStreamWithLength(RealSlicePayload14);
        using (MemoryStream sliceStream = new(sliceWire))
        {
            RfbReader sliceReader = new(sliceStream, ProtocolLimits.Default);
            FramebufferRect sliceRect = new(100, 200, 16, 16);

            var ex = await Assert.ThrowsAsync<RfbProtocolException>(() =>
                decoder.DecodeAsync(sliceReader, fb, sliceRect, CancellationToken.None).AsTask());

            Assert.Contains("Unsupported or unverified MVS entropy bitstream syntax", ex.Message);
        }
    }

    [Fact]
    public async Task DecodeAsync_ArbitraryHeaderOnlySlice_IsExplicitlyRejected()
    {
        // 验证传入非法 magic 的切片头时，会被严格拒绝
        AppleMvsDecoder decoder = new();
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        byte[] setupWire = BuildStreamWithLength(RealSetupPayload129);
        using (MemoryStream setupStream = new(setupWire))
        {
            RfbReader setupReader = new(setupStream, ProtocolLimits.Default);
            await decoder.DecodeAsync(setupReader, fb, FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0), CancellationToken.None);
        }

        byte[] arbitraryHeaderSlice = [0x00, 0x0F, 0x12, 0x34, 0x00, 0x05];
        byte[] sliceWire = BuildStreamWithLength(arbitraryHeaderSlice);
        using (MemoryStream sliceStream = new(sliceWire))
        {
            RfbReader sliceReader = new(sliceStream, ProtocolLimits.Default);
            FramebufferRect sliceRect = new(0, 0, 16, 16);

            var ex = await Assert.ThrowsAsync<RfbProtocolException>(() =>
                decoder.DecodeAsync(sliceReader, fb, sliceRect, CancellationToken.None).AsTask());

            Assert.Contains("macroblock decoding failed", ex.Message);
        }
    }

    [Fact]
    public async Task DecodeAsync_RealSolidRedSlice_ReconstructsDominantRedPixels()
    {
        AppleMvsDecoder decoder = new();
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        byte[] setupWire = BuildStreamWithLength(RealSetupPayload129);
        using (MemoryStream setupStream = new(setupWire))
        {
            RfbReader setupReader = new(setupStream, ProtocolLimits.Default);
            await decoder.DecodeAsync(setupReader, fb, FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0), CancellationToken.None);
        }

        byte[] redWire = BuildStreamWithLength(RealRedSlicePayload27);
        using (MemoryStream redStream = new(redWire))
        {
            RfbReader redReader = new(redStream, ProtocolLimits.Default);
            FramebufferRect sliceRect = new(16, 32, 16, 16);
            await decoder.DecodeAsync(redReader, fb, sliceRect, CancellationToken.None);

            // 采样切片整块区域：四角与中心
            (int dx, int dy)[] samplePoints = [(0, 0), (15, 0), (0, 15), (15, 15), (8, 8), (4, 4), (12, 12)];
            foreach (var (dx, dy) in samplePoints)
            {
                uint pixel = fb.GetBgra32(16 + dx, 32 + dy);
                byte blue = (byte)(pixel & 0xFF);
                byte green = (byte)((pixel >> 8) & 0xFF);
                byte red = (byte)((pixel >> 16) & 0xFF);
                byte alpha = (byte)((pixel >> 24) & 0xFF);

                Assert.Equal(255, alpha);
                Assert.True(red > 240, $"Point ({dx},{dy}): Expected dominant Red > 240, but got {red}.");
                Assert.True(green < 20, $"Point ({dx},{dy}): Expected suppressed Green < 20, but got {green}.");
                Assert.True(blue < 20, $"Point ({dx},{dy}): Expected suppressed Blue < 20, but got {blue}.");
            }
        }
    }

    [Fact]
    public async Task DecodeAsync_RealSolidGreenSlice_ReconstructsDominantGreenPixels()
    {
        AppleMvsDecoder decoder = new();
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        byte[] setupWire = BuildStreamWithLength(RealSetupPayload129);
        using (MemoryStream setupStream = new(setupWire))
        {
            RfbReader setupReader = new(setupStream, ProtocolLimits.Default);
            await decoder.DecodeAsync(setupReader, fb, FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0), CancellationToken.None);
        }

        byte[] greenWire = BuildStreamWithLength(RealGreenSlicePayload27);
        using (MemoryStream greenStream = new(greenWire))
        {
            RfbReader greenReader = new(greenStream, ProtocolLimits.Default);
            FramebufferRect sliceRect = new(0, 0, 16, 16);
            await decoder.DecodeAsync(greenReader, fb, sliceRect, CancellationToken.None);

            (int dx, int dy)[] samplePoints = [(0, 0), (15, 0), (0, 15), (15, 15), (8, 8)];
            foreach (var (dx, dy) in samplePoints)
            {
                uint pixel = fb.GetBgra32(dx, dy);
                byte blue = (byte)(pixel & 0xFF);
                byte green = (byte)((pixel >> 8) & 0xFF);
                byte red = (byte)((pixel >> 16) & 0xFF);
                byte alpha = (byte)((pixel >> 24) & 0xFF);

                Assert.Equal(255, alpha);
                Assert.True(green > 240, $"Point ({dx},{dy}): Expected dominant Green > 240, but got {green}.");
                Assert.True(red < 20, $"Point ({dx},{dy}): Expected suppressed Red < 20, but got {red}.");
                Assert.True(blue < 20, $"Point ({dx},{dy}): Expected suppressed Blue < 20, but got {blue}.");
            }
        }
    }

    [Fact]
    public async Task DecodeAsync_RealSolidBlueSlice_ReconstructsDominantBluePixels()
    {
        AppleMvsDecoder decoder = new();
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        byte[] setupWire = BuildStreamWithLength(RealSetupPayload129);
        using (MemoryStream setupStream = new(setupWire))
        {
            RfbReader setupReader = new(setupStream, ProtocolLimits.Default);
            await decoder.DecodeAsync(setupReader, fb, FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0), CancellationToken.None);
        }

        byte[] blueWire = BuildStreamWithLength(RealBlueSlicePayload27);
        using (MemoryStream blueStream = new(blueWire))
        {
            RfbReader blueReader = new(blueStream, ProtocolLimits.Default);
            FramebufferRect sliceRect = new(0, 0, 16, 16);
            await decoder.DecodeAsync(blueReader, fb, sliceRect, CancellationToken.None);

            (int dx, int dy)[] samplePoints = [(0, 0), (15, 0), (0, 15), (15, 15), (8, 8)];
            foreach (var (dx, dy) in samplePoints)
            {
                uint pixel = fb.GetBgra32(dx, dy);
                byte blue = (byte)(pixel & 0xFF);
                byte green = (byte)((pixel >> 8) & 0xFF);
                byte red = (byte)((pixel >> 16) & 0xFF);
                byte alpha = (byte)((pixel >> 24) & 0xFF);

                Assert.Equal(255, alpha);
                Assert.True(blue > 240, $"Point ({dx},{dy}): Expected dominant Blue > 240, but got {blue}.");
                Assert.True(red < 20, $"Point ({dx},{dy}): Expected suppressed Red < 20, but got {red}.");
                Assert.True(green < 20, $"Point ({dx},{dy}): Expected suppressed Green < 20, but got {green}.");
            }
        }
    }

    [Fact]
    public async Task DecodeAsync_RealSolidWhiteSlice_ReconstructsHighLuminancePixels()
    {
        AppleMvsDecoder decoder = new();
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        byte[] setupWire = BuildStreamWithLength(RealSetupPayload129);
        using (MemoryStream setupStream = new(setupWire))
        {
            RfbReader setupReader = new(setupStream, ProtocolLimits.Default);
            await decoder.DecodeAsync(setupReader, fb, FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0), CancellationToken.None);
        }

        byte[] whiteWire = BuildStreamWithLength(RealWhiteSlicePayload13);
        using (MemoryStream whiteStream = new(whiteWire))
        {
            RfbReader whiteReader = new(whiteStream, ProtocolLimits.Default);
            FramebufferRect sliceRect = new(0, 0, 16, 16);
            await decoder.DecodeAsync(whiteReader, fb, sliceRect, CancellationToken.None);

            (int dx, int dy)[] samplePoints = [(0, 0), (15, 0), (0, 15), (15, 15), (8, 8)];
            foreach (var (dx, dy) in samplePoints)
            {
                uint pixel = fb.GetBgra32(dx, dy);
                byte blue = (byte)(pixel & 0xFF);
                byte green = (byte)((pixel >> 8) & 0xFF);
                byte red = (byte)((pixel >> 16) & 0xFF);
                byte alpha = (byte)((pixel >> 24) & 0xFF);

                Assert.Equal(255, alpha);
                Assert.True(red > 240, $"Point ({dx},{dy}): Expected high Red > 240, but got {red}.");
                Assert.True(green > 240, $"Point ({dx},{dy}): Expected high Green > 240, but got {green}.");
                Assert.True(blue > 240, $"Point ({dx},{dy}): Expected high Blue > 240, but got {blue}.");
            }
        }
    }

    [Fact]
    public async Task DecodeAsync_RealSolidBlackSlice_ReconstructsLowLuminancePixels()
    {
        AppleMvsDecoder decoder = new();
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);

        byte[] setupWire = BuildStreamWithLength(RealSetupPayload129);
        using (MemoryStream setupStream = new(setupWire))
        {
            RfbReader setupReader = new(setupStream, ProtocolLimits.Default);
            await decoder.DecodeAsync(setupReader, fb, FramebufferRect.CreateMetadataRectangle(0, 0, 0, 0), CancellationToken.None);
        }

        byte[] blackWire = BuildStreamWithLength(RealBlackSlicePayload13);
        using (MemoryStream blackStream = new(blackWire))
        {
            RfbReader blackReader = new(blackStream, ProtocolLimits.Default);
            FramebufferRect sliceRect = new(0, 0, 16, 16);
            await decoder.DecodeAsync(blackReader, fb, sliceRect, CancellationToken.None);

            (int dx, int dy)[] samplePoints = [(0, 0), (15, 0), (0, 15), (15, 15), (8, 8)];
            foreach (var (dx, dy) in samplePoints)
            {
                uint pixel = fb.GetBgra32(dx, dy);
                byte blue = (byte)(pixel & 0xFF);
                byte green = (byte)((pixel >> 8) & 0xFF);
                byte red = (byte)((pixel >> 16) & 0xFF);
                byte alpha = (byte)((pixel >> 24) & 0xFF);

                Assert.Equal(255, alpha);
                Assert.True(red < 15, $"Point ({dx},{dy}): Expected low Red < 15, but got {red}.");
                Assert.True(green < 15, $"Point ({dx},{dy}): Expected low Green < 15, but got {green}.");
                Assert.True(blue < 15, $"Point ({dx},{dy}): Expected low Blue < 15, but got {blue}.");
            }
        }
    }

    [Fact]
    public async Task SessionIntegration_WhenResearchDecoderInjected_DecodesSetupAndSliceSuccessfully()
    {
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);
        await using FramebufferUpdateSession session = FramebufferUpdateReader.CreateSession(
            fb,
            PixelFormat.WinArdBgra32);

        // 显式研究注入
        AppleMvsDecoder decoder = new();
        session.RegisterResearchDecoder(decoder);

        byte[] packetData = BuildTwoRectanglePacket(
            RealSetupPayload129,
            RealRedSlicePayload27,
            32,
            64,
            16,
            16);

        using MemoryStream stream = new(packetData);

        var result = await session.ApplyAsync(stream, CancellationToken.None);

        Assert.Single(result.DirtyRects);
        Assert.Equal(new FramebufferRect(32, 64, 16, 16), result.DirtyRects[0]);
    }

    [Fact]
    public async Task DefaultOneShotSession_AppleMvsRejectedWithUnsupportedEncoding()
    {
        using FramebufferModel fb = new(1920, 1080, ProtocolLimits.Default);
        byte[] packetData = BuildOneRectanglePacket(RealSetupPayload129, 0, 0, 0, 0);

        using MemoryStream stream = new(packetData);

        RfbProtocolException ex = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(stream, fb, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Equal(RfbProtocolFailureKind.UnsupportedEncoding, ex.Failure?.Kind);
    }

    private static byte[] BuildTwoRectanglePacket(
        byte[] setupPayload,
        byte[] slicePayload,
        ushort sliceX,
        ushort sliceY,
        ushort sliceW,
        ushort sliceH)
    {
        using MemoryStream stream = new();
        stream.WriteByte(0); // MessageType
        stream.WriteByte(0); // Padding

        byte[] u16Buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(u16Buffer, 2); // 2 rectangles
        stream.Write(u16Buffer);

        // Rect 1 (Setup)
        WriteRectangleHeader(stream, 0, 0, 0, 0, 1011);
        byte[] setupWire = BuildStreamWithLength(setupPayload);
        stream.Write(setupWire);

        // Rect 2 (Slice)
        WriteRectangleHeader(stream, sliceX, sliceY, sliceW, sliceH, 1011);
        byte[] sliceWire = BuildStreamWithLength(slicePayload);
        stream.Write(sliceWire);

        return stream.ToArray();
    }

    private static byte[] BuildOneRectanglePacket(
        byte[] payload,
        ushort x,
        ushort y,
        ushort width,
        ushort height)
    {
        using MemoryStream stream = new();
        stream.WriteByte(0);
        stream.WriteByte(0);

        byte[] u16Buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(u16Buffer, 1);
        stream.Write(u16Buffer);

        WriteRectangleHeader(stream, x, y, width, height, 1011);
        byte[] wire = BuildStreamWithLength(payload);
        stream.Write(wire);

        return stream.ToArray();
    }

    private static void WriteRectangleHeader(
        Stream stream,
        ushort x,
        ushort y,
        ushort width,
        ushort height,
        int encodingId)
    {
        byte[] u16 = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(u16, x);
        stream.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, y);
        stream.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, width);
        stream.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, height);
        stream.Write(u16);

        byte[] i32 = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(i32, encodingId);
        stream.Write(i32);
    }

    [Fact]
    public void Header_without_entropy_is_not_a_decoded_macroblock()
    {
        byte[] payload = [0x00, 0x0F, 0x19, 0x00, 0x00, 0x0A];
        byte[] table = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] output = new byte[16 * 16 * 4];
        Assert.Throws<FormatException>(() =>
            MvsMacroblockParser.DecodeMacroblock(payload, table, table, output));
    }

    [Fact]
    public void Arbitrary_unsupported_entropy_payload_is_explicitly_rejected()
    {
        byte[] payload = [0x00, 0x0F, 0x19, 0x00, 0x00, 0x0A, 0xFE, 0xDC, 0xBA, 0x98];
        byte[] table = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] output = new byte[16 * 16 * 4];
        Assert.Throws<FormatException>(() =>
            MvsMacroblockParser.DecodeMacroblock(payload, table, table, output));
    }

    [Fact]
    public void DecodeMacroblock_PayloadShorterThan6Bytes_ThrowsFormatException()
    {
        byte[] payload = [0x00, 0x0F, 0x19];
        byte[] table = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] output = new byte[16 * 16 * 4];
        Assert.Throws<FormatException>(() =>
            MvsMacroblockParser.DecodeMacroblock(payload, table, table, output));
    }

    [Fact]
    public void DecodeMacroblock_InvalidMagic_ThrowsFormatException()
    {
        byte[] payload = [0x00, 0x0F, 0x18, 0xFF, 0x00, 0x0A, 0x31, 0x08, 0x36, 0x80, 0x7F, 0x00, 0x6D];
        byte[] table = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] output = new byte[16 * 16 * 4];
        Assert.Throws<FormatException>(() =>
            MvsMacroblockParser.DecodeMacroblock(payload, table, table, output));
    }

    [Fact]
    public void DecodeMacroblock_InvalidBlockDimension_ThrowsFormatException()
    {
        byte[] payload = [0x00, 0x07, 0x19, 0x00, 0x00, 0x0A, 0x31, 0x08, 0x36, 0x80, 0x7F, 0x00, 0x6D];
        byte[] table = Enumerable.Repeat((byte)1, 64).ToArray();
        byte[] output = new byte[16 * 16 * 4];
        Assert.Throws<FormatException>(() =>
            MvsMacroblockParser.DecodeMacroblock(payload, table, table, output));
    }

    [Fact]
    public void DecodeMacroblock_TextGridDense_ReconstructsSpatialVariationWithoutConstantFill()
    {
        byte[] output = new byte[16 * 16 * 4];
        MvsMacroblockParser.DecodeMacroblock(
            RealTextGridDenseSlicePayload36,
            RealSetupPayload129.AsSpan(1, 64).ToArray(),
            RealSetupPayload129.AsSpan(65, 64).ToArray(),
            output);

        // 验证非整块恒定常数：计算亮度的最大差异
        byte minB = 255, maxB = 0;
        for (int i = 0; i < 256; i++)
        {
            byte b = output[i * 4];
            if (b < minB) minB = b;
            if (b > maxB) maxB = b;
        }

        Assert.True(maxB - minB > 10, $"Expected spatial variation across block, but max-min was {maxB - minB}.");
    }

    [Fact]
    public void DecodeMacroblock_QuantizationTable_MustScaleReconstructedCoefficients()
    {
        byte[] output1 = new byte[16 * 16 * 4];
        byte[] output2 = new byte[16 * 16 * 4];

        byte[] table1 = Enumerable.Repeat((byte)12, 64).ToArray();
        byte[] table2 = Enumerable.Repeat((byte)24, 64).ToArray();

        MvsMacroblockParser.DecodeMacroblock(RealTextGridDenseSlicePayload36, table1, table1, output1);
        MvsMacroblockParser.DecodeMacroblock(RealTextGridDenseSlicePayload36, table2, table2, output2);

        // 如果量化表生效，两个输出块的差值绝不能处处为 0
        bool anyDifference = false;
        for (int i = 0; i < output1.Length; i++)
        {
            if (output1[i] != output2[i])
            {
                anyDifference = true;
                break;
            }
        }

        Assert.True(anyDifference, "Changing quantization table must affect decoded macroblock pixels.");
    }
}
