using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using WinARD.Remote.Protocol.Errors;
using WinARD.Testing.Streams;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Encodings;

public sealed class ZlibEncodingTests
{
    [Fact]
    public void Protocol_limits_expose_independent_bounded_zlib_budgets()
    {
        var limits = new ProtocolLimits(1024, 1024, 1024, 4096, 1024, 800, 900, 700, 600);

        Assert.Equal(700, limits.MaxZlibCompressedBytes);
        Assert.Equal(600, limits.MaxZlibDecompressedBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProtocolLimits(1024, 1024, 1024, 4096, 1024, 800, 900, 0, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProtocolLimits(1024, 1024, 1024, 4096, 1024, 800, 900, 700, 4097));
    }

    [Fact]
    public async Task Big_endian_length_and_raw_pixels_are_decoded()
    {
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        await using var decoder = new ZlibEncoding(PixelFormat.WinArdBgra32);
        var rectangle = new FramebufferRect(0, 0, 2, 1);
        var compressed = Compress([1, 2, 3, 0, 4, 5, 6, 0]);

        var result = await DecodeChunkAsync(decoder, framebuffer, rectangle, compressed);

        Assert.Equal(6, decoder.EncodingId);
        Assert.Equal(6, (int)RfbEncodingType.Zlib);
        Assert.Equal(rectangle, Assert.Single(result.DirtyRects));
        Assert.Equal(rectangle, Assert.Single(result.PixelContentRects));
        Assert.Equal(0xFF030201u, framebuffer.GetBgra32(0, 0));
        Assert.Equal(0xFF060504u, framebuffer.GetBgra32(1, 0));
    }

    [Fact]
    public async Task Fragmented_length_and_payload_are_read_exactly()
    {
        using var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        await using var decoder = new ZlibEncoding();
        var compressed = Compress([1, 2, 3, 0, 4, 5, 6, 0]);
        await using var stream = new ChunkedReadStream(WithLength(compressed), 1);

        _ = await decoder.DecodeAsync(
            new RfbReader(stream, framebuffer.Limits),
            framebuffer,
            new FramebufferRect(0, 0, 2, 1),
            CancellationToken.None);

        Assert.Equal(0xFF060504u, framebuffer.GetBgra32(1, 0));
    }

    [Fact]
    public async Task Successful_decode_releases_compressed_segment_reference()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        await using var decoder = new ZlibEncoding();

        _ = await DecodeChunkAsync(
            decoder,
            framebuffer,
            new FramebufferRect(0, 0, 1, 1),
            Compress([1, 2, 3, 0]));

        var inflater = GetPrivateField(decoder, "_inflater");
        var compressedInput = GetPrivateField(inflater, "_compressedInput");
        Assert.Null(GetPrivateFieldValue(compressedInput, "_segment"));
    }

