using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class ZlibEncoding : IRfbEncodingDecoder, IReconfigurablePixelFormatDecoder, IAsyncDisposable
{
    private PixelFormat _pixelFormat;
    private readonly PersistentZlibInflater _inflater;

    public ZlibEncoding()
        : this(PixelFormat.WinArdBgra32)
    {
    }

    public ZlibEncoding(PixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(pixelFormat);
        _pixelFormat = pixelFormat;
        _inflater = new PersistentZlibInflater("Zlib");
    }

    public int EncodingId => (int)RfbEncodingType.Zlib;

    void IReconfigurablePixelFormatDecoder.ValidatePixelFormat(PixelFormat pixelFormat) =>
        ArgumentNullException.ThrowIfNull(pixelFormat);

    void IReconfigurablePixelFormatDecoder.CommitPixelFormat(PixelFormat pixelFormat) =>
        _pixelFormat = pixelFormat;

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
                rectangle.ValidateWithin(framebuffer.Width, framebuffer.Height);
                reader.ReserveFramebufferUpdateBytes(sizeof(uint));
                var compressedLengthValue = await reader.ReadUInt32Async(operationCancellationToken)
                    .ConfigureAwait(false);
                if (compressedLengthValue > int.MaxValue ||
                    compressedLengthValue > framebuffer.Limits.MaxZlibCompressedBytes)
                {
                    throw new RfbProtocolException(
                        $"Zlib compressed length {compressedLengthValue} exceeds the configured limit of " +
                        $"{framebuffer.Limits.MaxZlibCompressedBytes} bytes.");
                }

                var compressedLength = checked((int)compressedLengthValue);
                var wireLength = PixelConverter.CheckedWireLength(
                    rectangle.Width,
                    rectangle.Height,
                    _pixelFormat,
                    framebuffer.Limits);
                var bgraLength = PixelConverter.CheckedBgraLength(
                    rectangle.Width,
                    rectangle.Height,
                    framebuffer.Limits);
                if (wireLength > framebuffer.Limits.MaxZlibDecompressedBytes)
                {
                    throw RfbProtocolException.Create(
                        $"Zlib decompressed length {wireLength} exceeds the configured limit of " +
                        $"{framebuffer.Limits.MaxZlibDecompressedBytes} bytes.",
                        new RfbProtocolFailureInfo(
                            RfbProtocolFailureKind.DecoderFailure,
                            DecoderFailureReason: RfbDecoderFailureReason.OutputLimitExceeded));
                }

                reader.ReserveFramebufferUpdateBytes(compressedLength);
                reader.ReserveFramebufferUpdateWorkBytes(
                    checked((long)compressedLength + wireLength + bgraLength + bgraLength));
                var compressed = await reader.ReadFramebufferPayloadBytesAsync(
                    compressedLength,
                    operationCancellationToken).ConfigureAwait(false);
                PersistentZlibInflater.DecompressedChunk decompressed = default;
                byte[]? bgra = null;
                try
                {
                    decompressed = await operation.DecompressChunkAsync(
                        compressed,
                        wireLength,
                        framebuffer.Limits.MaxZlibDecompressedBytes,
                        operationCancellationToken,
                        RfbDecoderFailureReason.DecompressedLengthMismatch).ConfigureAwait(false);
                    if (decompressed.Length != wireLength)
                    {
                        throw RfbProtocolException.Create(
                            $"Zlib rectangle decompressed to {decompressed.Length} bytes; expected exactly {wireLength} bytes.",
                            new RfbProtocolFailureInfo(
                                RfbProtocolFailureKind.DecoderFailure,
                                DecoderFailureReason: RfbDecoderFailureReason.DecompressedLengthMismatch));
                    }

                    bgra = PixelConverter.ToBgra32(
                        decompressed.Buffer,
                        rectangle.Width,
                        rectangle.Height,
                        _pixelFormat,
                        framebuffer.Limits);
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

    public ValueTask ResetAsync(CancellationToken cancellationToken = default) =>
        _inflater.ResetAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inflater.DisposeAsync();
}
