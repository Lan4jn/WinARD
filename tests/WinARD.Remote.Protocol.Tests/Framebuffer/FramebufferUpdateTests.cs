using System.Buffers.Binary;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using WinARD.Testing.Streams;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Framebuffer;

public sealed class FramebufferUpdateTests
{
    [Fact]
    public async Task Raw_rectangle_updates_only_declared_region()
    {
        using var framebuffer = new FramebufferModel(4, 4, ProtocolLimits.Default);
        var message = Update(Raw(1, 1, 2, 2,
            [0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]));

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(message), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None);

        Assert.Equal(new FramebufferRect(1, 1, 2, 2), Assert.Single(result.DirtyRects));
        Assert.Equal(0xFFFF0000u, framebuffer.GetBgra32(1, 1));
        Assert.Equal(0xFF00FF00u, framebuffer.GetBgra32(2, 1));
        Assert.Equal(0xFF0000FFu, framebuffer.GetBgra32(1, 2));
        Assert.Equal(0xFFFFFFFFu, framebuffer.GetBgra32(2, 2));
        Assert.Equal(0xFF000000u, framebuffer.GetBgra32(0, 0));
    }

    [Fact]
    public async Task Raw_supports_16_bit_big_endian_rgb565()
    {
        using var framebuffer = new FramebufferModel(4, 1, ProtocolLimits.Default);
        var format = new PixelFormat(16, 16, 1, 1, 31, 63, 31, 11, 5, 0);

        _ = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(Update(Raw(0, 0, 4, 1, [0xF8, 0, 0x07, 0xE0, 0, 0x1F, 0xFF, 0xFF]))),
            framebuffer,
            format,
            CancellationToken.None);

        Assert.Equal(0xFFFF0000u, framebuffer.GetBgra32(0, 0));
        Assert.Equal(0xFF00FF00u, framebuffer.GetBgra32(1, 0));
        Assert.Equal(0xFF0000FFu, framebuffer.GetBgra32(2, 0));
        Assert.Equal(0xFFFFFFFFu, framebuffer.GetBgra32(3, 0));
    }

    [Fact]
    public async Task Raw_supports_8_bit_true_color()
    {
        using var framebuffer = new FramebufferModel(4, 1, ProtocolLimits.Default);
        var format = new PixelFormat(8, 8, 0, 1, 7, 7, 3, 5, 2, 0);

        _ = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(Update(Raw(0, 0, 4, 1, [0xE0, 0x1C, 0x03, 0xFF]))),
            framebuffer,
            format,
            CancellationToken.None);

        Assert.Equal(0xFFFF0000u, framebuffer.GetBgra32(0, 0));
        Assert.Equal(0xFF00FF00u, framebuffer.GetBgra32(1, 0));
        Assert.Equal(0xFF0000FFu, framebuffer.GetBgra32(2, 0));
        Assert.Equal(0xFFFFFFFFu, framebuffer.GetBgra32(3, 0));
    }

    [Fact]
    public async Task Raw_rectangle_failure_does_not_change_destination()
    {
        using var framebuffer = new FramebufferModel(2, 2, ProtocolLimits.Default);
        var before = framebuffer.GetPixelsBgra32();
        var shortMessage = Update(Raw(0, 0, 2, 2, [1, 2, 3, 4]));

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(shortMessage), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task Out_of_bounds_raw_rectangle_is_rejected_before_payload_read()
    {
        using var framebuffer = new FramebufferModel(2, 2, ProtocolLimits.Default);
        var message = Update(Header(1, 1, 2, 2, RfbEncodingType.Raw));
        await using var stream = new MemoryStream(message);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(stream, framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Equal(message.Length, stream.Position);
        Assert.All(framebuffer.GetPixelsBgra32().Where((_, index) => index % 4 != 3), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Raw_length_above_message_limit_is_rejected_without_mutation()
    {
        var limits = new ProtocolLimits(15, 64);
        using var framebuffer = new FramebufferModel(2, 2, limits);
        var before = framebuffer.GetPixelsBgra32();
        var message = Update(Header(0, 0, 2, 2, RfbEncodingType.Raw));

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(message), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task CopyRect_applies_payload_and_returns_destination_dirty()
    {
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        framebuffer.ApplyRaw(new FramebufferRect(0, 0, 2, 1), [7, 0, 0, 255, 9, 0, 0, 255]);
        var message = Update(CopyRect(1, 0, 1, 1, 0, 0));

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(message), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None);

        Assert.Equal(new FramebufferRect(1, 0, 1, 1), Assert.Single(result.DirtyRects));
        Assert.Equal(7u, framebuffer.GetBgra32(1, 0) & 0xFF);
    }

    [Fact]
    public async Task Invalid_CopyRect_does_not_modify_pixels()
    {
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        framebuffer.ApplyRaw(new FramebufferRect(0, 0, 2, 1), [7, 0, 0, 255, 9, 0, 0, 255]);
        var before = framebuffer.GetPixelsBgra32();

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(Update(CopyRect(0, 0, 2, 1, 1, 0))),
                framebuffer,
                PixelFormat.WinArdBgra32,
                CancellationToken.None));

        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task DesktopSize_resizes_atomically_and_marks_full_screen_dirty()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(Update(Header(0, 0, 3, 2, RfbEncodingType.DesktopSize))),
            framebuffer,
            PixelFormat.WinArdBgra32,
            CancellationToken.None);

        Assert.True(result.DesktopResized);
        Assert.Equal(3, framebuffer.Width);
        Assert.Equal(2, framebuffer.Height);
        Assert.Equal(new FramebufferRect(0, 0, 3, 2), Assert.Single(result.DirtyRects));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task DesktopSize_requires_zero_origin(ushort x, ushort y)
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(Update(Header(x, y, 2, 2, RfbEncodingType.DesktopSize))),
                framebuffer,
                PixelFormat.WinArdBgra32,
                CancellationToken.None));

        Assert.Equal(1, framebuffer.Width);
        Assert.Equal(1, framebuffer.Height);
    }

    [Fact]
    public async Task DesktopSize_above_limit_preserves_old_framebuffer()
    {
        using var framebuffer = new FramebufferModel(1, 1, new ProtocolLimits(1024, 4));

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(Update(Header(0, 0, 2, 2, RfbEncodingType.DesktopSize))),
                framebuffer,
                PixelFormat.WinArdBgra32,
                CancellationToken.None));

        Assert.Equal(1, framebuffer.Width);
        Assert.Equal(1, framebuffer.Height);
    }

    [Fact]
    public async Task Cursor_applies_msb_first_mask_with_row_padding_without_touching_desktop()
    {
        using var framebuffer = new FramebufferModel(2, 2, ProtocolLimits.Default);
        var before = framebuffer.GetPixelsBgra32();
        var pixels = Enumerable.Range(0, 10)
            .SelectMany(index => new byte[] { (byte)(index + 1), 0, 0, 0 })
            .ToArray();
        var message = Update(Cursor(2, 0, 10, 1, pixels, [0b1010_0000, 0b0100_0000]));

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(message), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None);

        var cursor = Assert.IsType<RemoteCursor>(result.Cursor);
        Assert.Equal(2, cursor.HotspotX);
        Assert.Equal(0, cursor.HotspotY);
        Assert.Equal(10, cursor.Width);
        Assert.Equal(1, cursor.Height);
        Assert.Equal(255, cursor.GetPixelsBgra32()[3]);
        Assert.Equal(0, cursor.GetPixelsBgra32()[7]);
        Assert.Equal(255, cursor.GetPixelsBgra32()[11]);
        Assert.Equal(255, cursor.GetPixelsBgra32()[(9 * 4) + 3]);
        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task Cursor_pixels_are_defensively_copied()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(Update(Cursor(0, 0, 1, 1, [1, 2, 3, 0], [0x80]))),
            framebuffer,
            PixelFormat.WinArdBgra32,
            CancellationToken.None);
        var cursor = Assert.IsType<RemoteCursor>(result.Cursor);

        var pixels = cursor.GetPixelsBgra32();
        pixels[0] = 99;

        Assert.Equal(1, cursor.GetPixelsBgra32()[0]);
    }

    [Fact]
    public async Task Empty_cursor_has_no_payload()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(Update(Header(0, 0, 0, 0, RfbEncodingType.Cursor))),
            framebuffer,
            PixelFormat.WinArdBgra32,
            CancellationToken.None);

        var cursor = Assert.IsType<RemoteCursor>(result.Cursor);
        Assert.Equal(0, cursor.Width);
        Assert.Empty(cursor.GetPixelsBgra32());
    }

    [Fact]
    public async Task Multiple_rectangles_preserve_order_in_dirty_list()
    {
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        var message = Update(
            Raw(0, 0, 1, 1, [1, 0, 0, 0]),
            Raw(1, 0, 1, 1, [2, 0, 0, 0]));

        var result = await FramebufferUpdateReader.ApplyAsync(
            new ChunkedReadStream(message, 1), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None);

        Assert.Equal(
            [new FramebufferRect(0, 0, 1, 1), new FramebufferRect(1, 0, 1, 1)],
            result.DirtyRects);
        Assert.Equal(1u, framebuffer.GetBgra32(0, 0) & 0xFF);
        Assert.Equal(2u, framebuffer.GetBgra32(1, 0) & 0xFF);
    }

    [Fact]
    public async Task Unknown_encoding_reports_signed_id()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        const int encoding = -777;

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(Update(Header(0, 0, 1, 1, (RfbEncodingType)encoding))),
                framebuffer,
                PixelFormat.WinArdBgra32,
                CancellationToken.None));

        Assert.Contains("-777", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rectangle_count_above_limit_is_rejected_before_loop()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream([0, 0, 0x10, 0x01]),
                framebuffer,
                PixelFormat.WinArdBgra32,
                CancellationToken.None));

        Assert.Contains("4097", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cursor_payload_length_overflow_is_reported_as_protocol_failure()
    {
        var limits = new ProtocolLimits(int.MaxValue, int.MaxValue);
        using var framebuffer = new FramebufferModel(1, 1, limits);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(Update(Header(0, 0, ushort.MaxValue, 8193, RfbEncodingType.Cursor))),
                framebuffer,
                PixelFormat.WinArdBgra32,
                CancellationToken.None));
    }

    [Fact]
    public async Task Non_update_server_message_is_rejected()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream([2]), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));
    }

    [Fact]
    public async Task Pending_update_read_propagates_cancellation()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        await using var stream = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource();

        var task = FramebufferUpdateReader.ApplyAsync(stream, framebuffer, PixelFormat.WinArdBgra32, cancellation.Token);
        await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, stream.ReceivedCancellationToken);
    }

    private static byte[] Update(params byte[][] rectangles)
    {
        var bytes = new List<byte> { 0, 0 };
        AddUInt16(bytes, checked((ushort)rectangles.Length));
        foreach (var rectangle in rectangles)
        {
            bytes.AddRange(rectangle);
        }

        return bytes.ToArray();
    }

    private static byte[] Raw(ushort x, ushort y, ushort width, ushort height, byte[] pixels) =>
        [.. Header(x, y, width, height, RfbEncodingType.Raw), .. pixels];

    private static byte[] CopyRect(ushort x, ushort y, ushort width, ushort height, ushort sourceX, ushort sourceY)
    {
        var bytes = new List<byte>(Header(x, y, width, height, RfbEncodingType.CopyRect));
        AddUInt16(bytes, sourceX);
        AddUInt16(bytes, sourceY);
        return bytes.ToArray();
    }

    private static byte[] Cursor(
        ushort hotspotX,
        ushort hotspotY,
        ushort width,
        ushort height,
        byte[] pixels,
        byte[] mask) =>
        [.. Header(hotspotX, hotspotY, width, height, RfbEncodingType.Cursor), .. pixels, .. mask];

    private static byte[] Header(ushort x, ushort y, ushort width, ushort height, RfbEncodingType encoding)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, x);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), y);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), height);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), (int)encoding);
        return bytes;
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        Span<byte> encoded = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(encoded, value);
        bytes.AddRange(encoded.ToArray());
    }
}
