using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Framebuffer;

public static class FramebufferUpdateReader
{
    private const int MaximumRectangleCount = 4096;
    private static readonly Dictionary<RfbEncodingType, IRfbEncodingDecoder> Decoders =
        new IRfbEncodingDecoder[]
        {
            new RawEncoding(),
            new CopyRectEncoding(),
            new DesktopSizeEncoding(),
            new CursorEncoding(),
        }.ToDictionary(decoder => decoder.EncodingType);

    /// <summary>Consumes a complete server-to-client FramebufferUpdate message, including message type zero.</summary>
    public static async Task<FramebufferUpdateResult> ApplyAsync(
        Stream stream,
        Framebuffer framebuffer,
        PixelFormat pixelFormat,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(framebuffer);
        ArgumentNullException.ThrowIfNull(pixelFormat);
        cancellationToken.ThrowIfCancellationRequested();

        var reader = new RfbReader(stream, framebuffer.Limits);
        var messageType = await reader.ReadByteAsync(cancellationToken);
        if (messageType != 0)
        {
            throw new RfbProtocolException($"Expected FramebufferUpdate message type 0, received {messageType}.");
        }

        _ = await reader.ReadByteAsync(cancellationToken);
        var rectangleCount = await reader.ReadUInt16Async(cancellationToken);
        if (rectangleCount > MaximumRectangleCount)
        {
            throw new RfbProtocolException(
                $"FramebufferUpdate rectangle count {rectangleCount} exceeds the limit of {MaximumRectangleCount}.");
        }

        var dirtyRects = new List<FramebufferRect>(rectangleCount);
        RemoteCursor? cursor = null;
        var desktopResized = false;
        for (var index = 0; index < rectangleCount; index++)
        {
            var x = await reader.ReadUInt16Async(cancellationToken);
            var y = await reader.ReadUInt16Async(cancellationToken);
            var width = await reader.ReadUInt16Async(cancellationToken);
            var height = await reader.ReadUInt16Async(cancellationToken);
            var encodingId = await reader.ReadInt32Async(cancellationToken);
            var encoding = (RfbEncodingType)encodingId;
            if (!Decoders.TryGetValue(encoding, out var decoder))
            {
                throw new RfbProtocolException($"Unsupported RFB encoding ID {encodingId}.");
            }

            if (encoding != RfbEncodingType.Cursor && (width == 0 || height == 0))
            {
                throw new RfbProtocolException($"RFB encoding {encodingId} requires non-zero rectangle dimensions.");
            }

            var decoded = await decoder.DecodeAsync(
                reader, framebuffer, x, y, width, height, pixelFormat, cancellationToken);
            if (decoded.DirtyRect is { } dirtyRect)
            {
                dirtyRects.Add(dirtyRect);
            }

            if (decoded.Cursor is not null)
            {
                cursor = decoded.Cursor;
            }

            desktopResized |= decoded.DesktopResized;
        }

        return new FramebufferUpdateResult(dirtyRects, cursor, desktopResized);
    }
}
