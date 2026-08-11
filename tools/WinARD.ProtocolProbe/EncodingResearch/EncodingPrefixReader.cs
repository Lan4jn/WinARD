using System.Security.Cryptography;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.ProtocolProbe.EncodingResearch;

public static class EncodingPrefixReader
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumEmptyUpdates = 8;
    private const int MaximumRectangleCount = 4096;
    private const int MaximumAllowedPrefixLength = 64 * 1024;
    private const string EmptyPayloadMessage =
        "The candidate encoding rectangle contained no observable payload prefix.";

    public static Task<EncodingPrefixCapture> ReadAsync(
        Stream stream,
        int candidateEncodingId,
        int maximumPrefixLength,
        CancellationToken cancellationToken) =>
        ReadCoreAsync(
            stream,
            candidateEncodingId,
            maximumPrefixLength,
            framebufferWidth: null,
            framebufferHeight: null,
            requestNextUpdate: null,
            cancellationToken);

    internal static Task<EncodingPrefixCapture> ReadAsync(
        Stream stream,
        int candidateEncodingId,
        int maximumPrefixLength,
        Func<CancellationToken, Task> requestNextUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestNextUpdate);
        return ReadCoreAsync(
            stream,
            candidateEncodingId,
            maximumPrefixLength,
            framebufferWidth: null,
            framebufferHeight: null,
            requestNextUpdate,
            cancellationToken);
    }

    internal static Task<EncodingPrefixCapture> ReadAsync(
        Stream stream,
        int candidateEncodingId,
        int maximumPrefixLength,
        ushort framebufferWidth,
        ushort framebufferHeight,
        Func<CancellationToken, Task> requestNextUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestNextUpdate);
        return ReadCoreAsync(
            stream,
            candidateEncodingId,
            maximumPrefixLength,
            framebufferWidth,
            framebufferHeight,
            requestNextUpdate,
            cancellationToken);
    }

    private static async Task<EncodingPrefixCapture> ReadCoreAsync(
        Stream stream,
        int candidateEncodingId,
        int maximumPrefixLength,
        ushort? framebufferWidth,
        ushort? framebufferHeight,
        Func<CancellationToken, Task>? requestNextUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumPrefixLength is < 1 or > MaximumAllowedPrefixLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPrefixLength),
                $"The payload prefix limit must be between 1 and {MaximumAllowedPrefixLength} bytes.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var reader = new RfbReader(stream, ProtocolLimits.Default);
        for (var updateIndex = 0; updateIndex < MaximumEmptyUpdates; updateIndex++)
        {
            byte messageType;
            do
            {
                messageType = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            }
            while (ArdServerMessage.IsZeroPayloadControl(messageType));

            if (messageType != 0)
            {
                throw new RfbProtocolException(
                    $"Expected FramebufferUpdate message type 0, received {messageType}.");
            }

            _ = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            var rectangleCount = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            if (rectangleCount > MaximumRectangleCount)
            {
                throw new RfbProtocolException(
                    $"FramebufferUpdate rectangle count {rectangleCount} exceeds the limit of {MaximumRectangleCount}.");
            }

            if (rectangleCount == 0)
            {
                if (updateIndex + 1 < MaximumEmptyUpdates && requestNextUpdate is not null)
                {
                    await requestNextUpdate(cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var rectangle = new CapturedRectangle(
                await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false),
                await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false),
                await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false),
                await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false));
            var encodingId = await reader.ReadInt32Async(cancellationToken).ConfigureAwait(false);
            if (encodingId != candidateEncodingId)
            {
                throw new EncodingCandidateNotObservedException(encodingId);
            }

            if (rectangle.Width == 0
                || rectangle.Height == 0
                || (framebufferWidth is { } width
                    && checked((uint)rectangle.X + rectangle.Width) > width)
                || (framebufferHeight is { } height
                    && checked((uint)rectangle.Y + rectangle.Height) > height))
            {
                throw new RfbProtocolException(
                    "The candidate rectangle must have non-zero dimensions within the framebuffer.");
            }

            var payloadLength = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
            var requestedLength = (int)Math.Min(payloadLength, (uint)maximumPrefixLength);
            if (requestedLength == 0)
            {
                throw new EndOfStreamException(EmptyPayloadMessage);
            }

            var prefix = new byte[requestedLength];
            var count = await stream.ReadAsync(prefix, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException(EmptyPayloadMessage);
            }

            Array.Resize(ref prefix, count);
            return new EncodingPrefixCapture(
                CurrentSchemaVersion,
                encodingId,
                rectangle,
                prefix.Length,
                Convert.ToHexString(SHA256.HashData(prefix)),
                prefix,
                payloadLength);
        }

        throw new RfbProtocolException(
            $"The server returned {MaximumEmptyUpdates} empty framebuffer updates without a candidate rectangle.");
    }
}

public sealed class EncodingCandidateNotObservedException : IOException
{
    public EncodingCandidateNotObservedException(int observedEncodingId)
        : base($"candidate-not-observed; observed-encoding={observedEncodingId}.")
    {
        ObservedEncodingId = observedEncodingId;
        Category = "candidate-not-observed";
    }

    public string Category { get; }

    public int ObservedEncodingId { get; }
}
