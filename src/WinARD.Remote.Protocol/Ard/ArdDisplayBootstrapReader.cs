using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Ard;

public readonly record struct ArdDisplaySize(ushort Width, ushort Height);

public static class ArdDisplayBootstrapReader
{
    private const byte FramebufferUpdateMessageType = 0x00;
    private const int MaximumTopLevelMessages = 64;
    private const int MaximumRectanglesPerFramebufferUpdate = 4096;
    private const int FramebufferUpdateHeaderBytes = 4;
    private const int RectangleHeaderBytes = 12;

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
                if (ArdServerMessage.IsZeroPayloadControl(messageType))
                {
                    continue;
                }

                switch (messageType)
                {
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
        if (rectangleCount > MaximumRectanglesPerFramebufferUpdate)
        {
            throw new ArdSessionMalformedException();
        }

        budget.Reserve(checked((int)rectangleCount * RectangleHeaderBytes));

        ArdDisplaySize? firstNonzeroSize = null;
        for (var rectangleIndex = 0; rectangleIndex < rectangleCount; rectangleIndex++)
        {
            var rectangleX = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var rectangleY = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var rectangleWidth = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var rectangleHeight = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var encoding = await reader.ReadInt32Async(cancellationToken).ConfigureAwait(false);

            ArdDisplaySize candidate;
            switch ((RfbEncodingType)encoding)
            {
                case RfbEncodingType.DesktopSize:
                    if (rectangleX != 0 || rectangleY != 0)
                    {
                        throw new ArdSessionMalformedException();
                    }

                    candidate = new ArdDisplaySize(rectangleWidth, rectangleHeight);
                    break;
                case RfbEncodingType.ArdDisplayInfo:
                    candidate = await ArdDisplayMetadataReader.ReadDisplayInfoAsync(
                            reader,
                            budget.Reserve,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case RfbEncodingType.ArdDisplayInfo2:
                    await ArdDisplayMetadataReader.ReadDisplayInfo2Async(
                            reader,
                            budget.Reserve,
                            cancellationToken)
                        .ConfigureAwait(false);
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
