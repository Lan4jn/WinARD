using System.Security.Cryptography;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Ard;

public readonly record struct ArdDisplaySize(ushort Width, ushort Height);

public static class ArdDisplayBootstrapReader
{
    private const byte FramebufferUpdateMessageType = 0x00;
    private const byte AckMessageType = 0x04;
    private const byte NopMessageType = 0x07;
    private const int MaximumTopLevelMessages = 64;
    private const int FramebufferUpdateHeaderBytes = 4;
    private const int RectangleHeaderBytes = 12;
    private const int DisplayInfoHeaderBytes = 8;
    private const int DisplayInfoRecordBytes = 28;
    private const int DisplayInfo2SizeBytes = 2;
    private const int SkipBufferBytes = 4096;

    public static async Task<ArdDisplaySize> ReadAsync(
        Stream stream,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();

        var reader = new RfbReader(stream, limits);

        try
        {
            for (var messageIndex = 0; messageIndex < MaximumTopLevelMessages; messageIndex++)
            {
                var messageType = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                switch (messageType)
                {
                    case AckMessageType:
                    case NopMessageType:
                        continue;
                    case FramebufferUpdateMessageType:
                    {
                        var size = await ReadFramebufferUpdateAsync(reader, limits, cancellationToken)
                            .ConfigureAwait(false);
                        if (size.HasValue)
                        {
                            return size.Value;
                        }

                        break;
                    }
                    default:
                        throw new ArdSessionMalformedException();
                }
            }
        }
        catch (RfbProtocolException)
        {
            throw new ArdSessionMalformedException();
        }
        catch (OverflowException)
        {
            throw new ArdSessionMalformedException();
        }

        throw new ArdSessionMalformedException();
    }

    private static async ValueTask<ArdDisplaySize?> ReadFramebufferUpdateAsync(
        RfbReader reader,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        var budget = new ReadBudget(Math.Min(limits.MaxMessageBytes, limits.MaxFramebufferUpdateBytes));
        budget.Reserve(FramebufferUpdateHeaderBytes);

        _ = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        var rectangleCount = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        budget.Reserve(checked((int)rectangleCount * RectangleHeaderBytes));

        ArdDisplaySize? firstNonzeroSize = null;
        for (var rectangleIndex = 0; rectangleIndex < rectangleCount; rectangleIndex++)
        {
            _ = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var rectangleWidth = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var rectangleHeight = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var encoding = await reader.ReadInt32Async(cancellationToken).ConfigureAwait(false);

            ArdDisplaySize candidate;
            switch ((RfbEncodingType)encoding)
            {
                case RfbEncodingType.DesktopSize:
                    candidate = new ArdDisplaySize(rectangleWidth, rectangleHeight);
                    break;
                case RfbEncodingType.ArdDisplayInfo:
                    candidate = await ReadDisplayInfoAsync(reader, budget, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case RfbEncodingType.ArdDisplayInfo2:
                    await ReadDisplayInfo2Async(reader, budget, cancellationToken).ConfigureAwait(false);
                    candidate = new ArdDisplaySize(rectangleWidth, rectangleHeight);
                    break;
                default:
                    throw new ArdSessionMalformedException();
            }

            if (!firstNonzeroSize.HasValue && candidate.Width != 0 && candidate.Height != 0)
            {
                firstNonzeroSize = candidate;
            }
        }

        return firstNonzeroSize;
    }

    private static async ValueTask<ArdDisplaySize> ReadDisplayInfoAsync(
        RfbReader reader,
        ReadBudget budget,
        CancellationToken cancellationToken)
    {
        budget.Reserve(DisplayInfoHeaderBytes);
        var width = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        var height = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        var displayCount = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);

        var displayBytes = checked((int)displayCount * DisplayInfoRecordBytes);
        budget.Reserve(displayBytes);
        await SkipAsync(reader, displayBytes, cancellationToken).ConfigureAwait(false);
        return new ArdDisplaySize(width, height);
    }

    private static async ValueTask ReadDisplayInfo2Async(
        RfbReader reader,
        ReadBudget budget,
        CancellationToken cancellationToken)
    {
        budget.Reserve(DisplayInfo2SizeBytes);
        var payloadSize = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        budget.Reserve(payloadSize);
        await SkipAsync(reader, payloadSize, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SkipAsync(
        RfbReader reader,
        int byteCount,
        CancellationToken cancellationToken)
    {
        var remaining = byteCount;
        while (remaining > 0)
        {
            var chunkLength = Math.Min(remaining, SkipBufferBytes);
            var chunk = await reader.ReadBytesAsync(chunkLength, cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(chunk);
            remaining -= chunkLength;
        }
    }

    private sealed class ReadBudget
    {
        private readonly int _maximumBytes;
        private int _reservedBytes;

        public ReadBudget(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
        }

        public void Reserve(int byteCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
            var total = checked(_reservedBytes + byteCount);
            if (total > _maximumBytes)
            {
                throw new ArdSessionMalformedException();
            }

            _reservedBytes = total;
        }
    }
}
