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

        return await ApplyAsync(
            stream,
            framebuffer,
            CreateOneShotDecoders(pixelFormat),
            cancellationToken).ConfigureAwait(false);
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
        var messageType = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (messageType != 0)
        {
            throw new RfbProtocolException($"Expected FramebufferUpdate message type 0, received {messageType}.");
        }

        _ = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        var rectangleCount = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
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
            var x = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var y = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var width = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var height = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var encodingId = await reader.ReadInt32Async(cancellationToken).ConfigureAwait(false);
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
            var decodeResult = await decoder.DecodeAsync(reader, framebuffer, rectangle, cancellationToken)
                .ConfigureAwait(false);
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

    private static Dictionary<int, IRfbEncodingDecoder> CreateOneShotDecoders(PixelFormat pixelFormat) =>
        new IRfbEncodingDecoder[]
        {
            new RawEncoding(pixelFormat),
            new CopyRectEncoding(),
            new SessionRequiredZrleEncoding(),
            new DesktopSizeEncoding(),
            new CursorEncoding(pixelFormat),
        }.ToDictionary(decoder => decoder.EncodingId);

    private sealed class SessionRequiredZrleEncoding : IRfbEncodingDecoder
    {
        public int EncodingId => (int)RfbEncodingType.Zrle;

        public ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            Framebuffer framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<EncodingDecodeResult>(
                new RfbProtocolException(
                    "ZRLE requires a persistent per-connection session; use FramebufferUpdateReader.CreateSession."));
    }
}
