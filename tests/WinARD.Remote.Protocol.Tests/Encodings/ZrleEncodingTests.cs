using System.Buffers.Binary;
using System.IO.Compression;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using WinARD.Testing.Streams;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Encodings;

public sealed class ZrleEncodingTests
{
    [Fact]
    public async Task Pending_pixel_layout_is_bound_to_the_validated_format_and_cleared_after_commit()
    {
        await using var decoder = new ZrleEncoding(PixelFormat.WinArdBgra32);
        var reconfigurable = (IReconfigurablePixelFormatDecoder)decoder;

        reconfigurable.ValidatePixelFormat(PixelFormat.WinArdRgb565);
        Assert.Throws<InvalidOperationException>(() =>
            reconfigurable.CommitPixelFormat(PixelFormat.WinArdBgra32));
        reconfigurable.CommitPixelFormat(PixelFormat.WinArdRgb565);
        Assert.Throws<InvalidOperationException>(() =>
            reconfigurable.CommitPixelFormat(PixelFormat.WinArdRgb565));
    }

    [Fact]
    public async Task Raw_tile_decodes_cpixel_and_reports_pixel_content()
    {
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        var rectangle = new FramebufferRect(0, 0, 2, 1);

        var result = await DecodeAsync(
            framebuffer,
            PixelFormat.WinArdBgra32,
            rectangle,
            [0, 1, 2, 3, 4, 5, 6]);

        Assert.Equal(16, new ZrleEncoding().EncodingId);
        Assert.Equal(rectangle, Assert.Single(result.DirtyRects));
        Assert.Equal(rectangle, Assert.Single(result.PixelContentRects));
        Assert.Equal(0xFF030201u, framebuffer.GetBgra32(0, 0));
        Assert.Equal(0xFF060504u, framebuffer.GetBgra32(1, 0));
    }

    [Fact]
    public async Task Solid_tile_fills_all_pixels()
    {
        using var framebuffer = new FramebufferModel(3, 2, ProtocolLimits.Default);

        _ = await DecodeAsync(
            framebuffer,
            PixelFormat.WinArdBgra32,
            new FramebufferRect(0, 0, 3, 2),
            [1, 9, 8, 7]);

        Assert.All(
            framebuffer.GetPixelsBgra32().Chunk(4),
            pixel => Assert.Equal([9, 8, 7, 255], pixel));
    }

    [Fact]
    public async Task Packed_palette_uses_row_aligned_msb_first_indices()
    {
        using var framebuffer = new FramebufferModel(2, 2, ProtocolLimits.Default);

        _ = await DecodeAsync(
            framebuffer,
            PixelFormat.WinArdBgra32,
            new FramebufferRect(0, 0, 2, 2),
            [2, 1, 0, 0, 2, 0, 0, 0b0100_0000, 0b1000_0000]);

        Assert.Equal(1u, framebuffer.GetBgra32(0, 0) & 0xFF);
        Assert.Equal(2u, framebuffer.GetBgra32(1, 0) & 0xFF);
        Assert.Equal(2u, framebuffer.GetBgra32(0, 1) & 0xFF);
        Assert.Equal(1u, framebuffer.GetBgra32(1, 1) & 0xFF);
    }

    [Fact]
    public async Task Plain_rle_requires_exact_pixel_count()
    {
        using var framebuffer = new FramebufferModel(5, 1, ProtocolLimits.Default);

        _ = await DecodeAsync(
            framebuffer,
            PixelFormat.WinArdBgra32,
            new FramebufferRect(0, 0, 5, 1),
            [128, 3, 0, 0, 2, 4, 0, 0, 1]);

        Assert.Equal([3u, 3u, 3u, 4u, 4u], BlueValues(framebuffer));
    }

    [Fact]
    public async Task Rectangle_is_decoded_as_independent_64_pixel_tiles()
    {
        using var framebuffer = new FramebufferModel(65, 1, ProtocolLimits.Default);
        var uncompressed = new List<byte> { 0 };
        for (var index = 0; index < 64; index++)
        {
            uncompressed.Add((byte)index);
            uncompressed.Add(0);
            uncompressed.Add(0);
        }

        uncompressed.AddRange([1, 200, 0, 0]);

        _ = await DecodeAsync(
            framebuffer,
            PixelFormat.WinArdBgra32,
            new FramebufferRect(0, 0, 65, 1),
            uncompressed.ToArray());

        Assert.Equal(63u, framebuffer.GetBgra32(63, 0) & 0xFF);
        Assert.Equal(200u, framebuffer.GetBgra32(64, 0) & 0xFF);
    }

