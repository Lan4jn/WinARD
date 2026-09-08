using System.Buffers.Binary;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Framebuffer;

public sealed class AppleMvsRectangleValidationTests
{
    private sealed class MockAppleMvsDecoder : IRfbEncodingDecoder
    {
        public int EncodingId => 1011;
        public FramebufferRect LastRectangle { get; private set; }
        public int InvocationCount { get; private set; }

        public ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            FramebufferModel framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            LastRectangle = rectangle;
            return ValueTask.FromResult(EncodingDecodeResult.Empty);
        }
    }

    private static byte[] CreateFramebufferUpdate(ushort x, ushort y, ushort width, ushort height, int encodingId)
    {
        var bytes = new byte[16];
        bytes[0] = 0x00; // MessageType 0 (FramebufferUpdate)
        bytes[1] = 0x00; // Padding
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 1); // 1 rectangle

        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), x);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), y);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), height);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), encodingId);
        return bytes;
    }

    [Fact]
    public async Task DefaultSession_without_research_decoder_rejects_apple_mvs_with_unsupported_encoding()
    {
        using var framebuffer = new FramebufferModel(1920, 1080, ProtocolLimits.Default);
        await using var session = FramebufferUpdateReader.CreateSession(
            framebuffer,
            PixelFormat.WinArdBgra32);

        var data = CreateFramebufferUpdate(0, 0, 0, 0, 1011);
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            session.ApplyAsync(new MemoryStream(data), CancellationToken.None));

        Assert.Equal(RfbProtocolFailureKind.UnsupportedEncoding, exception.Failure?.Kind);
        Assert.Equal(1011, exception.Failure?.EncodingId);
    }

    [Fact]
    public async Task Update_allows_zero_dimension_apple_mvs_control_record_at_origin()
    {
        using var framebuffer = new FramebufferModel(1920, 1080, ProtocolLimits.Default);
        var decoder = new MockAppleMvsDecoder();
        await using var session = FramebufferUpdateReader.CreateSession(
            framebuffer,
            PixelFormat.WinArdBgra32);
        session.RegisterResearchDecoder(decoder);

        var data = CreateFramebufferUpdate(0, 0, 0, 0, 1011);
        var result = await session.ApplyAsync(new MemoryStream(data), CancellationToken.None);

        Assert.Equal(1, decoder.InvocationCount);
        Assert.Equal(0, decoder.LastRectangle.X);
        Assert.Equal(0, decoder.LastRectangle.Y);
        Assert.Equal(0, decoder.LastRectangle.Width);
        Assert.Equal(0, decoder.LastRectangle.Height);
        Assert.Equal(1, result.EncodingCounts[1011]);
    }

    [Fact]
    public async Task Update_allows_valid_non_zero_apple_mvs_slice_within_framebuffer()
    {
        using var framebuffer = new FramebufferModel(1920, 1080, ProtocolLimits.Default);
        var decoder = new MockAppleMvsDecoder();
        await using var session = FramebufferUpdateReader.CreateSession(
            framebuffer,
            PixelFormat.WinArdBgra32);
        session.RegisterResearchDecoder(decoder);

        var data = CreateFramebufferUpdate(100, 100, 800, 600, 1011);
        var result = await session.ApplyAsync(new MemoryStream(data), CancellationToken.None);

        Assert.Equal(1, decoder.InvocationCount);
        Assert.Equal(100, decoder.LastRectangle.X);
        Assert.Equal(100, decoder.LastRectangle.Y);
        Assert.Equal(800, decoder.LastRectangle.Width);
        Assert.Equal(600, decoder.LastRectangle.Height);
        Assert.Equal(1, result.EncodingCounts[1011]);
    }

    [Fact]
    public async Task Update_rejects_single_axis_zero_apple_mvs_rectangle()
    {
        using var framebuffer = new FramebufferModel(1920, 1080, ProtocolLimits.Default);
        var decoder = new MockAppleMvsDecoder();
        await using var session = FramebufferUpdateReader.CreateSession(
            framebuffer,
            PixelFormat.WinArdBgra32);
        session.RegisterResearchDecoder(decoder);

        var data = CreateFramebufferUpdate(0, 0, 0, 100, 1011);
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            session.ApplyAsync(new MemoryStream(data), CancellationToken.None));

        Assert.Equal(RfbProtocolFailureKind.MalformedFramebufferUpdate, exception.Failure?.Kind);
        Assert.Equal(1011, exception.Failure?.EncodingId);
        Assert.Equal(0, decoder.InvocationCount);
    }

    [Fact]
    public async Task Update_rejects_zero_dimension_apple_mvs_with_non_zero_coordinates()
    {
        using var framebuffer = new FramebufferModel(1920, 1080, ProtocolLimits.Default);
        var decoder = new MockAppleMvsDecoder();
        await using var session = FramebufferUpdateReader.CreateSession(
            framebuffer,
            PixelFormat.WinArdBgra32);
        session.RegisterResearchDecoder(decoder);

        var data = CreateFramebufferUpdate(10, 20, 0, 0, 1011);
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            session.ApplyAsync(new MemoryStream(data), CancellationToken.None));

        Assert.Equal(RfbProtocolFailureKind.MalformedFramebufferUpdate, exception.Failure?.Kind);
        Assert.Equal(1011, exception.Failure?.EncodingId);
        Assert.Equal(0, decoder.InvocationCount);
    }

    [Fact]
    public async Task Update_rejects_out_of_bounds_apple_mvs_slice_rectangle()
    {
        using var framebuffer = new FramebufferModel(1920, 1080, ProtocolLimits.Default);
        var decoder = new MockAppleMvsDecoder();
        await using var session = FramebufferUpdateReader.CreateSession(
            framebuffer,
            PixelFormat.WinArdBgra32);
        session.RegisterResearchDecoder(decoder);

        var data = CreateFramebufferUpdate(1900, 1000, 200, 200, 1011); // 1900 + 200 = 2100 > 1920
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            session.ApplyAsync(new MemoryStream(data), CancellationToken.None));

        Assert.Contains("exceeds", exception.Message);
        Assert.Equal(0, decoder.InvocationCount);
    }

    [Fact]
    public async Task Session_supports_registering_research_decoder_without_reflection()
    {
        using var framebuffer = new FramebufferModel(1920, 1080, ProtocolLimits.Default);
        await using var session = FramebufferUpdateReader.CreateSession(framebuffer, PixelFormat.WinArdBgra32);

        var decoder = new MockAppleMvsDecoder();
        session.RegisterResearchDecoder(decoder);

        // Verify registration by passing a message with 1011
        var data = CreateFramebufferUpdate(0, 0, 0, 0, 1011);
        var result = await session.ApplyAsync(new MemoryStream(data), CancellationToken.None);
        Assert.Equal(1, decoder.InvocationCount);
        Assert.Equal(1, result.EncodingCounts[1011]);
    }
}
