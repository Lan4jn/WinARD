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
        PixelFormat pixelFormat,
        params IRfbEncodingDecoder[] additionalDecoders)
    {
        ArgumentNullException.ThrowIfNull(framebuffer);
        ArgumentNullException.ThrowIfNull(pixelFormat);
        ArgumentNullException.ThrowIfNull(additionalDecoders);
        var registeredEncodingIds = new HashSet<int>
        {
            (int)RfbEncodingType.Raw,
            (int)RfbEncodingType.CopyRect,
            (int)RfbEncodingType.Zlib,
            (int)RfbEncodingType.Zrle,
            (int)RfbEncodingType.DesktopSize,
            (int)RfbEncodingType.Cursor,
            (int)RfbEncodingType.ArdDisplayInfo,
            (int)RfbEncodingType.ArdDisplayInfo2,
        };
        var additionalDecoderSnapshot = new List<(int EncodingId, IRfbEncodingDecoder Decoder)>(
            additionalDecoders.Length);
        foreach (var decoder in additionalDecoders)
        {
            ArgumentNullException.ThrowIfNull(decoder);
            if (decoder is IReconfigurablePixelFormatDecoder)
            {
                throw new ArgumentException(
                    "Additional RFB decoders cannot participate in the internal pixel format transaction.",
                    nameof(additionalDecoders));
            }

            var encodingId = decoder.EncodingId;
            if (!registeredEncodingIds.Add(encodingId))
            {
                throw new ArgumentException(
                    $"An RFB decoder for encoding ID {encodingId} is already registered.",
                    nameof(additionalDecoders));
            }

            additionalDecoderSnapshot.Add((encodingId, decoder));
        }

        var decoders = CreateDecoders(pixelFormat);
        foreach (var (encodingId, decoder) in additionalDecoderSnapshot)
        {
            decoders.Add(encodingId, decoder);
        }

        return new FramebufferUpdateSession(framebuffer, decoders);
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
        byte messageType;
        try
        {
            messageType = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.UnexpectedServerMessage,
                RfbProtocolReadStage.ServerMessageType));
        }

        if (messageType != 0)
        {
            throw RfbProtocolException.Create(
                $"Expected FramebufferUpdate message type 0, received {messageType}.",
                new RfbProtocolFailureInfo(
                    RfbProtocolFailureKind.UnexpectedServerMessage,
                    RfbProtocolReadStage.ServerMessageType,
                    messageType));
        }

        return await ApplyBodyAsync(reader, framebuffer, decoders, cancellationToken).ConfigureAwait(false);
    }

    internal static Task<FramebufferUpdateResult> ApplyBodyAsync(
        Stream stream,
        Framebuffer framebuffer,
        IReadOnlyDictionary<int, IRfbEncodingDecoder> decoders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ApplyBodyAsync(new RfbReader(stream, framebuffer.Limits), framebuffer, decoders, cancellationToken);
    }

    private static async Task<FramebufferUpdateResult> ApplyBodyAsync(
        RfbReader reader,
        Framebuffer framebuffer,
        IReadOnlyDictionary<int, IRfbEncodingDecoder> decoders,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ushort rectangleCount;
        try
        {
            _ = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            rectangleCount = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.MalformedFramebufferUpdate,
                RfbProtocolReadStage.FramebufferHeader,
                0));
        }

        if (rectangleCount > MaximumRectangleCount)
        {
            throw RfbProtocolException.Create(
                $"FramebufferUpdate rectangle count {rectangleCount} exceeds the limit of {MaximumRectangleCount}.",
                new RfbProtocolFailureInfo(
                    RfbProtocolFailureKind.MalformedFramebufferUpdate,
                    RfbProtocolReadStage.FramebufferHeader,
                    0));
        }

        var dirtyRects = new List<FramebufferRect>(rectangleCount);
        var pixelContentRects = new List<FramebufferRect>(rectangleCount);
        var encodingCounts = new Dictionary<int, int>();
        var transferStatistics = new List<RectangleTransferStatistics>(rectangleCount);
        RemoteCursor? cursor = null;
        var desktopResized = false;
        for (var index = 0; index < rectangleCount; index++)
        {
            ushort x;
            ushort y;
            ushort width;
            ushort height;
            int encodingId;
            try
            {
                x = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
                y = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
                width = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
                height = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
                encodingId = await reader.ReadInt32Async(cancellationToken).ConfigureAwait(false);
            }
            catch (RfbProtocolException exception)
            {
                throw exception.WithContext(new RfbProtocolFailureInfo(
                    RfbProtocolFailureKind.MalformedFramebufferUpdate,
                    RfbProtocolReadStage.FramebufferRectangleHeader,
                    0,
                    RectangleIndex: index));
            }

            encodingCounts.TryGetValue(encodingId, out var encodingCount);
            encodingCounts[encodingId] = checked(encodingCount + 1);

            var encoding = (RfbEncodingType)encodingId;
            if (!decoders.TryGetValue(encodingId, out var decoder))
            {
                throw RfbProtocolException.Create(
                    $"Unsupported RFB encoding ID {encodingId}.",
                    new RfbProtocolFailureInfo(
                        RfbProtocolFailureKind.UnsupportedEncoding,
                        RfbProtocolReadStage.FramebufferRectangleHeader,
                        0,
                        encodingId,
                        index));
            }

            var isArdMetadata = encoding is
                RfbEncodingType.ArdDisplayInfo or
                RfbEncodingType.ArdSessionEncryption or
                RfbEncodingType.ArdDisplayInfo2;
            var isAppleMvsControl = encoding == RfbEncodingType.AppleMvs && x == 0 && y == 0 && width == 0 && height == 0;
            if (encoding != RfbEncodingType.Cursor && !isArdMetadata && !isAppleMvsControl && (width == 0 || height == 0))
            {
                throw RfbProtocolException.Create(
                    $"RFB encoding {encodingId} requires non-zero rectangle dimensions.",
                    new RfbProtocolFailureInfo(
                        RfbProtocolFailureKind.MalformedFramebufferUpdate,
                        RfbProtocolReadStage.FramebufferRectangleHeader,
                        0,
                        encodingId,
                        index));
            }

            var previousWidth = framebuffer.Width;
            var previousHeight = framebuffer.Height;
            var previousCursor = framebuffer.Cursor;
            var rectangle = encoding switch
            {
                RfbEncodingType.Cursor => FramebufferRect.CreateCursorRectangle(x, y, width, height),
                RfbEncodingType.ArdDisplayInfo or
                RfbEncodingType.ArdSessionEncryption or
                RfbEncodingType.ArdDisplayInfo2 =>
                    FramebufferRect.CreateMetadataRectangle(x, y, width, height),
                RfbEncodingType.AppleMvs when isAppleMvsControl =>
                    FramebufferRect.CreateMetadataRectangle(x, y, width, height),
                _ => new FramebufferRect(x, y, width, height),
            };
            if (encoding == RfbEncodingType.AppleMvs && !isAppleMvsControl)
            {
                rectangle.ValidateWithin(framebuffer.Width, framebuffer.Height);
            }
            EncodingDecodeResult decodeResult;
            try
            {
                decodeResult = await decoder.DecodeAsync(reader, framebuffer, rectangle, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RfbProtocolException exception)
            {
                throw exception.WithContext(new RfbProtocolFailureInfo(
                    RfbProtocolFailureKind.DecoderFailure,
                    RfbProtocolReadStage.FramebufferRectanglePayload,
                    0,
                    encodingId,
                    index));
            }
            dirtyRects.AddRange(decodeResult.DirtyRects);
            if (decodeResult.TransferStatistics is { } rectangleTransferStatistics)
            {
                transferStatistics.Add(rectangleTransferStatistics);
            }
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

        return new FramebufferUpdateResult(
            dirtyRects,
            pixelContentRects,
            cursor,
            desktopResized,
            encodingCounts,
            new FramebufferTransferStatistics(transferStatistics));
    }

    private static Dictionary<int, IRfbEncodingDecoder> CreateDecoders(PixelFormat pixelFormat) =>
        new IRfbEncodingDecoder[]
        {
            new RawEncoding(pixelFormat),
            new CopyRectEncoding(),
            new ZlibEncoding(pixelFormat),
            new ZrleEncoding(pixelFormat),
            new DesktopSizeEncoding(),
            new CursorEncoding(pixelFormat),
            new ArdDisplayInfoEncoding(),
            new ArdDisplayInfo2Encoding(),
        }.ToDictionary(decoder => decoder.EncodingId);

    private static Dictionary<int, IRfbEncodingDecoder> CreateOneShotDecoders(PixelFormat pixelFormat) =>
        new IRfbEncodingDecoder[]
        {
            new RawEncoding(pixelFormat),
            new CopyRectEncoding(),
            new SessionRequiredPersistentEncoding(RfbEncodingType.Zlib, "Zlib"),
            new SessionRequiredZrleEncoding(),
            new DesktopSizeEncoding(),
            new CursorEncoding(pixelFormat),
            new ArdDisplayInfoEncoding(),
            new ArdDisplayInfo2Encoding(),
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

    private sealed class SessionRequiredPersistentEncoding(
        RfbEncodingType encodingType,
        string encodingName) : IRfbEncodingDecoder
    {
        public int EncodingId => (int)encodingType;

        public ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            Framebuffer framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<EncodingDecodeResult>(
                new RfbProtocolException(
                    $"{encodingName} requires a persistent per-connection session; use FramebufferUpdateReader.CreateSession."));
    }
}
