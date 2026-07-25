using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class ZrleEncoding : IRfbEncodingDecoder, IDisposable
{
    private const int TileSize = 64;
    private readonly PixelFormat _pixelFormat;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SegmentedReadStream? _compressedInput;
    private ZLibStream? _zlib;
    private uint _adlerA;
    private uint _adlerB;
    private bool _faulted;
    private bool _disposed;

    public ZrleEncoding()
        : this(PixelFormat.WinArdBgra32)
    {
    }

    public ZrleEncoding(PixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(pixelFormat);
        _pixelFormat = pixelFormat;
        InitializeContext();
    }

    public int EncodingId => (int)RfbEncodingType.Zrle;

    public async ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(framebuffer);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            rectangle.ValidateWithin(framebuffer.Width, framebuffer.Height);
            ValidatePixelFormat();

            reader.ReserveFramebufferUpdateBytes(sizeof(uint));
            var compressedLengthValue = await reader.ReadUInt32Async(cancellationToken);
            if (compressedLengthValue > int.MaxValue ||
                compressedLengthValue > framebuffer.Limits.MaxZrleCompressedBytes)
            {
                throw new RfbProtocolException(
                    $"ZRLE compressed length {compressedLengthValue} exceeds the configured limit of " +
                    $"{framebuffer.Limits.MaxZrleCompressedBytes} bytes.");
            }

            var compressedLength = checked((int)compressedLengthValue);
            reader.ReserveFramebufferUpdateBytes(compressedLength);
            var bgraLength = PixelConverter.CheckedBgraLength(
                rectangle.Width,
                rectangle.Height,
                framebuffer.Limits);
            reader.ReserveFramebufferUpdateWorkBytes(checked((long)bgraLength * 2));

            var compressed = await reader.ReadFramebufferPayloadBytesAsync(compressedLength, cancellationToken);
            byte[]? decompressed = null;
            byte[]? bgra = null;
            var contextTouched = false;
            try
            {
                contextTouched = true;
                decompressed = await DecompressChunkAsync(
                    compressed,
                    reader,
                    framebuffer.Limits.MaxZrleDecompressedBytes,
                    cancellationToken);
                ValidateChunkBoundary(compressed, decompressed);
                bgra = DecodeTiles(decompressed, rectangle.Width, rectangle.Height);
                framebuffer.ApplyRaw(rectangle, bgra);
                return new EncodingDecodeResult([rectangle], [rectangle]);
            }
            catch
            {
                if (contextTouched)
                {
                    FaultContext();
                }

                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(compressed);
                if (decompressed is not null)
                {
                    CryptographicOperations.ZeroMemory(decompressed);
                }

                if (bgra is not null)
                {
                    CryptographicOperations.ZeroMemory(bgra);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Reset()
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DisposeContext();
            InitializeContext();
            _faulted = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            DisposeContext();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void InitializeContext()
    {
        _compressedInput = new SegmentedReadStream();
        _zlib = new ZLibStream(_compressedInput, CompressionMode.Decompress, leaveOpen: true);
        _adlerA = 1;
        _adlerB = 0;
    }

    private void DisposeContext()
    {
        _zlib?.Dispose();
        _compressedInput?.Dispose();
        _zlib = null;
        _compressedInput = null;
    }

    private void FaultContext()
    {
        DisposeContext();
        _faulted = true;
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new RfbProtocolException(
                "The ZRLE decompression context is faulted and must be reset before reuse.");
        }
    }

    private void ValidateChunkBoundary(ReadOnlySpan<byte> compressed, ReadOnlySpan<byte> decompressed)
    {
        const uint adlerModulus = 65521;
        ulong nextA = _adlerA;
        ulong nextB = _adlerB;
        var offset = 0;
        while (offset < decompressed.Length)
        {
            var blockLength = Math.Min(5552, decompressed.Length - offset);
            var block = decompressed.Slice(offset, blockLength);
            foreach (var value in block)
            {
                nextA += value;
                nextB += nextA;
            }

            nextA %= adlerModulus;
            nextB %= adlerModulus;
            offset += blockLength;
        }

        var hasSyncFlushBoundary =
            compressed.Length >= 4 &&
            compressed[^4] == 0 &&
            compressed[^3] == 0 &&
            compressed[^2] == byte.MaxValue &&
            compressed[^1] == byte.MaxValue;
        var expectedAdler = checked((uint)((nextB << 16) | nextA));
        var hasCompleteStreamBoundary =
            compressed.Length >= 4 &&
            BinaryPrimitives.ReadUInt32BigEndian(compressed[^4..]) == expectedAdler;
        if (!hasSyncFlushBoundary && !hasCompleteStreamBoundary)
        {
            throw new RfbProtocolException(
                "ZRLE compressed data ended without a complete zlib stream or Z_SYNC_FLUSH boundary.");
        }

        _adlerA = checked((uint)nextA);
        _adlerB = checked((uint)nextB);
    }

    private async Task<byte[]> DecompressChunkAsync(
        byte[] compressed,
        RfbReader reader,
        int decompressedLimit,
        CancellationToken cancellationToken)
    {
        try
        {
            _compressedInput!.SetSegment(compressed);
            using var output = new MemoryStream();
            var buffer = new byte[Math.Min(8192, decompressedLimit)];
            try
            {
                while (true)
                {
                    var read = await _zlib!.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    var total = checked(output.Length + read);
                    if (total > decompressedLimit)
                    {
                        throw new RfbProtocolException(
                            $"ZRLE decompressed length exceeds the configured limit of {decompressedLimit} bytes.");
                    }

                    reader.ReserveFramebufferUpdateWorkBytes(checked((long)read * 2));
                    output.Write(buffer, 0, read);
                }

                return output.ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
        catch (InvalidDataException exception)
        {
            throw new RfbProtocolException("ZRLE payload is not a valid zlib stream.", exception);
        }
    }

    private byte[] DecodeTiles(ReadOnlySpan<byte> data, int rectangleWidth, int rectangleHeight)
    {
        var bgra = new byte[checked(rectangleWidth * rectangleHeight * 4)];
        var offset = 0;
        for (var tileY = 0; tileY < rectangleHeight; tileY += TileSize)
        {
            var tileHeight = Math.Min(TileSize, rectangleHeight - tileY);
            for (var tileX = 0; tileX < rectangleWidth; tileX += TileSize)
            {
                var tileWidth = Math.Min(TileSize, rectangleWidth - tileX);
                DecodeTile(data, ref offset, bgra, rectangleWidth, tileX, tileY, tileWidth, tileHeight);
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
        int tileHeight)
    {
        var subencoding = ReadByte(data, ref offset);
        switch (subencoding)
        {
            case 0:
                DecodeRawTile(data, ref offset, destination, destinationWidth, tileX, tileY, tileWidth, tileHeight);
                return;
            case 1:
                var solid = ReadCpixel(data, ref offset);
                FillTile(destination, destinationWidth, tileX, tileY, tileWidth, tileHeight, solid);
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
                    subencoding);
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
                    tileHeight);
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
        int tileHeight)
    {
        for (var y = 0; y < tileHeight; y++)
        {
            for (var x = 0; x < tileWidth; x++)
            {
                WritePixel(
                    destination,
                    destinationWidth,
                    tileX + x,
                    tileY + y,
                    ReadCpixel(data, ref offset));
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
        int paletteSize)
    {
        var palette = new uint[paletteSize];
        for (var index = 0; index < paletteSize; index++)
        {
            palette[index] = ReadCpixel(data, ref offset);
        }

        var bitsPerIndex = paletteSize <= 2 ? 1 : paletteSize <= 4 ? 2 : 4;
        var rowBytes = checked((tileWidth * bitsPerIndex + 7) / 8);
        for (var y = 0; y < tileHeight; y++)
        {
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
        int tileHeight)
    {
        var tilePixelCount = checked(tileWidth * tileHeight);
        var decodedPixels = 0;
        while (decodedPixels < tilePixelCount)
        {
            var pixel = ReadCpixel(data, ref offset);
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

    private uint ReadCpixel(ReadOnlySpan<byte> data, ref int offset)
    {
        var cpixelLength = _pixelFormat.Depth <= 24 ? 3 : 4;
        var cpixel = ReadBytes(data, ref offset, cpixelLength);
        Span<byte> wirePixel = stackalloc byte[4];
        if (cpixelLength == 4)
        {
            cpixel.CopyTo(wirePixel);
        }
        else if (_pixelFormat.BigEndian)
        {
            cpixel.CopyTo(wirePixel[1..]);
        }
        else
        {
            cpixel.CopyTo(wirePixel);
        }

        return PixelConverter.ToBgra32Pixel(wirePixel, _pixelFormat);
    }

    private void ValidatePixelFormat()
    {
        if (_pixelFormat.BitsPerPixel != 32 || !_pixelFormat.TrueColor)
        {
            throw new RfbProtocolException("ZRLE requires a 32-bit true-color pixel format.");
        }

        if (_pixelFormat.Depth <= 24)
        {
            var highestComponentBit = Math.Max(
                _pixelFormat.RedShift + BitWidth(_pixelFormat.RedMax),
                Math.Max(
                    _pixelFormat.GreenShift + BitWidth(_pixelFormat.GreenMax),
                    _pixelFormat.BlueShift + BitWidth(_pixelFormat.BlueMax)));
            if (highestComponentBit > 24)
            {
                throw new RfbProtocolException(
                    "ZRLE CPIXEL cannot omit a byte that contains true-color component bits.");
            }
        }
    }

    private static int BitWidth(ushort value)
    {
        var width = 0;
        while (value != 0)
        {
            width++;
            value >>= 1;
        }

        return width;
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
        uint pixel)
    {
        for (var y = 0; y < tileHeight; y++)
        {
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
            ArgumentNullException.ThrowIfNull(segment);
            if (_segment is not null && _position != _segment.Length)
            {
                throw new RfbProtocolException(
                    "The previous ZRLE compressed segment was not fully consumed.");
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
}
