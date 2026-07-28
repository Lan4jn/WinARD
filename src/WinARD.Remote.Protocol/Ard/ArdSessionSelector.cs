using System.Buffers.Binary;
using System.Text;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Ard;

public static class ArdSessionSelector
{
    private const ushort SessionProtocolVersion = 1;
    private const byte RequestConsoleCommand = 0;
    private const byte ConnectToConsoleCommand = 1;
    private const uint GrantedStatus = 0;
    private const uint PendingStatus = 2;
    private const uint PendingAlternateStatus = 3;
    private const uint GrantedAfterPendingStatus = 4;
    private const int MaximumPendingResults = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task SelectConsoleAsync(
        Stream stream,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();

        var reader = new RfbReader(stream, limits);
        var sessionInfo = await ReadSessionInfoAsync(reader, limits, cancellationToken).ConfigureAwait(false);
        var command = SelectCommand(sessionInfo.AllowedCommands);
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));
        await writer.WriteSessionCommandAsync(command, sessionInfo.Username, cancellationToken).ConfigureAwait(false);

        var pendingResults = 0;
        while (true)
        {
            var status = await ReadSessionResultAsync(reader, limits, cancellationToken).ConfigureAwait(false);
            switch (status)
            {
                case GrantedStatus:
                case GrantedAfterPendingStatus:
                    return;
                case PendingStatus:
                case PendingAlternateStatus:
                    if (pendingResults == MaximumPendingResults)
                    {
                        throw new ArdSessionMalformedException();
                    }

                    pendingResults++;
                    break;
                default:
                    throw new ArdSessionDeniedException(status);
            }
        }
    }

    private static async ValueTask<SessionInfo> ReadSessionInfoAsync(
        RfbReader reader,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            var bodySize = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            ValidateBodySize(bodySize, 10, limits);
            var body = await reader.ReadBytesAsync(bodySize, cancellationToken).ConfigureAwait(false);
            return ParseSessionInfo(body);
        }
        catch (RfbProtocolException)
        {
            throw new ArdSessionMalformedException();
        }
    }

    private static async ValueTask<uint> ReadSessionResultAsync(
        RfbReader reader,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            var bodySize = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            ValidateBodySize(bodySize, 6, limits);
            var body = await reader.ReadBytesAsync(bodySize, cancellationToken).ConfigureAwait(false);
            return ParseSessionResult(body);
        }
        catch (RfbProtocolException)
        {
            throw new ArdSessionMalformedException();
        }
    }

    private static SessionInfo ParseSessionInfo(ReadOnlySpan<byte> body)
    {
        if (BinaryPrimitives.ReadUInt16BigEndian(body) != SessionProtocolVersion)
        {
            throw new ArdSessionMalformedException();
        }

        var allowedCommands = BinaryPrimitives.ReadUInt32BigEndian(body[2..]);
        var usernameBytes = body[10..];
        var nulIndex = usernameBytes.IndexOf((byte)0);
        if (nulIndex >= 0)
        {
            usernameBytes = usernameBytes[..nulIndex];
        }

        try
        {
            return new SessionInfo(allowedCommands, StrictUtf8.GetString(usernameBytes));
        }
        catch (DecoderFallbackException)
        {
            throw new ArdSessionMalformedException();
        }
    }

    private static uint ParseSessionResult(ReadOnlySpan<byte> body)
    {
        if (BinaryPrimitives.ReadUInt16BigEndian(body) != SessionProtocolVersion)
        {
            throw new ArdSessionMalformedException();
        }

        return BinaryPrimitives.ReadUInt32BigEndian(body[2..]);
    }

    private static void ValidateBodySize(ushort bodySize, int minimumBodySize, ProtocolLimits limits)
    {
        if (bodySize < minimumBodySize || bodySize > limits.MaxMessageBytes)
        {
            throw new ArdSessionMalformedException();
        }
    }

    private static byte SelectCommand(uint allowedCommands)
    {
        if ((allowedCommands & (1u << ConnectToConsoleCommand)) != 0)
        {
            return ConnectToConsoleCommand;
        }

        if ((allowedCommands & (1u << RequestConsoleCommand)) != 0)
        {
            return RequestConsoleCommand;
        }

        throw new ArdSessionCommandUnavailableException();
    }

    private readonly record struct SessionInfo(uint AllowedCommands, string Username);
}
