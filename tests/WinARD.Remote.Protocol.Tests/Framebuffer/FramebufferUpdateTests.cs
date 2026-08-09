using System.Buffers.Binary;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using WinARD.Testing.Streams;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707, CA1859

namespace WinARD.Remote.Protocol.Tests.Framebuffer;

public sealed class FramebufferUpdateTests
{
    [Fact]
    public async Task Update_reports_each_rectangle_encoding_count()
    {
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        var message = Update(
            Raw(0, 0, 1, 1, [1, 2, 3, 0]),
            CopyRect(1, 0, 1, 1, 0, 0),
            Cursor(0, 0, 1, 1, [4, 5, 6, 0], [0x80]));

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(message),
            framebuffer,
            PixelFormat.WinArdBgra32,
            CancellationToken.None);

        Assert.Equal(1, result.EncodingCounts[(int)RfbEncodingType.Raw]);
        Assert.Equal(1, result.EncodingCounts[(int)RfbEncodingType.CopyRect]);
        Assert.Equal(1, result.EncodingCounts[(int)RfbEncodingType.Cursor]);
    }

    [Fact]
    public async Task Update_aggregates_repeated_rectangle_encodings()
    {
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(Update(
                Raw(0, 0, 1, 1, [1, 2, 3, 0]),
                Raw(1, 0, 1, 1, [4, 5, 6, 0]))),
            framebuffer,
            PixelFormat.WinArdBgra32,
            CancellationToken.None);

        Assert.Equal(2, result.EncodingCounts[(int)RfbEncodingType.Raw]);
        Assert.Single(result.EncodingCounts);
    }

    [Fact]
    public void Update_result_defensively_copies_encoding_counts()
    {
        var counts = new Dictionary<int, int> { [(int)RfbEncodingType.Raw] = 1 };
        var result = new FramebufferUpdateResult([], [], null, false, counts);

        counts[(int)RfbEncodingType.Raw] = 99;

        Assert.Equal(1, result.EncodingCounts[(int)RfbEncodingType.Raw]);
        var mutableView = Assert.IsAssignableFrom<IDictionary<int, int>>(result.EncodingCounts);
        Assert.True(mutableView.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableView.Add((int)RfbEncodingType.CopyRect, 1));
    }

    [Fact]
    public void Legacy_update_result_constructor_has_immutable_empty_encoding_counts()
    {
        var result = new FramebufferUpdateResult([], [], null, false);

        Assert.Empty(result.EncodingCounts);
        var mutableView = Assert.IsAssignableFrom<IDictionary<int, int>>(result.EncodingCounts);
        Assert.True(mutableView.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableView.Add((int)RfbEncodingType.Raw, 1));
    }

    [Fact]
    public async Task Public_encoding_decoder_contract_decodes_canonical_raw_rectangle()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var reader = new RfbReader(
            new MemoryStream([0, 0, 255, 0]),
            ProtocolLimits.Default);
        var decoder = CreateRawDecoder();
        var rectangle = new FramebufferRect(0, 0, 1, 1);

        var decodeResult = await decoder.DecodeAsync(
            reader,
            framebuffer,
            rectangle,
            CancellationToken.None);

        Assert.Equal(0, decoder.EncodingId);
        Assert.Equal(rectangle, Assert.Single(decodeResult.DirtyRects));
        Assert.Equal(rectangle, Assert.Single(decodeResult.PixelContentRects));
        Assert.Equal(0xFFFF0000u, framebuffer.GetBgra32(0, 0));
    }

    [Fact]
    public async Task Public_cursor_decoder_updates_independent_cursor_without_touching_desktop()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var before = framebuffer.GetPixelsBgra32();
        var reader = new RfbReader(
            new MemoryStream([1, 2, 3, 0, 0x80]),
            ProtocolLimits.Default);
        var decoder = CreateCursorDecoder(PixelFormat.WinArdBgra32);

        var decodeResult = await decoder.DecodeAsync(
            reader,
            framebuffer,
            new FramebufferRect(0, 0, 1, 1),
            CancellationToken.None);

        Assert.Empty(decodeResult.DirtyRects);
        Assert.Empty(decodeResult.PixelContentRects);
        var cursor = Assert.IsType<RemoteCursor>(framebuffer.Cursor);
        Assert.Equal([1, 2, 3, 255], cursor.GetPixelsBgra32());
        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task Public_raw_decoder_uses_configured_noncanonical_pixel_format()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var format = new PixelFormat(16, 16, 1, 1, 31, 63, 31, 11, 5, 0);
        var reader = new RfbReader(new MemoryStream([0xF8, 0]), ProtocolLimits.Default);
        var decoder = CreateRawDecoder(format);
        var rectangle = new FramebufferRect(0, 0, 1, 1);

        var decodeResult = await decoder.DecodeAsync(
            reader,
            framebuffer,
            rectangle,
            CancellationToken.None);

        Assert.Equal(rectangle, Assert.Single(decodeResult.DirtyRects));
        Assert.Equal(rectangle, Assert.Single(decodeResult.PixelContentRects));
        Assert.Equal(0xFFFF0000u, framebuffer.GetBgra32(0, 0));
    }

    [Fact]
    public async Task Reader_dispatches_registered_decoder_through_public_contract()
    {
        var dirtyRect = new FramebufferRect(0, 0, 1, 1);
        var contentRect = new FramebufferRect(1, 0, 1, 1);
        var decoder = new RecordingDecoder(
            (int)RfbEncodingType.Raw,
            new EncodingDecodeResult([dirtyRect], [contentRect]));
        IReadOnlyDictionary<int, IRfbEncodingDecoder> decoders =
            new Dictionary<int, IRfbEncodingDecoder> { [decoder.EncodingId] = decoder };
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(Update(Header(0, 0, 2, 1, RfbEncodingType.Raw))),
            framebuffer,
            decoders,
            CancellationToken.None);

        Assert.True(decoder.WasCalled);
        Assert.Equal(dirtyRect, Assert.Single(result.DirtyRects));
        Assert.Equal(contentRect, Assert.Single(result.PixelContentRects));
    }

    [Fact]
    public async Task Raw_rectangle_updates_only_declared_region()
    {
        using var framebuffer = new FramebufferModel(4, 4, ProtocolLimits.Default);
        var message = Update(Raw(1, 1, 2, 2,
            [0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]));

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(message), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None);

        Assert.Equal(new FramebufferRect(1, 1, 2, 2), Assert.Single(result.DirtyRects));
        Assert.Equal(new FramebufferRect(1, 1, 2, 2), Assert.Single(result.PixelContentRects));
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
        var before = framebuffer.GetPixelsBgra32();
        var message = Update([.. Header(1, 1, 2, 2, RfbEncodingType.Raw), 0xDE, 0xAD]);
        await using var stream = new MemoryStream(message);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(stream, framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Contains("exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, stream.Position);
        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task Raw_length_above_update_budget_is_rejected_without_consuming_payload()
    {
        var limits = new ProtocolLimits(15, 64, 15);
        using var framebuffer = new FramebufferModel(2, 2, limits);
        var before = framebuffer.GetPixelsBgra32();
        var message = Update([.. Header(0, 0, 2, 2, RfbEncodingType.Raw), 0xDE, 0xAD]);
        await using var stream = new MemoryStream(message);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                stream, framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Contains("16", exception.Message, StringComparison.Ordinal);
        Assert.Equal(16, stream.Position);
        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task Raw_payload_budget_is_cumulative_and_rejects_before_second_payload()
    {
        var limits = new ProtocolLimits(16, 8, 7);
        using var framebuffer = new FramebufferModel(2, 1, limits);
        var message = Update(
            Raw(0, 0, 1, 1, [1, 0, 0, 0]),
            Raw(1, 0, 1, 1, [0xDE, 0xAD, 0xBE, 0xEF]));
        await using var stream = new MemoryStream(message);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(stream, framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Contains("8", exception.Message, StringComparison.Ordinal);
        Assert.Equal(32, stream.Position);
        Assert.Equal(1u, framebuffer.GetBgra32(0, 0) & 0xFF);
        Assert.Equal(0u, framebuffer.GetBgra32(1, 0) & 0xFF);
    }

    [Fact]
    public async Task Raw_payload_can_exceed_message_limit_when_within_update_budget()
    {
        const int width = 3840;
        const int height = 2160;
        const int payloadLength = width * height * 4;
        const int workLength = payloadLength * 3;
        var limits = new ProtocolLimits(
            16 * 1024 * 1024,
            payloadLength,
            payloadLength,
            workLength,
            16 * 1024 * 1024);
        using var framebuffer = new FramebufferModel(width, height, limits);
        var payload = new byte[payloadLength];

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(Update(Raw(0, 0, width, height, payload))),
            framebuffer,
            PixelFormat.WinArdBgra32,
            CancellationToken.None);

        Assert.Equal(new FramebufferRect(0, 0, width, height), Assert.Single(result.PixelContentRects));
    }

    [Fact]
    public async Task Raw_work_budget_counts_wire_conversion_and_framebuffer_write()
    {
        var limits = new ProtocolLimits(16, 4, 4, 11, 4);
        using var framebuffer = new FramebufferModel(1, 1, limits);
        var message = Update(Raw(0, 0, 1, 1, [0xDE, 0xAD, 0xBE, 0xEF]));
        await using var stream = new MemoryStream(message);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(stream, framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Contains("work", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12", exception.Message, StringComparison.Ordinal);
        Assert.Equal(16, stream.Position);
        Assert.Equal(0u, framebuffer.GetBgra32(0, 0) & 0x00FFFFFF);
    }

    [Fact]
    public async Task Framebuffer_update_payload_budget_resets_for_each_update()
    {
        var limits = new ProtocolLimits(4, 4, 4);
        using var framebuffer = new FramebufferModel(1, 1, limits);
        var update = Update(Raw(0, 0, 1, 1, [1, 0, 0, 0]));

        _ = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(update), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None);
        _ = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(update), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None);

        Assert.Equal(1u, framebuffer.GetBgra32(0, 0) & 0xFF);
    }

    [Fact]
    public async Task Framebuffer_update_work_budget_resets_for_each_update()
    {
        var limits = new ProtocolLimits(4, 4, 4, 12, 4);
        using var framebuffer = new FramebufferModel(1, 1, limits);
        var update = Update(Raw(0, 0, 1, 1, [1, 0, 0, 0]));

        _ = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(update), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None);
        _ = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(update), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None);

        Assert.Equal(1u, framebuffer.GetBgra32(0, 0) & 0xFF);
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
        Assert.Empty(result.PixelContentRects);
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
    public async Task CopyRect_payload_budget_is_cumulative_and_rejects_before_second_payload()
    {
        var limits = new ProtocolLimits(16, 8, 7);
        using var framebuffer = new FramebufferModel(2, 1, limits);
        framebuffer.ApplyRaw(new FramebufferRect(0, 0, 2, 1), [1, 0, 0, 255, 2, 0, 0, 255]);
        var message = Update(
            CopyRect(0, 0, 1, 1, 1, 0),
            [.. Header(1, 0, 1, 1, RfbEncodingType.CopyRect), 0xDE, 0xAD, 0xBE, 0xEF]);
        await using var stream = new MemoryStream(message);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(stream, framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Contains("8", exception.Message, StringComparison.Ordinal);
        Assert.Equal(32, stream.Position);
        Assert.Equal(2u, framebuffer.GetBgra32(0, 0) & 0xFF);
        Assert.Equal(2u, framebuffer.GetBgra32(1, 0) & 0xFF);
    }

    [Fact]
    public async Task CopyRect_work_budget_counts_copied_pixels_before_coordinate_payload()
    {
        const int width = 1024;
        const int height = 1024;
        const int framebufferBytes = width * height * 4;
        var limits = new ProtocolLimits(16, framebufferBytes, 8, framebufferBytes + 1, 4);
        using var framebuffer = new FramebufferModel(width, height, limits);
        var message = Update(
            CopyRect(0, 0, width, height, 0, 0),
            [.. Header(0, 0, width, height, RfbEncodingType.CopyRect), 0xDE, 0xAD, 0xBE, 0xEF]);
        await using var stream = new MemoryStream(message);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(stream, framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Contains("work", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(32, stream.Position);
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
        Assert.Empty(result.PixelContentRects);
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
    public async Task DesktopSize_rejects_excessive_resizes_before_next_allocation()
    {
        var limits = new ProtocolLimits(16, 64, 64, 256, 4);
        using var framebuffer = new FramebufferModel(1, 1, limits);
        var message = Update(
            Header(0, 0, 1, 1, RfbEncodingType.DesktopSize),
            Header(0, 0, 2, 1, RfbEncodingType.DesktopSize),
            Header(0, 0, 3, 1, RfbEncodingType.DesktopSize),
            Header(0, 0, 4, 1, RfbEncodingType.DesktopSize),
            Header(0, 0, 5, 1, RfbEncodingType.DesktopSize));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(message), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Contains("DesktopSize", exception.Message, StringComparison.Ordinal);
        Assert.Equal(4, framebuffer.Width);
        Assert.Equal(1, framebuffer.Height);
    }

    [Fact]
    public async Task DesktopSize_work_budget_is_reserved_before_resize_allocation()
    {
        var limits = new ProtocolLimits(16, 8, 8, 7, 4);
        using var framebuffer = new FramebufferModel(1, 1, limits);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(Update(Header(0, 0, 2, 1, RfbEncodingType.DesktopSize))),
                framebuffer,
                PixelFormat.WinArdBgra32,
                CancellationToken.None));

        Assert.Contains("work", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task Cursor_payload_budget_is_cumulative_and_rejects_before_second_payload()
    {
        var limits = new ProtocolLimits(16, 16, 9);
        using var framebuffer = new FramebufferModel(1, 1, limits);
        var message = Update(
            Cursor(0, 0, 1, 1, [1, 2, 3, 0], [0x80]),
            Cursor(0, 0, 1, 1, [0xDE, 0xAD, 0xBE, 0xEF], [0x80]));
        await using var stream = new MemoryStream(message);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(stream, framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Contains("10", exception.Message, StringComparison.Ordinal);
        Assert.Equal(33, stream.Position);
        Assert.Equal([1, 2, 3, 255], Assert.IsType<RemoteCursor>(framebuffer.Cursor).GetPixelsBgra32());
    }

    [Fact]
    public async Task Cursor_rejects_peak_memory_above_cursor_limit_before_payload_read()
    {
        const int framebufferLimit = 64 * 1024 * 1024;
        var limits = new ProtocolLimits(16, framebufferLimit, framebufferLimit, 256 * 1024 * 1024, 1024 * 1024);
        using var framebuffer = new FramebufferModel(1, 1, limits);
        var message = Update([.. Header(0, 0, 2048, 2048, RfbEncodingType.Cursor), 0xDE, 0xAD]);
        await using var stream = new MemoryStream(message);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(stream, framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Contains("cursor", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, stream.Position);
        Assert.Null(framebuffer.Cursor);
    }

    [Fact]
    public async Task Cursor_work_budget_is_reserved_before_payload_read()
    {
        var limits = new ProtocolLimits(16, 5, 5, 12, 4);
        using var framebuffer = new FramebufferModel(1, 1, limits);
        var message = Update([.. Header(0, 0, 1, 1, RfbEncodingType.Cursor), 0xDE, 0xAD]);
        await using var stream = new MemoryStream(message);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(stream, framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Contains("work", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("13", exception.Message, StringComparison.Ordinal);
        Assert.Equal(16, stream.Position);
        Assert.Null(framebuffer.Cursor);
    }

    [Fact]
    public async Task Cursor_with_reasonable_dimensions_fits_payload_and_work_budgets()
    {
        const int width = 64;
        const int height = 64;
        const int pixelBytes = width * height * 4;
        const int maskBytes = width / 8 * height;
        const int workBytes = pixelBytes + maskBytes + (pixelBytes * 2);
        var limits = new ProtocolLimits(
            16,
            pixelBytes + maskBytes,
            pixelBytes + maskBytes,
            workBytes,
            pixelBytes);
        using var framebuffer = new FramebufferModel(1, 1, limits);

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(Update(Cursor(
                0,
                0,
                width,
                height,
                new byte[pixelBytes],
                Enumerable.Repeat((byte)0xFF, maskBytes).ToArray()))),
            framebuffer,
            PixelFormat.WinArdBgra32,
            CancellationToken.None);

        var cursor = Assert.IsType<RemoteCursor>(result.Cursor);
        Assert.Equal(width, cursor.Width);
        Assert.Equal(height, cursor.Height);
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
    public async Task Session_consumes_late_ARD_display_metadata_updates_then_continues_with_raw()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        await using var session = FramebufferUpdateReader.CreateSession(framebuffer, PixelFormat.WinArdBgra32);
        await using var stream = new MemoryStream(
            [
                .. Update(ArdDisplayInfo(0, 0, 2, 1, displayCount: 0)),
                .. Update(ArdDisplayInfo2(3, 1, [0xAA, 0xBB])),
                .. Update(Raw(0, 0, 3, 1, [1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0])),
            ]);

        var displayInfo = await session.ApplyAsync(stream, CancellationToken.None);
        var displayInfo2 = await session.ApplyAsync(stream, CancellationToken.None);
        var raw = await session.ApplyAsync(stream, CancellationToken.None);

        Assert.True(displayInfo.DesktopResized);
        Assert.True(displayInfo2.DesktopResized);
        Assert.Empty(displayInfo.DirtyRects);
        Assert.Empty(displayInfo2.DirtyRects);
        Assert.False(raw.DesktopResized);
        Assert.Equal(new FramebufferRect(0, 0, 3, 1), Assert.Single(raw.DirtyRects));
        Assert.Equal(3u, framebuffer.GetBgra32(2, 0) & 0xFF);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Same_update_applies_ARD_metadata_resize_before_following_raw_rectangle()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var message = Update(
            ArdDisplayInfo(0, 0, 2, 1, displayCount: 1, recordFill: 0xA5),
            Raw(0, 0, 2, 1, [4, 0, 0, 0, 5, 0, 0, 0]));

        var result = await FramebufferUpdateReader.ApplyAsync(
            new MemoryStream(message),
            framebuffer,
            PixelFormat.WinArdBgra32,
            CancellationToken.None);

        Assert.True(result.DesktopResized);
        Assert.Equal(new FramebufferRect(0, 0, 2, 1), Assert.Single(result.DirtyRects));
        Assert.Equal(new FramebufferRect(0, 0, 2, 1), Assert.Single(result.PixelContentRects));
        Assert.Equal(5u, framebuffer.GetBgra32(1, 0) & 0xFF);
    }

    [Theory]
    [InlineData(RfbEncodingType.ArdDisplayInfo)]
    [InlineData(RfbEncodingType.ArdDisplayInfo2)]
    public async Task Same_size_ARD_metadata_preserves_pixels_before_following_partial_raw(
        RfbEncodingType encoding)
    {
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        await using var session = FramebufferUpdateReader.CreateSession(framebuffer, PixelFormat.WinArdBgra32);
        var metadata = encoding == RfbEncodingType.ArdDisplayInfo
            ? ArdDisplayInfo(0, 0, 2, 1, displayCount: 0)
            : ArdDisplayInfo2(2, 1, [0xAA]);
        await using var stream = new MemoryStream(
            [
                .. Update(Raw(0, 0, 2, 1, [1, 0, 0, 0, 2, 0, 0, 0])),
                .. Update(metadata),
                .. Update(Raw(0, 0, 1, 1, [3, 0, 0, 0])),
            ]);

        _ = await session.ApplyAsync(stream, CancellationToken.None);
        var metadataResult = await session.ApplyAsync(stream, CancellationToken.None);

        Assert.False(metadataResult.DesktopResized);
        Assert.Empty(metadataResult.DirtyRects);
        Assert.Equal(1u, framebuffer.GetBgra32(0, 0) & 0xFF);
        Assert.Equal(2u, framebuffer.GetBgra32(1, 0) & 0xFF);

        _ = await session.ApplyAsync(stream, CancellationToken.None);

        Assert.Equal(3u, framebuffer.GetBgra32(0, 0) & 0xFF);
        Assert.Equal(2u, framebuffer.GetBgra32(1, 0) & 0xFF);
    }

    [Fact]
    public async Task ARD_display_info_payload_over_update_budget_is_rejected_before_records()
    {
        var limits = new ProtocolLimits(64, 64, 16);
        using var framebuffer = new FramebufferModel(1, 1, limits);
        var message = Update(ArdDisplayInfo(0, 0, 2, 1, displayCount: 1, recordFill: 0x5A));
        await using var stream = new MemoryStream(message);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                stream,
                framebuffer,
                PixelFormat.WinArdBgra32,
                CancellationToken.None));

        Assert.Contains("36", exception.Message, StringComparison.Ordinal);
        Assert.Equal(24, stream.Position);
        Assert.Equal((1, 1), (framebuffer.Width, framebuffer.Height));
    }

    [Fact]
    public async Task Truncated_ARD_display_info_two_payload_is_protocol_failure()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var rectangle = new List<byte>(Header(0, 0, 2, 1, RfbEncodingType.ArdDisplayInfo2));
        AddUInt16(rectangle, 4);
        rectangle.AddRange([0xDE, 0xAD]);
        await using var stream = new MemoryStream(Update(rectangle.ToArray()));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                stream,
                framebuffer,
                PixelFormat.WinArdBgra32,
                CancellationToken.None));

        Assert.Contains("Unexpected end", exception.Message, StringComparison.Ordinal);
        Assert.Equal(stream.Length, stream.Position);
        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.FramebufferRectanglePayload, exception.Failure?.ReadStage);
        Assert.Equal((byte)0, exception.Failure?.ServerMessageType);
        Assert.Equal((int)RfbEncodingType.ArdDisplayInfo2, exception.Failure?.EncodingId);
        Assert.Equal(0, exception.Failure?.RectangleIndex);
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
        Assert.Equal(RfbProtocolFailureKind.UnsupportedEncoding, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.FramebufferRectangleHeader, exception.Failure?.ReadStage);
        Assert.Equal((byte)0, exception.Failure?.ServerMessageType);
        Assert.Equal(encoding, exception.Failure?.EncodingId);
        Assert.Equal(0, exception.Failure?.RectangleIndex);
    }

    [Fact]
    public async Task Truncated_second_rectangle_header_reports_its_index()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var message = Update(Raw(0, 0, 1, 1, [1, 2, 3, 4]), [0, 0]);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(message),
                framebuffer,
                PixelFormat.WinArdBgra32,
                CancellationToken.None));

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.FramebufferRectangleHeader, exception.Failure?.ReadStage);
        Assert.Equal((byte)0, exception.Failure?.ServerMessageType);
        Assert.Null(exception.Failure?.EncodingId);
        Assert.Equal(1, exception.Failure?.RectangleIndex);
    }

    [Fact]
    public async Task Decoder_protocol_failure_reports_second_rectangle_context()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        const int encoding = 777;
        var decoder = new ProtocolFailureDecoder(encoding);
        var decoders = new Dictionary<int, IRfbEncodingDecoder>
        {
            [(int)RfbEncodingType.Raw] = CreateRawDecoder(),
            [encoding] = decoder,
        };

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(Update(
                    Raw(0, 0, 1, 1, [1, 2, 3, 4]),
                    Header(0, 0, 1, 1, (RfbEncodingType)encoding))),
                framebuffer,
                decoders,
                CancellationToken.None));

        Assert.Equal(RfbProtocolFailureKind.DecoderFailure, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.FramebufferRectanglePayload, exception.Failure?.ReadStage);
        Assert.Equal((byte)0, exception.Failure?.ServerMessageType);
        Assert.Equal(encoding, exception.Failure?.EncodingId);
        Assert.Equal(1, exception.Failure?.RectangleIndex);
    }

    [Theory]
    [InlineData(new byte[] { 0 }, RfbProtocolReadStage.FramebufferHeader)]
    [InlineData(new byte[] { 0, 0, 0, 1, 0 }, RfbProtocolReadStage.FramebufferRectangleHeader)]
    public async Task Truncated_update_headers_report_stage(byte[] message, RfbProtocolReadStage expectedStage)
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(message), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(expectedStage, exception.Failure?.ReadStage);
        Assert.Equal((byte)0, exception.Failure?.ServerMessageType);
        Assert.Equal(expectedStage == RfbProtocolReadStage.FramebufferRectangleHeader ? 0 : null,
            exception.Failure?.RectangleIndex);
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
    public async Task Update_message_type_eof_reports_server_message_stage_without_type()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                new MemoryStream(), framebuffer, PixelFormat.WinArdBgra32, CancellationToken.None));

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ServerMessageType, exception.Failure?.ReadStage);
        Assert.Null(exception.Failure?.ServerMessageType);
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

    [Fact]
    public async Task One_shot_reader_rejects_zrle_before_consuming_compressed_length()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        await using var stream = new MemoryStream(
            Update([.. Header(0, 0, 1, 1, RfbEncodingType.Zrle), 0, 0, 0, 4, 0, 0, 0xFF, 0xFF]));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            FramebufferUpdateReader.ApplyAsync(
                stream,
                framebuffer,
                PixelFormat.WinArdBgra32,
                CancellationToken.None));

        Assert.Contains("CreateSession", exception.Message, StringComparison.Ordinal);
        Assert.Equal(16, stream.Position);
    }

    [Fact]
    public async Task Session_faults_permanently_after_message_consumption_failure()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        await using var session = FramebufferUpdateReader.CreateSession(framebuffer, PixelFormat.WinArdBgra32);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            session.ApplyAsync(new MemoryStream([0, 0, 0, 1]), CancellationToken.None));

        await using var retry = new MemoryStream(Update(Raw(0, 0, 1, 1, [1, 2, 3, 4])));
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            session.ApplyAsync(retry, CancellationToken.None));

        Assert.Contains("faulted", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, retry.Position);
    }

    [Fact]
    public async Task Concurrent_session_disposals_share_completion_while_apply_is_in_flight()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var session = FramebufferUpdateReader.CreateSession(framebuffer, PixelFormat.WinArdBgra32);
        await using var stream = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource();
        var apply = session.ApplyAsync(stream, cancellation.Token);
        await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var firstDispose = session.DisposeAsync().AsTask();
        var secondDispose = session.DisposeAsync().AsTask();

        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Concurrent_session_disposals_observe_the_same_cleanup_failure()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var decoder = new ThrowingDisposableDecoder();
        var session = new FramebufferUpdateSession(
            framebuffer,
            new Dictionary<int, IRfbEncodingDecoder> { [decoder.EncodingId] = decoder });

        var firstDispose = session.DisposeAsync().AsTask();
        await decoder.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var secondDispose = session.DisposeAsync().AsTask();
        Assert.False(secondDispose.IsCompleted);
        decoder.ReleaseDispose();

        var firstException = await Assert.ThrowsAsync<InvalidOperationException>(() => firstDispose);
        var secondException = await Assert.ThrowsAsync<InvalidOperationException>(() => secondDispose);
        Assert.Same(firstException, secondException);
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

    private static IRfbEncodingDecoder CreateRawDecoder() => new RawEncoding();

    private static IRfbEncodingDecoder CreateRawDecoder(PixelFormat pixelFormat) => new RawEncoding(pixelFormat);

    private static IRfbEncodingDecoder CreateCursorDecoder(PixelFormat pixelFormat) => new CursorEncoding(pixelFormat);

    private sealed class RecordingDecoder(int encodingId, EncodingDecodeResult result) : IRfbEncodingDecoder
    {
        public int EncodingId { get; } = encodingId;

        public bool WasCalled { get; private set; }

        public ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            FramebufferModel framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingDisposableDecoder : IRfbEncodingDecoder, IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDispose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int EncodingId => 777;
        public Task DisposeStarted => _disposeStarted.Task;

        public ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            FramebufferModel framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(EncodingDecodeResult.Empty);

        public async ValueTask DisposeAsync()
        {
            _disposeStarted.TrySetResult();
            await _releaseDispose.Task;
            throw new InvalidOperationException("Injected decoder disposal failure.");
        }

        public void ReleaseDispose() => _releaseDispose.TrySetResult();
    }

    private static byte[] Raw(ushort x, ushort y, ushort width, ushort height, byte[] pixels) =>
        [.. Header(x, y, width, height, RfbEncodingType.Raw), .. pixels];

    private static byte[] ArdDisplayInfo(
        ushort rectangleWidth,
        ushort rectangleHeight,
        ushort width,
        ushort height,
        ushort displayCount,
        byte recordFill = 0)
    {
        var bytes = new List<byte>(Header(
            0,
            0,
            rectangleWidth,
            rectangleHeight,
            RfbEncodingType.ArdDisplayInfo));
        AddUInt16(bytes, width);
        AddUInt16(bytes, height);
        AddUInt16(bytes, displayCount);
        AddUInt16(bytes, 0);
        bytes.AddRange(Enumerable.Repeat(recordFill, checked(displayCount * 28)));
        return bytes.ToArray();
    }

    private sealed class ProtocolFailureDecoder(int encodingId) : IRfbEncodingDecoder
    {
        public int EncodingId { get; } = encodingId;

        public ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            FramebufferModel framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<EncodingDecodeResult>(new RfbProtocolException("Injected decoder failure."));
    }

    private static byte[] ArdDisplayInfo2(ushort width, ushort height, byte[] payload)
    {
        var bytes = new List<byte>(Header(0, 0, width, height, RfbEncodingType.ArdDisplayInfo2));
        AddUInt16(bytes, checked((ushort)payload.Length));
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

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
