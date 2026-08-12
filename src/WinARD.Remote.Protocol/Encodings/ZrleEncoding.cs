using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class ZrleEncoding : IRfbEncodingDecoder, IReconfigurablePixelFormatDecoder, IAsyncDisposable
{
    private const int TileSize = 64;
    private PixelFormat _pixelFormat;
    private int _encodedPixelLength;
    private int _wirePixelOffset;
    private readonly PersistentZlibInflater _inflater;

    public ZrleEncoding()
        : this(PixelFormat.WinArdBgra32)
    {
    }

    public ZrleEncoding(PixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(pixelFormat);
        _pixelFormat = pixelFormat;
        (_encodedPixelLength, _wirePixelOffset) = GetPixelLayout(pixelFormat);
        _inflater = new PersistentZlibInflater("ZRLE");
    }

    public int EncodingId => (int)RfbEncodingType.Zrle;

    void IReconfigurablePixelFormatDecoder.ValidatePixelFormat(PixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(pixelFormat);
        _ = GetPixelLayout(pixelFormat);
    }

    void IReconfigurablePixelFormatDecoder.CommitPixelFormat(PixelFormat pixelFormat)
    {
        var (encodedPixelLength, wirePixelOffset) = GetPixelLayout(pixelFormat);
        _pixelFormat = pixelFormat;
        _encodedPixelLength = encodedPixelLength;
        _wirePixelOffset = wirePixelOffset;
    }

    public async ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(framebuffer);
        return await _inflater.RunAsync(
            async (operation, operationCancellationToken) =>
            {
                operationCancellationToken.ThrowIfCancellationRequested();
                rectangle.ValidateWithin(framebuffer.Width, framebuffer.Height);

                reader.ReserveFramebufferUpdateBytes(sizeof(uint));
                var compressedLengthValue = await reader.ReadUInt32Async(operationCancellationToken)
                    .ConfigureAwait(false);
                if (compressedLengthValue > int.MaxValue ||
                    compressedLengthValue > framebuffer.Limits.MaxZrleCompressedBytes)
                {
                    throw new RfbProtocolException(
                        $"ZRLE compressed length {compressedLengthValue} exceeds the configured limit of " +
                        $"{framebuffer.Limits.MaxZrleCompressedBytes} bytes.");
                }

                var compressedLength = checked((int)compressedLengthValue);
                reader.ReserveFramebufferUpdateBytes(compressedLength);
                reader.ReserveFramebufferUpdateWorkBytes(compressedLength);
                var bgraLength = PixelConverter.CheckedBgraLength(
                    rectangle.Width,
                    rectangle.Height,
                    framebuffer.Limits);
                reader.ReserveFramebufferUpdateWorkBytes(checked((long)bgraLength * 2));

                var compressed = await reader.ReadFramebufferPayloadBytesAsync(
                    compressedLength,
                    operationCancellationToken).ConfigureAwait(false);
                PersistentZlibInflater.DecompressedChunk decompressed = default;
                byte[]? bgra = null;
                try
                {
                    var tileCount = checked(
                        ((rectangle.Width + TileSize - 1) / TileSize) *
                        ((rectangle.Height + TileSize - 1) / TileSize));
                    var maximumValidLength = checked(
                        ((long)rectangle.Width * rectangle.Height * (_encodedPixelLength + 1)) + tileCount);
                    var capacity = checked((int)Math.Min(
                        framebuffer.Limits.MaxZrleDecompressedBytes,
                        maximumValidLength));
                    reader.ReserveFramebufferUpdateWorkBytes(capacity);
                    decompressed = await operation.DecompressChunkAsync(
                        compressed,
                        capacity,
                        framebuffer.Limits.MaxZrleDecompressedBytes,
                        operationCancellationToken).ConfigureAwait(false);
                    bgra = DecodeTiles(
                        decompressed.Buffer.AsSpan(0, decompressed.Length),
                        rectangle.Width,
                        rectangle.Height,
                        operationCancellationToken);
                    operationCancellationToken.ThrowIfCancellationRequested();
                    framebuffer.ApplyRaw(rectangle, bgra);
                    return new EncodingDecodeResult([rectangle], [rectangle]);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(compressed);
                    if (decompressed.Buffer is not null)
                    {
                        CryptographicOperations.ZeroMemory(decompressed.Buffer);
                    }

                    if (bgra is not null)
                    {
                        CryptographicOperations.ZeroMemory(bgra);
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resets the inflater for a new connection. It cannot resynchronize a damaged connection.</summary>
    public ValueTask ResetAsync(CancellationToken cancellationToken = default) =>
        _inflater.ResetAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inflater.DisposeAsync();

    private byte[] DecodeTiles(
        ReadOnlySpan<byte> data,
        int rectangleWidth,
        int rectangleHeight,
        CancellationToken cancellationToken)
    {
        var bgra = new byte[checked(rectangleWidth * rectangleHeight * 4)];
        var offset = 0;
        for (var tileY = 0; tileY < rectangleHeight; tileY += TileSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tileHeight = Math.Min(TileSize, rectangleHeight - tileY);
            for (var tileX = 0; tileX < rectangleWidth; tileX += TileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tileWidth = Math.Min(TileSize, rectangleWidth - tileX);
                DecodeTile(
                    data,
                    ref offset,
                    bgra,
                    rectangleWidth,
                    tileX,
                    tileY,
                    tileWidth,
                    tileHeight,
                    cancellationToken);
            }
        }

        if (offset != data.Length)
        {
            throw new RfbProtocolException(
                $"ZRLE rectangle contains {data.Length - offset} trailing decompressed bytes.");
        }

        return bgra;
    }

    private void DecodeTile(
        ReadOnlySpan<byte> data,
        ref int offset,
        Span<byte> destination,
        int destinationWidth,
        int tileX,
        int tileY,
        int tileWidth,
        int tileHeight,
        CancellationToken cancellationToken)
    {
        var subencoding = ReadByte(data, ref offset);
        switch (subencoding)
        {
            case 0:
                DecodeRawTile(
                    data,
                    ref offset,
                    destination,
                    destinationWidth,
                    tileX,
                    tileY,
                    tileWidth,
                    tileHeight,
                    cancellationToken);
                return;
            case 1:
                var solid = ReadPixel(data, ref offset);
                FillTile(
                    destination,
                    destinationWidth,
                    tileX,
                    tileY,
                    tileWidth,
                    tileHeight,
                    solid,
                    cancellationToken);
                return;
            case >= 2 and <= 16:
                DecodePackedPaletteTile(
                    data,
                    ref offset,
                    destination,
                    destinationWidth,
                    tileX,
                    tileY,
                    tileWidth,
                    tileHeight,
                    subencoding,
                    cancellationToken);
                return;
            case 128:
                DecodePlainRleTile(
                    data,
                    ref offset,
                    destination,
                    destinationWidth,
                    tileX,
                    tileY,
                    tileWidth,
                    tileHeight,
                    cancellationToken);
                return;
            default:
                throw new RfbProtocolException($"Unsupported ZRLE subencoding {subencoding}.");
        }
    }

    private void DecodeRawTile(
        ReadOnlySpan<byte> data,
        ref int offset,
        Span<byte> destination,
        int destinationWidth,
        int tileX,
        int tileY,
        int tileWidth,
        int tileHeight,
        CancellationToken cancellationToken)
    {
        for (var y = 0; y < tileHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < tileWidth; x++)
            {
                WritePixel(
                    destination,
                    destinationWidth,
                    tileX + x,
                    tileY + y,
                    ReadPixel(data, ref offset));
            }
        }
    }

    private void DecodePackedPaletteTile(
        ReadOnlySpan<byte> data,
        ref int offset,
        Span<byte> destination,
        int destinationWidth,
        int tileX,
        int tileY,
        int tileWidth,
        int tileHeight,
        int paletteSize,
        CancellationToken cancellationToken)
    {
        var palette = new uint[paletteSize];
        for (var index = 0; index < paletteSize; index++)
        {
            palette[index] = ReadPixel(data, ref offset);
        }

        var bitsPerIndex = paletteSize <= 2 ? 1 : paletteSize <= 4 ? 2 : 4;
        var rowBytes = checked((tileWidth * bitsPerIndex + 7) / 8);
        for (var y = 0; y < tileHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = ReadBytes(data, ref offset, rowBytes);
            for (var x = 0; x < tileWidth; x++)
            {
                var bitOffset = x * bitsPerIndex;
                var shift = 8 - bitsPerIndex - (bitOffset % 8);
                var paletteIndex = (row[bitOffset / 8] >> shift) & ((1 << bitsPerIndex) - 1);
                if (paletteIndex >= paletteSize)
                {
                    throw new RfbProtocolException(
                        $"ZRLE palette index {paletteIndex} exceeds palette size {paletteSize}.");
                }

                WritePixel(
                    destination,
                    destinationWidth,
                    tileX + x,
                    tileY + y,
                    palette[paletteIndex]);
            }
        }

        Array.Clear(palette);
    }

    private void DecodePlainRleTile(
        ReadOnlySpan<byte> data,
        ref int offset,
        Span<byte> destination,
        int destinationWidth,
        int tileX,
        int tileY,
        int tileWidth,
        int tileHeight,
        CancellationToken cancellationToken)
    {
        var tilePixelCount = checked(tileWidth * tileHeight);
        var decodedPixels = 0;
        while (decodedPixels < tilePixelCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pixel = ReadPixel(data, ref offset);
            var runLength = 1;
            byte extension;
            do
            {
                extension = ReadByte(data, ref offset);
                runLength = checked(runLength + extension);
                if (runLength > tilePixelCount - decodedPixels)
                {
                    throw new RfbProtocolException("ZRLE RLE run exceeds the tile pixel count.");
                }
            }
            while (extension == byte.MaxValue);

            for (var runIndex = 0; runIndex < runLength; runIndex++)
            {
                if ((runIndex & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var tilePixelIndex = decodedPixels + runIndex;
                WritePixel(
                    destination,
                    destinationWidth,
                    tileX + (tilePixelIndex % tileWidth),
                    tileY + (tilePixelIndex / tileWidth),
                    pixel);
            }

            decodedPixels += runLength;
        }
    }

    private uint ReadPixel(ReadOnlySpan<byte> data, ref int offset)
    {
        var encodedPixel = ReadBytes(data, ref offset, _encodedPixelLength);
        if (_encodedPixelLength == _pixelFormat.BytesPerPixel)
        {
            return PixelConverter.ToBgra32Pixel(encodedPixel, _pixelFormat);
        }

        Span<byte> wirePixel = stackalloc byte[4];
        wirePixel.Clear();
        encodedPixel.CopyTo(wirePixel.Slice(_wirePixelOffset, _encodedPixelLength));
        return PixelConverter.ToBgra32Pixel(wirePixel, _pixelFormat);
    }

    private static (int EncodedLength, int WireOffset) GetPixelLayout(PixelFormat pixelFormat)
    {
        if (pixelFormat.BitsPerPixel != 32 || pixelFormat.Depth > 24)
        {
            return (pixelFormat.BytesPerPixel, 0);
        }

        var componentMask =
            ((uint)pixelFormat.RedMax << pixelFormat.RedShift) |
            ((uint)pixelFormat.GreenMax << pixelFormat.GreenShift) |
            ((uint)pixelFormat.BlueMax << pixelFormat.BlueShift);
        var fitsLowThreeBytes = (componentMask & 0xFF000000u) == 0;
        var fitsHighThreeBytes = (componentMask & 0x000000FFu) == 0;
        if (!fitsLowThreeBytes && !fitsHighThreeBytes)
        {
            return (pixelFormat.BytesPerPixel, 0);
        }

        var usesHighThreeBytes = !fitsLowThreeBytes;
        var wireOffset = pixelFormat.BigEndian == usesHighThreeBytes ? 0 : 1;
        return (3, wireOffset);
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int offset)
    {
        if ((uint)offset >= (uint)data.Length)
        {
            throw new RfbProtocolException("ZRLE tile data ended before the tile was complete.");
        }

        return data[offset++];
    }

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (count < 0 || offset > data.Length - count)
        {
            throw new RfbProtocolException("ZRLE tile data ended before the tile was complete.");
        }

        var result = data.Slice(offset, count);
        offset += count;
        return result;
    }

    private static void FillTile(
        Span<byte> destination,
        int destinationWidth,
        int tileX,
        int tileY,
        int tileWidth,
        int tileHeight,
        uint pixel,
        CancellationToken cancellationToken)
    {
        for (var y = 0; y < tileHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < tileWidth; x++)
            {
                WritePixel(destination, destinationWidth, tileX + x, tileY + y, pixel);
            }
        }
    }

    private static void WritePixel(
        Span<byte> destination,
        int destinationWidth,
        int x,
        int y,
        uint pixel) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination.Slice(checked(((y * destinationWidth) + x) * 4), 4),
            pixel);

}