    [Fact]
    public async Task Default_limits_accept_a_4k_solid_rectangle()
    {
        const int width = 3840;
        const int height = 2160;
        using var framebuffer = new FramebufferModel(width, height, ProtocolLimits.Default);

        var result = await DecodeAsync(
            framebuffer,
            PixelFormat.WinArdBgra32,
            new FramebufferRect(0, 0, width, height),
            Enumerable.Repeat(new byte[] { 1, 7, 8, 9 }, 2040).SelectMany(tile => tile).ToArray());

        Assert.Equal(new FramebufferRect(0, 0, width, height), Assert.Single(result.PixelContentRects));
        Assert.Equal(7u, framebuffer.GetBgra32(width - 1, height - 1) & 0xFF);
    }

    [Fact]
    public async Task Big_endian_cpixel_omits_unused_most_significant_byte()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var format = new PixelFormat(32, 24, 1, 1, 255, 255, 255, 16, 8, 0);

        _ = await DecodeAsync(
            framebuffer,
            format,
            new FramebufferRect(0, 0, 1, 1),
            [1, 0x11, 0x22, 0x33]);

        Assert.Equal(0xFF112233u, framebuffer.GetBgra32(0, 0));
    }

    [Fact]
    public async Task Raw_tile_decodes_winard_little_endian_rgb565_fixture()
    {
        using var framebuffer = new FramebufferModel(4, 1, ProtocolLimits.Default);

        _ = await DecodeAsync(
            framebuffer,
            PixelFormat.WinArdRgb565,
            new FramebufferRect(0, 0, 4, 1),
            [0, 0, 0xF8, 0xE0, 0x07, 0x1F, 0, 0xFF, 0xFF]);

        Assert.Equal(
            [0xFFFF0000u, 0xFF00FF00u, 0xFF0000FFu, 0xFFFFFFFFu],
            Enumerable.Range(0, 4).Select(x => framebuffer.GetBgra32(x, 0)));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(129)]
    [InlineData(130)]
    [InlineData(255)]
    public async Task Unsupported_subencoding_is_rejected_without_modifying_framebuffer(byte subencoding)
    {
        using var framebuffer = SeededFramebuffer();
        var before = framebuffer.GetPixelsBgra32();

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeAsync(
                framebuffer,
                PixelFormat.WinArdBgra32,
                new FramebufferRect(0, 0, 2, 1),
                [subencoding]));

        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task Packed_palette_index_out_of_range_is_rejected_atomically()
    {
        using var framebuffer = SeededFramebuffer();
        var before = framebuffer.GetPixelsBgra32();

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeAsync(
                framebuffer,
                PixelFormat.WinArdBgra32,
                new FramebufferRect(0, 0, 2, 1),
                [3, 1, 0, 0, 2, 0, 0, 3, 0, 0, 0b1100_0000]));

        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task Plain_rle_run_overflow_is_rejected_atomically()
    {
        using var framebuffer = SeededFramebuffer();
        var before = framebuffer.GetPixelsBgra32();

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeAsync(
                framebuffer,
                PixelFormat.WinArdBgra32,
                new FramebufferRect(0, 0, 2, 1),
                [128, 1, 0, 0, 2]));

        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 0, 0 })]
    [InlineData(new byte[] { 1, 1, 0, 0, 0xEE })]
    public async Task Short_or_trailing_tile_data_is_rejected_atomically(byte[] uncompressed)
    {
        using var framebuffer = SeededFramebuffer();
        var before = framebuffer.GetPixelsBgra32();

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeAsync(
                framebuffer,
                PixelFormat.WinArdBgra32,
                new FramebufferRect(0, 0, 2, 1),
                uncompressed));

        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task Compressed_length_above_limit_is_rejected_before_payload_read()
    {
        var limits = new ProtocolLimits(1024, 1024, 1024, 4096, 1024, 4, 1024);
        using var framebuffer = new FramebufferModel(16, 16, limits);
        var payload = Compress([1, 1, 2, 3]);
        await using var stream = new MemoryStream(WithLength(payload));
        var reader = new RfbReader(stream, limits);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            new ZrleEncoding().DecodeAsync(
                reader,
                framebuffer,
                new FramebufferRect(0, 0, 1, 1),
                CancellationToken.None).AsTask());

        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task Decompressed_length_above_limit_is_rejected_atomically()
    {
        var limits = new ProtocolLimits(1024, 1024, 1024, 4096, 1024, 1024, 3);
        using var framebuffer = new FramebufferModel(16, 16, limits);
        var before = framebuffer.GetPixelsBgra32();

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeAsync(
                framebuffer,
                PixelFormat.WinArdBgra32,
                new FramebufferRect(0, 0, 1, 1),
                [1, 1, 2, 3]));

        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task Work_budget_includes_compressed_backing_before_payload_read()
    {
        var limits = new ProtocolLimits(1024, 1024, 1024, 13, 1024, 1024, 5);
        using var framebuffer = new FramebufferModel(16, 16, limits);
        var payload = Compress([1, 1, 2, 3]);
        await using var stream = new MemoryStream(WithLength(payload));
        var reader = new RfbReader(stream, limits);
        await using var decoder = new ZrleEncoding();

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            decoder.DecodeAsync(
                reader,
                framebuffer,
                new FramebufferRect(0, 0, 1, 1),
                CancellationToken.None).AsTask());

        Assert.Equal(4, stream.Position);
    }

    [Theory]
    [MemberData(nameof(PixelReaderFixtures))]
    public async Task Raw_and_solid_tiles_use_legal_pixel_or_cpixel_layout(
        PixelFormat format,
        byte subencoding,
        byte[] wirePixel,
        uint expectedBgra)
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        _ = await DecodeAsync(
            framebuffer,
            format,
            new FramebufferRect(0, 0, 1, 1),
            [subencoding, .. wirePixel]);

        Assert.Equal(expectedBgra, framebuffer.GetBgra32(0, 0));
    }

    [Fact]
    public async Task Packed_palette_reuses_ordinary_16_bit_pixel_reader()
    {
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        var format = new PixelFormat(16, 16, 1, 1, 31, 63, 31, 11, 5, 0);

        _ = await DecodeAsync(
            framebuffer,
            format,
            new FramebufferRect(0, 0, 2, 1),
            [2, 0xF8, 0, 0, 0x1F, 0b0100_0000]);

        Assert.Equal(0xFFFF0000u, framebuffer.GetBgra32(0, 0));
        Assert.Equal(0xFF0000FFu, framebuffer.GetBgra32(1, 0));
    }

    [Fact]
    public async Task Plain_rle_reuses_high_three_byte_cpixel_reader()
    {
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        var format = new PixelFormat(32, 24, 0, 1, 255, 255, 255, 24, 16, 8);

        _ = await DecodeAsync(
            framebuffer,
            format,
            new FramebufferRect(0, 0, 2, 1),
            [128, 0x33, 0x22, 0x11, 1]);

        Assert.Equal(0xFF112233u, framebuffer.GetBgra32(0, 0));
        Assert.Equal(0xFF112233u, framebuffer.GetBgra32(1, 0));
    }

    [Fact]
    public async Task Bcl_zlib_stream_can_resume_after_sync_flush_chunk_boundary()
    {
        var (firstChunk, secondChunk) = CreateSharedZlibChunks(
            Enumerable.Repeat((byte)'A', 4096).ToArray(),
            Enumerable.Repeat((byte)'A', 4096).ToArray());
        await using var input = new SegmentedReadStream();
        input.SetSegment(firstChunk);
        await using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);

        var first = await ReadUntilBoundaryAsync(zlib);
        input.SetSegment(secondChunk);
        var second = await ReadUntilBoundaryAsync(zlib);

        Assert.Equal(4096, first.Length);
        Assert.Equal(4096, second.Length);
        Assert.All(first, value => Assert.Equal((byte)'A', value));
        Assert.All(second, value => Assert.Equal((byte)'A', value));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DecompressIndependentAsync(secondChunk));
    }

    [Fact]
    public async Task Decoder_reuses_zlib_state_for_consecutive_rectangles()
    {
        var firstTile = RawTile(1);
        var secondTile = RawTile(2);
        var (firstChunk, secondChunk) = CreateSharedZlibChunks(firstTile, secondTile);
        using var framebuffer = new FramebufferModel(64, 64, ProtocolLimits.Default);
        await using var decoder = new ZrleEncoding();
        var rectangle = new FramebufferRect(0, 0, 64, 64);

        _ = await DecodeChunkAsync(decoder, framebuffer, rectangle, firstChunk, CancellationToken.None);
        _ = await DecodeChunkAsync(decoder, framebuffer, rectangle, secondChunk, CancellationToken.None);

        Assert.All(
            framebuffer.GetPixelsBgra32().Chunk(4),
            pixel => Assert.Equal([2, 0, 0, 255], pixel));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DecompressIndependentAsync(secondChunk));
    }

    [Fact]
    public async Task Framebuffer_update_session_reuses_zlib_state_across_updates()
    {
        var (firstChunk, secondChunk) = CreateSharedZlibChunks(RawTile(1), RawTile(2));
        using var framebuffer = new FramebufferModel(64, 64, ProtocolLimits.Default);
        await using var session = FramebufferUpdateReader.CreateSession(
            framebuffer,
            PixelFormat.WinArdBgra32);

        _ = await session.ApplyAsync(
            new MemoryStream(ZrleUpdate(64, 64, firstChunk)),
            CancellationToken.None);
        _ = await session.ApplyAsync(
            new MemoryStream(ZrleUpdate(64, 64, secondChunk)),
            CancellationToken.None);

        Assert.Equal(2u, framebuffer.GetBgra32(0, 0) & 0xFF);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Framebuffer_session_preserves_zrle_stream_across_pixel_format_changes(
        bool bgraFirst)
    {
        var firstFormat = bgraFirst ? PixelFormat.WinArdBgra32 : PixelFormat.WinArdRgb565;
        var secondFormat = bgraFirst ? PixelFormat.WinArdRgb565 : PixelFormat.WinArdBgra32;
        var firstTile = RawTile(bgraFirst ? [1, 2, 3] : [0, 0xF8]);
        var secondTile = RawTile(bgraFirst ? [0x1F, 0] : [4, 5, 6]);
        var expectedSecondPixel = bgraFirst ? 0xFF0000FFu : 0xFF060504u;
        var (firstChunk, secondChunk) = CreateSharedZlibChunks(firstTile, secondTile);
        using var framebuffer = new FramebufferModel(64, 64, ProtocolLimits.Default);
        await using var session = FramebufferUpdateReader.CreateSession(framebuffer, firstFormat);

        _ = await session.ApplyAsync(
            new MemoryStream(ZrleUpdate(64, 64, firstChunk)),
            CancellationToken.None);
        await session.ReconfigurePixelFormatAsync(secondFormat, CancellationToken.None);
        _ = await session.ApplyAsync(
            new MemoryStream(ZrleUpdate(64, 64, secondChunk)),
            CancellationToken.None);

        Assert.Equal(expectedSecondPixel, framebuffer.GetBgra32(63, 63));
    }

    [Fact]
    public async Task Complete_zlib_stream_is_rejected()
    {
        using var framebuffer = SeededFramebuffer();
        await using var decoder = new ZrleEncoding();

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeChunkAsync(
                decoder,
                framebuffer,
                new FramebufferRect(0, 0, 1, 1),
                CompressComplete([1, 1, 2, 3]),
                CancellationToken.None));

        Assert.Contains("Z_SYNC_FLUSH", exception.Message, StringComparison.Ordinal);
        Assert.Equal([9u, 6u], BlueValues(framebuffer));
    }

    [Fact]
    public async Task Complete_zlib_with_trailing_sync_marker_is_rejected_atomically()
    {
        using var framebuffer = SeededFramebuffer();
        await using var decoder = new ZrleEncoding();
        var compressed = CompressComplete([1, 1, 2, 3])
            .Concat(new byte[] { 0xDE, 0xAD, 0, 0, 0xFF, 0xFF })
            .ToArray();

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeChunkAsync(
                decoder,
                framebuffer,
                new FramebufferRect(0, 0, 1, 1),
                compressed,
                CancellationToken.None));

        Assert.Equal([9u, 6u], BlueValues(framebuffer));
    }

    [Fact]
    public async Task Truncated_sync_flush_chunk_faults_context_until_reset()
    {
        var (firstChunk, secondChunk) = CreateSharedZlibChunks(RawTile(1), RawTile(2));
        using var framebuffer = new FramebufferModel(64, 64, ProtocolLimits.Default);
        await using var decoder = new ZrleEncoding();
        var rectangle = new FramebufferRect(0, 0, 64, 64);
        _ = await DecodeChunkAsync(decoder, framebuffer, rectangle, firstChunk, CancellationToken.None);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeChunkAsync(
                decoder,
                framebuffer,
                rectangle,
                secondChunk[..^1],
                CancellationToken.None));
        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeChunkAsync(decoder, framebuffer, rectangle, secondChunk, CancellationToken.None));

        await decoder.ResetAsync();
        _ = await DecodeChunkAsync(
            decoder,
            framebuffer,
            rectangle,
            Compress(RawTile(3)),
            CancellationToken.None);

        Assert.Equal(3u, framebuffer.GetBgra32(0, 0) & 0xFF);
    }

    [Fact]
    public async Task Cancellation_after_chunk_read_faults_context_until_reset()
    {
        var payload = Compress([1, 1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        await using var stream = new CancelAfterReadStream(WithLength(payload), cancellation);
        var reader = new RfbReader(stream, ProtocolLimits.Default);
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        await using var decoder = new ZrleEncoding();
        var rectangle = new FramebufferRect(0, 0, 1, 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            decoder.DecodeAsync(reader, framebuffer, rectangle, cancellation.Token).AsTask());
        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeChunkAsync(decoder, framebuffer, rectangle, payload, CancellationToken.None));

        await decoder.ResetAsync();
        _ = await DecodeChunkAsync(decoder, framebuffer, rectangle, payload, CancellationToken.None);
        Assert.Equal(0xFF030201u, framebuffer.GetBgra32(0, 0));
    }

    [Fact]
    public async Task Disposed_decoder_rejects_further_rectangles()
    {
        var decoder = new ZrleEncoding();
        await decoder.DisposeAsync();
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            DecodeChunkAsync(
                decoder,
                framebuffer,
                new FramebufferRect(0, 0, 1, 1),
                Compress([1, 1, 2, 3]),
                CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_disposals_share_completion_while_decode_is_in_flight()
    {
        await using var stream = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource();
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var decoder = new ZrleEncoding();
        var decode = decoder.DecodeAsync(
            new RfbReader(stream, ProtocolLimits.Default),
            framebuffer,
            new FramebufferRect(0, 0, 1, 1),
            cancellation.Token).AsTask();
        await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var firstDispose = decoder.DisposeAsync().AsTask();
        var secondDispose = decoder.DisposeAsync().AsTask();

        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decode);
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<EncodingDecodeResult> DecodeAsync(
        FramebufferModel framebuffer,
        PixelFormat format,
        FramebufferRect rectangle,
        byte[] uncompressed)
    {
        var payload = Compress(uncompressed);
        var reader = new RfbReader(new MemoryStream(WithLength(payload)), framebuffer.Limits);
        return await new ZrleEncoding(format).DecodeAsync(
            reader,
            framebuffer,
            rectangle,
            CancellationToken.None);
    }

    private static async Task<EncodingDecodeResult> DecodeChunkAsync(
        ZrleEncoding decoder,
        FramebufferModel framebuffer,
        FramebufferRect rectangle,
        byte[] compressed,
        CancellationToken cancellationToken)
    {
        var reader = new RfbReader(new MemoryStream(WithLength(compressed)), framebuffer.Limits);
        return await decoder.DecodeAsync(reader, framebuffer, rectangle, cancellationToken);
    }

    private static FramebufferModel SeededFramebuffer()
    {
        var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        framebuffer.ApplyRaw(new FramebufferRect(0, 0, 2, 1), [9, 8, 7, 255, 6, 5, 4, 255]);
        return framebuffer;
    }

    private static byte[] Compress(byte[] uncompressed)
    {
        using var output = new MemoryStream();
        using var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
        zlib.Write(uncompressed);
        zlib.Flush();
        return output.ToArray();
    }

    private static byte[] CompressComplete(byte[] uncompressed)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(uncompressed);
        }

        return output.ToArray();
    }

    private static (byte[] First, byte[] Second) CreateSharedZlibChunks(byte[] first, byte[] second)
    {
        using var output = new MemoryStream();
        using var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
        zlib.Write(first);
        zlib.Flush();
        var firstLength = checked((int)output.Length);
        zlib.Write(second);
        zlib.Flush();
        var all = output.ToArray();
        return (all[..firstLength], all[firstLength..]);
    }

    private static async Task<byte[]> ReadUntilBoundaryAsync(Stream stream)
    {
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return output.ToArray();
            }

            output.Write(buffer, 0, read);
        }
    }

    private static async Task DecompressIndependentAsync(byte[] compressed)
    {
        await using var input = new MemoryStream(compressed);
        await using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        _ = await ReadUntilBoundaryAsync(zlib);
    }

    private static byte[] WithLength(byte[] payload)
    {
        var message = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(message, checked((uint)payload.Length));
        payload.CopyTo(message, 4);
        return message;
    }

    private static byte[] RawTile(byte blue)
    {
        var tile = new byte[1 + (64 * 64 * 3)];
        for (var offset = 1; offset < tile.Length; offset += 3)
        {
            tile[offset] = blue;
        }

        return tile;
    }

    private static byte[] RawTile(byte[] encodedPixel)
    {
        var tile = new byte[checked(1 + (64 * 64 * encodedPixel.Length))];
        for (var offset = 1; offset < tile.Length; offset += encodedPixel.Length)
        {
            encodedPixel.CopyTo(tile, offset);
        }

        return tile;
    }

    private static byte[] ZrleUpdate(ushort width, ushort height, byte[] compressed)
    {
        var message = new byte[20 + compressed.Length];
        message[2] = 0;
        message[3] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(10), height);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(12), (int)RfbEncodingType.Zrle);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(16), checked((uint)compressed.Length));
        compressed.CopyTo(message, 20);
        return message;
    }

    private static uint[] BlueValues(FramebufferModel framebuffer) =>
        Enumerable.Range(0, framebuffer.Width)
            .Select(x => framebuffer.GetBgra32(x, 0) & 0xFF)
            .ToArray();

    public static IEnumerable<object[]> PixelReaderFixtures()
    {
        var fixtures = new (PixelFormat Format, byte[] Pixel, uint Expected)[]
        {
            (
                new PixelFormat(8, 8, 0, 1, 7, 7, 3, 5, 2, 0),
                [0xE0],
                0xFFFF0000u),
            (
                new PixelFormat(16, 16, 0, 1, 31, 63, 31, 11, 5, 0),
                [0, 0xF8],
                0xFFFF0000u),
            (
                new PixelFormat(16, 16, 1, 1, 31, 63, 31, 11, 5, 0),
                [0xF8, 0],
                0xFFFF0000u),
            (
                new PixelFormat(32, 24, 0, 1, 255, 255, 255, 16, 8, 0),
                [0x33, 0x22, 0x11],
                0xFF112233u),
            (
                new PixelFormat(32, 24, 1, 1, 255, 255, 255, 16, 8, 0),
                [0x11, 0x22, 0x33],
                0xFF112233u),
            (
                new PixelFormat(32, 24, 0, 1, 255, 255, 255, 24, 16, 8),
                [0x33, 0x22, 0x11],
                0xFF112233u),
            (
                new PixelFormat(32, 24, 1, 1, 255, 255, 255, 24, 16, 8),
                [0x11, 0x22, 0x33],
                0xFF112233u),
            (
                new PixelFormat(32, 24, 0, 1, 255, 255, 255, 24, 8, 0),
                [0x33, 0x22, 0, 0x11],
                0xFF112233u),
            (
                new PixelFormat(32, 24, 1, 1, 255, 255, 255, 24, 8, 0),
                [0x11, 0, 0x22, 0x33],
                0xFF112233u),
            (
                new PixelFormat(32, 3, 0, 1, 1, 1, 1, 31, 15, 0),
                [1, 0x80, 0, 0x80],
                0xFFFFFFFFu),
            (
                new PixelFormat(32, 32, 0, 1, 255, 255, 255, 16, 8, 0),
                [0x33, 0x22, 0x11, 0xAA],
                0xFF112233u),
            (
                new PixelFormat(32, 32, 1, 1, 255, 255, 255, 16, 8, 0),
                [0xAA, 0x11, 0x22, 0x33],
                0xFF112233u),
        };

        foreach (var fixture in fixtures)
        {
            yield return [fixture.Format, (byte)0, fixture.Pixel, fixture.Expected];
            yield return [fixture.Format, (byte)1, fixture.Pixel, fixture.Expected];
        }
    }

    private sealed class SegmentedReadStream : Stream
    {
        private byte[]? _segment;
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public void SetSegment(byte[] segment)
        {
            if (_segment is not null && _position != _segment.Length)
            {
                throw new InvalidOperationException("The previous segment was not fully consumed.");
            }

            _segment = segment;
            _position = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_segment is null || _position == _segment.Length)
            {
                return 0;
            }

            var count = Math.Min(buffer.Length, _segment.Length - _position);
            _segment.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancelAfterReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            if (Position == Length)
            {
                cancellation.Cancel();
            }

            return ValueTask.FromResult(read);
        }
    }
}
