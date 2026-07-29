using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Ard;

public static class ArdStateChangeReader
{
    private const int HeaderLength = 3;
    private const int FixedPayloadLength = 4;

    public static async ValueTask<ArdStateChange> ReadBodyAsync(
        RfbReader reader,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();

        byte padding;
        ushort payloadSize;
        try
        {
            padding = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            payloadSize = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(Context(RfbProtocolReadStage.ArdStateChangeHeader));
        }

        if (payloadSize < FixedPayloadLength || HeaderLength + payloadSize > limits.MaxMessageBytes)
        {
            throw RfbProtocolException.Create(
                $"ARD StateChange payload length {payloadSize} is invalid.",
                Context(RfbProtocolReadStage.ArdStateChangeHeader));
        }

        ushort flags;
        ushort status;
        try
        {
            flags = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            status = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadBytesAsync(payloadSize - FixedPayloadLength, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(Context(RfbProtocolReadStage.ArdStateChangePayload));
        }

        return new ArdStateChange(
            padding,
            payloadSize,
            flags,
            status,
            payloadSize - FixedPayloadLength);
    }

    private static RfbProtocolFailureInfo Context(RfbProtocolReadStage readStage) =>
        new(
            RfbProtocolFailureKind.MalformedArdStateChange,
            readStage,
            ArdProtocolConstants.StateChange);
}
