using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class ZrleEncoding : IRfbEncodingDecoder, IAsyncDisposable
{
    private const int TileSize = 64;
    private readonly PixelFormat _pixelFormat;
    private readonly int _encodedPixelLength;
    private readonly int _wirePixelOffset;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private SegmentedReadStream? _compressedInput;
    private ZLibStream? _zlib;
    private DecoderState _state = DecoderState.Active;

    public ZrleEncoding()
        : this(PixelFormat.WinArdBgra32)
    {
    }

    public ZrleEncoding(PixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(pixelFormat);
        _pixelFormat = pixelFormat;
        (_encodedPixelLength, _wirePixelOffset) = GetPixelLayout(pixelFormat);
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
        ThrowIfUnavailable();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            rectangle.ValidateWithin(framebuffer.Width, framebuffer.Height);

            reader.ReserveFramebufferUpdateBytes(sizeof(uint));
            var compressedLengthValue = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
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

            var compressed = await reader.ReadFramebufferPayloadBytesAsync(compressedLength, cancellationToken)
                .ConfigureAwait(false);
            DecompressedChunk decompressed = default;
            byte[]? bgra = null;
            var contextTouched = false;
            try
            {
                contextTouched = true;
                ValidateSyncFlushBoundary(compressed);
                decompressed = await DecompressChunkAsync(
                    compressed,
                    reader,
                    rectangle.Width,
                    rectangle.Height,
                    framebuffer.Limits.MaxZrleDecompressedBytes,
                    cancellationToken).ConfigureAwait(false);
                bgra = DecodeTiles(
                    decompressed.Buffer.AsSpan(0, decompressed.Length),
                    rectangle.Width,
                    rectangle.Height,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
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
                if (decompressed.Buffer is not null)
                {
                    CryptographicOperations.ZeroMemory(decompressed.Buffer);
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

    /// <summary>Resets the inflater for a new connection. It cannot resynchronize a damaged connection.</summary>
    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            DisposeContext();
            InitializeContext();
            lock (_stateLock)
            {
                _state = DecoderState.Active;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_state is DecoderState.Disposing or DecoderState.Disposed)
            {
                return;
            }

            _state = DecoderState.Disposing;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeContext();
            lock (_stateLock)
            {
                _state = DecoderState.Disposed;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void InitializeContext()
    {
        _compressedInput = new SegmentedReadStream();
        _zlib = new ZLibStream(_compressedInput, CompressionMode.Decompress, leaveOpen: true);
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
        lock (_stateLock)
        {
            if (_state == DecoderState.Active)
            {
                _state = DecoderState.Faulted;
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        lock (_stateLock)
        {
            switch (_state)
            {
                case DecoderState.Active:
                    return;
                case DecoderState.Faulted:
                    throw new RfbProtocolException(
                        "The ZRLE decompression context is faulted and must be reset for a new connection.");
                case DecoderState.Disposing:
                case DecoderState.Disposed:
                    ObjectDisposedException.ThrowIf(true, this);
                    return;
                default:
                    throw new InvalidOperationException("Unknown ZRLE decoder state.");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(
                _state is DecoderState.Disposing or DecoderState.Disposed,
                this);
        }
    }

    private static void ValidateSyncFlushBoundary(ReadOnlySpan<byte> compressed)
    {
        var hasSyncFlushBoundary =
            compressed.Length >= 4 &&
            compressed[^4] == 0 &&
            compressed[^3] == 0 &&
            compressed[^2] == byte.MaxValue &&
            compressed[^1] == byte.MaxValue;
        if (!hasSyncFlushBoundary)
        {
            throw new RfbProtocolException(
                "ZRLE compressed data must end at a Z_SYNC_FLUSH boundary.");
        }
    }

    private async Task<DecompressedChunk> DecompressChunkAsync(
        byte[] compressed,
        RfbReader reader,
        int rectangleWidth,
        int rectangleHeight,
        int decompressedLimit,
        CancellationToken cancellationToken)
    {
        try
        {
            _compressedInput!.SetSegment(compressed);
            var tileCount = checked(
                ((rectangleWidth + TileSize - 1) / TileSize) *
                ((rectangleHeight + TileSize - 1) / TileSize));
            var maximumValidLength = checked(
                ((long)rectangleWidth * rectangleHeight * (_encodedPixelLength + 1)) + tileCount);
            var capacity = checked((int)Math.Min(decompressedLimit, maximumValidLength));
            reader.ReserveFramebufferUpdateWorkBytes(capacity);
            var output = new byte[capacity];
            var length = 0;
            try
            {
                while (true)
                {
                    if (length == output.Length)
                    {
                        var overflow = new byte[1];
                        var overflowRead = await _zlib!.ReadAsync(overflow, cancellationToken)
                            .ConfigureAwait(false);
                        CryptographicOperations.ZeroMemory(overflow);
                        if (overflowRead != 0)
                        {
                            throw new RfbProtocolException(
                                $"ZRLE decompressed length exceeds the configured limit of {decompressedLimit} bytes.");
                        }

                        break;
                    }

                    var read = await _zlib!.ReadAsync(output.AsMemory(length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    length = checked(length + read);
                }

                if (_compressedInput.Position != compressed.Length)
                {
                    throw new RfbProtocolException(
                        "ZRLE compressed data contains a completed zlib stream or trailing bytes before its Z_SYNC_FLUSH boundary.");
                }

                return new DecompressedChunk(output, length);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(output);
                throw;
            }
        }
        catch (InvalidDataException exception)
        {
            throw new RfbProtocolException("ZRLE payload is not a valid zlib stream.", exception);
        }
    }

    private enum DecoderState
    {
        Active,
        Faulted,
        Disposing,
        Disposed,
    }

    private readonly record struct DecompressedChunk(byte[] Buffer, int Length);

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

            var prefixLength = _segment.Length - 4;
            var remainingInPortion = _position < prefixLength
                ? prefixLength - _position
                : _segment.Length - _position;
            var count = Math.Min(buffer.Length, remainingInPortion);
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
