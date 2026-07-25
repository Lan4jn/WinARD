using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Framebuffer;

public static class FramebufferUpdateReader
{
    private const int MaximumRectangleCount = 4096;

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

        using var session = CreateSession(framebuffer, pixelFormat);
        return await session.ApplyAsync(stream, cancellationToken);
    }

    public static FramebufferUpdateSession CreateSession(
        Framebuffer framebuffer,
        PixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(framebuffer);
        ArgumentNullException.ThrowIfNull(pixelFormat);
        return new FramebufferUpdateSession(framebuffer, CreateDecoders(pixelFormat));
    }

    internal static async Task<FramebufferUpdateResult> ApplyAsync(
        Stream stream,
        Framebuffer framebuffer,
        IReadOnlyDictionary<int, IRfbEncodingDecoder> decoders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(framebuffer);
        ArgumentNullException.ThrowIfNull(decoders);
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
        var pixelContentRects = new List<FramebufferRect>(rectangleCount);
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
            if (!decoders.TryGetValue(encodingId, out var decoder))
            {
                throw new RfbProtocolException($"Unsupported RFB encoding ID {encodingId}.");
            }

            if (encoding != RfbEncodingType.Cursor && (width == 0 || height == 0))
            {
                throw new RfbProtocolException($"RFB encoding {encodingId} requires non-zero rectangle dimensions.");
            }

            var previousWidth = framebuffer.Width;
            var previousHeight = framebuffer.Height;
            var previousCursor = framebuffer.Cursor;
            var rectangle = encoding == RfbEncodingType.Cursor
                ? FramebufferRect.CreateCursorRectangle(x, y, width, height)
                : new FramebufferRect(x, y, width, height);
            var decodeResult = await decoder.DecodeAsync(reader, framebuffer, rectangle, cancellationToken);
            dirtyRects.AddRange(decodeResult.DirtyRects);
            if (previousWidth != framebuffer.Width || previousHeight != framebuffer.Height)
            {
                pixelContentRects.Clear();
            }
            else
            {
                pixelContentRects.AddRange(decodeResult.PixelContentRects);
            }

            if (!ReferenceEquals(previousCursor, framebuffer.Cursor))
            {
                cursor = framebuffer.Cursor;
            }

            desktopResized |= previousWidth != framebuffer.Width || previousHeight != framebuffer.Height;
        }

        return new FramebufferUpdateResult(dirtyRects, pixelContentRects, cursor, desktopResized);
    }

    private static Dictionary<int, IRfbEncodingDecoder> CreateDecoders(PixelFormat pixelFormat) =>
        new IRfbEncodingDecoder[]
        {
            new RawEncoding(pixelFormat),
            new CopyRectEncoding(),
            new ZrleEncoding(pixelFormat),
            new DesktopSizeEncoding(),
            new CursorEncoding(pixelFormat),
        }.ToDictionary(decoder => decoder.EncodingId);
}