    [Fact]
    public async Task Decoder_reuses_dictionary_across_rectangles_but_instances_are_isolated()
    {
        var firstPixels = Enumerable.Repeat(new byte[] { 7, 8, 9, 0 }, 4096).SelectMany(x => x).ToArray();
        var secondPixels = firstPixels.ToArray();
        var (firstChunk, secondChunk) = CreateSharedZlibChunks(firstPixels, secondPixels);
        using var framebuffer = new FramebufferModel(64, 64, ProtocolLimits.Default);
        await using var decoder = new ZlibEncoding();

        _ = await DecodeChunkAsync(decoder, framebuffer, new FramebufferRect(0, 0, 64, 64), firstChunk);
        _ = await DecodeChunkAsync(decoder, framebuffer, new FramebufferRect(0, 0, 64, 64), secondChunk);
        Assert.Equal(0xFF090807u, framebuffer.GetBgra32(63, 63));

        await using var independent = new ZlibEncoding();
        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeChunkAsync(independent, framebuffer, new FramebufferRect(0, 0, 64, 64), secondChunk));
    }

    [Fact]
    public async Task Rgb565_pixels_are_converted_to_bgra()
    {
        using var framebuffer = new FramebufferModel(4, 1, ProtocolLimits.Default);
        await using var decoder = new ZlibEncoding(PixelFormat.WinArdRgb565);

        _ = await DecodeChunkAsync(
            decoder,
            framebuffer,
            new FramebufferRect(0, 0, 4, 1),
            Compress([0, 0xF8, 0xE0, 0x07, 0x1F, 0, 0xFF, 0xFF]));

        Assert.Equal(
            [0xFFFF0000u, 0xFF00FF00u, 0xFF0000FFu, 0xFFFFFFFFu],
            Enumerable.Range(0, 4).Select(x => framebuffer.GetBgra32(x, 0)));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 0 })]
    [InlineData(new byte[] { 1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0 })]
    public async Task Decompressed_size_must_equal_exact_raw_pixel_length(byte[] raw)
    {
        using var framebuffer = SeededFramebuffer();
        var before = framebuffer.GetPixelsBgra32();
        await using var decoder = new ZlibEncoding();

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeChunkAsync(
                decoder,
                framebuffer,
                new FramebufferRect(0, 0, 2, 1),
                Compress(raw)));

        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task Complete_stream_and_trailing_bytes_are_rejected_atomically()
    {
        using var framebuffer = SeededFramebuffer();
        var before = framebuffer.GetPixelsBgra32();
        await using var decoder = new ZlibEncoding();
        var compressed = CompressComplete([1, 2, 3, 0, 4, 5, 6, 0])
            .Concat(new byte[] { 0xDE, 0xAD, 0, 0, 0xFF, 0xFF })
            .ToArray();

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeChunkAsync(decoder, framebuffer, new FramebufferRect(0, 0, 2, 1), compressed));

        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public async Task Compressed_and_decompressed_limits_reject_before_payload_read()
    {
        var compressed = Compress([1, 2, 3, 0, 4, 5, 6, 0]);
        var compressedLimits = new ProtocolLimits(1024, 1024, 1024, 4096, 1024, 1024, 1024, 4, 1024);
        using var firstFramebuffer = new FramebufferModel(2, 1, compressedLimits);
        await using var firstStream = new MemoryStream(WithLength(compressed));
        await using var firstDecoder = new ZlibEncoding();
        await Assert.ThrowsAsync<RfbProtocolException>(() => firstDecoder.DecodeAsync(
            new RfbReader(firstStream, compressedLimits),
            firstFramebuffer,
            new FramebufferRect(0, 0, 2, 1),
            CancellationToken.None).AsTask());
        Assert.Equal(4, firstStream.Position);

        var decompressedLimits = new ProtocolLimits(1024, 1024, 1024, 4096, 1024, 1024, 1024, 1024, 7);
        using var secondFramebuffer = new FramebufferModel(2, 1, decompressedLimits);
        await using var secondStream = new MemoryStream(WithLength(compressed));
        await using var secondDecoder = new ZlibEncoding();
        await Assert.ThrowsAsync<RfbProtocolException>(() => secondDecoder.DecodeAsync(
            new RfbReader(secondStream, decompressedLimits),
            secondFramebuffer,
            new FramebufferRect(0, 0, 2, 1),
            CancellationToken.None).AsTask());
        Assert.Equal(4, secondStream.Position);
    }

    [Fact]
    public async Task Work_budget_is_reserved_before_payload_read()
    {
        var compressed = Compress([1, 2, 3, 0]);
        var limits = new ProtocolLimits(1024, 1024, 1024, 15, 1024, 1024, 15, 1024, 15);
        using var framebuffer = new FramebufferModel(1, 1, limits);
        await using var stream = new MemoryStream(WithLength(compressed));
        await using var decoder = new ZlibEncoding();

        await Assert.ThrowsAsync<RfbProtocolException>(() => decoder.DecodeAsync(
            new RfbReader(stream, limits),
            framebuffer,
            new FramebufferRect(0, 0, 1, 1),
            CancellationToken.None).AsTask());

        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task Cancellation_after_payload_faults_until_reset()
    {
        var compressed = Compress([1, 2, 3, 0]);
        using var cancellation = new CancellationTokenSource();
        await using var stream = new CancelAfterReadStream(WithLength(compressed), cancellation);
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        await using var decoder = new ZlibEncoding();
        var rectangle = new FramebufferRect(0, 0, 1, 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoder.DecodeAsync(
            new RfbReader(stream, framebuffer.Limits), framebuffer, rectangle, cancellation.Token).AsTask());
        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeChunkAsync(decoder, framebuffer, rectangle, compressed));

        await decoder.ResetAsync();
        _ = await DecodeChunkAsync(decoder, framebuffer, rectangle, compressed);
        Assert.Equal(0xFF030201u, framebuffer.GetBgra32(0, 0));
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_rejects_further_decode()
    {
        var decoder = new ZlibEncoding();
        await Task.WhenAll(decoder.DisposeAsync().AsTask(), decoder.DisposeAsync().AsTask());
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            DecodeChunkAsync(decoder, framebuffer, new FramebufferRect(0, 0, 1, 1), Compress([1, 2, 3, 0])));
    }

    [Fact]
    public async Task Truncated_sync_flush_chunk_faults_context_until_reset()
    {
        var compressed = Compress([1, 2, 3, 0]);
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        await using var decoder = new ZlibEncoding();
        var rectangle = new FramebufferRect(0, 0, 1, 1);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeChunkAsync(decoder, framebuffer, rectangle, compressed[..^1]));
        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            DecodeChunkAsync(decoder, framebuffer, rectangle, compressed));

        await decoder.ResetAsync();
        _ = await DecodeChunkAsync(decoder, framebuffer, rectangle, compressed);
        Assert.Equal(0xFF030201u, framebuffer.GetBgra32(0, 0));
    }

    [Fact]
    public async Task Concurrent_disposals_wait_for_in_flight_decode()
    {
        await using var stream = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource();
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var decoder = new ZlibEncoding();
        var decode = decoder.DecodeAsync(
            new RfbReader(stream, framebuffer.Limits),
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

    [Fact]
    public async Task Framebuffer_session_registers_zlib_and_reuses_dictionary_across_updates()
    {
        var firstPixels = Enumerable.Repeat(new byte[] { 1, 2, 3, 0 }, 4096).SelectMany(x => x).ToArray();
        var secondPixels = Enumerable.Repeat(new byte[] { 4, 5, 6, 0 }, 4096).SelectMany(x => x).ToArray();
        var (firstChunk, secondChunk) = CreateSharedZlibChunks(firstPixels, secondPixels);
        using var framebuffer = new FramebufferModel(64, 64, ProtocolLimits.Default);
        await using var session = FramebufferUpdateReader.CreateSession(framebuffer, PixelFormat.WinArdBgra32);

        _ = await session.ApplyAsync(new MemoryStream(ZlibUpdate(64, 64, firstChunk)), CancellationToken.None);
        _ = await session.ApplyAsync(new MemoryStream(ZlibUpdate(64, 64, secondChunk)), CancellationToken.None);

        Assert.Equal(0xFF060504u, framebuffer.GetBgra32(63, 63));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Framebuffer_session_preserves_zlib_stream_across_pixel_format_changes(
        bool bgraFirst)
    {
        var firstFormat = bgraFirst ? PixelFormat.WinArdBgra32 : PixelFormat.WinArdRgb565;
        var secondFormat = bgraFirst ? PixelFormat.WinArdRgb565 : PixelFormat.WinArdBgra32;
        var firstPixels = Enumerable.Repeat(
            bgraFirst ? new byte[] { 1, 2, 3, 0 } : [0, 0xF8],
            4096).SelectMany(pixel => pixel).ToArray();
        var secondPixels = Enumerable.Repeat(
            bgraFirst ? new byte[] { 0x1F, 0 } : [4, 5, 6, 0],
            4096).SelectMany(pixel => pixel).ToArray();
        var expectedSecondPixel = bgraFirst ? 0xFF0000FFu : 0xFF060504u;
        var (firstChunk, secondChunk) = CreateSharedZlibChunks(firstPixels, secondPixels);
        using var framebuffer = new FramebufferModel(64, 64, ProtocolLimits.Default);
        await using var session = FramebufferUpdateReader.CreateSession(framebuffer, firstFormat);

        _ = await session.ApplyAsync(
            new MemoryStream(ZlibUpdate(64, 64, firstChunk)),
            CancellationToken.None);
        await session.ReconfigurePixelFormatAsync(secondFormat, CancellationToken.None);
        _ = await session.ApplyAsync(
            new MemoryStream(ZlibUpdate(64, 64, secondChunk)),
            CancellationToken.None);

        Assert.Equal(expectedSecondPixel, framebuffer.GetBgra32(63, 63));
    }

    [Fact]
    public async Task One_shot_reader_rejects_zlib_before_consuming_compressed_length()
    {
        var compressed = Compress([1, 2, 3, 0]);
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        await using var stream = new MemoryStream(ZlibUpdate(1, 1, compressed));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() => FramebufferUpdateReader.ApplyAsync(
            stream,
            framebuffer,
            PixelFormat.WinArdBgra32,
            CancellationToken.None));

        Assert.Contains("CreateSession", exception.Message, StringComparison.Ordinal);
        Assert.Equal(16, stream.Position);
    }

    private static async Task<EncodingDecodeResult> DecodeChunkAsync(
        ZlibEncoding decoder,
        FramebufferModel framebuffer,
        FramebufferRect rectangle,
        byte[] compressed)
    {
        var message = WithLength(compressed);
        return await decoder.DecodeAsync(
            new RfbReader(new MemoryStream(message), framebuffer.Limits),
            framebuffer,
            rectangle,
            CancellationToken.None);
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

    private static byte[] WithLength(byte[] compressed)
    {
        var message = new byte[sizeof(uint) + compressed.Length];
        BinaryPrimitives.WriteUInt32BigEndian(message, checked((uint)compressed.Length));
        compressed.CopyTo(message, sizeof(uint));
        return message;
    }

    private static FramebufferModel SeededFramebuffer()
    {
        var framebuffer = new FramebufferModel(2, 1, ProtocolLimits.Default);
        framebuffer.ApplyRaw(new FramebufferRect(0, 0, 2, 1), [9, 8, 7, 255, 6, 5, 4, 255]);
        return framebuffer;
    }

    private static byte[] ZlibUpdate(ushort width, ushort height, byte[] compressed)
    {
        var message = new byte[20 + compressed.Length];
        message[3] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(10), height);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(12), (int)RfbEncodingType.Zlib);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(16), checked((uint)compressed.Length));
        compressed.CopyTo(message, 20);
        return message;
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

    private static object GetPrivateField(object instance, string name) =>
        GetPrivateFieldValue(instance, name) ?? throw new InvalidOperationException($"{name} was null.");

    private static object? GetPrivateFieldValue(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? (instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is null
            ? throw new InvalidOperationException($"Field {name} was not found.")
            : null);
}
