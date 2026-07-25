using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Clipboard;

public sealed class ClipboardProtocol
{
    public const int DefaultMaxUtf8Bytes = 1_048_576;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly RfbWriter _writer;
    private readonly int _maxUtf8Bytes;

    public ClipboardProtocol(RfbWriter writer, int maxUtf8Bytes = DefaultMaxUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);
        _writer = writer;
        _maxUtf8Bytes = maxUtf8Bytes;
    }

    public async ValueTask WriteClientCutTextAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var message = EncodeClientCutText(text, _maxUtf8Bytes);
        try
        {
            await _writer.WriteBytesAsync(message, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
        }
    }

    public static byte[] EncodeText(
        string text,
        int maxUtf8Bytes = DefaultMaxUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);
        var normalized = NormalizeLineEndings(text);
        var byteCount = StrictUtf8.GetByteCount(normalized);
        ValidateLength(checked((uint)byteCount), maxUtf8Bytes);
        return StrictUtf8.GetBytes(normalized);
    }

    public static byte[] EncodeClientCutText(
        string text,
        int maxUtf8Bytes = DefaultMaxUtf8Bytes)
    {
        var payload = EncodeText(text, maxUtf8Bytes);
        try
        {
            var message = new byte[checked(payload.Length + 8)];
            message[0] = 6;
            BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), checked((uint)payload.Length));
            payload.CopyTo(message, 8);
            return message;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static string DecodeClientCutText(
        ReadOnlySpan<byte> message,
        int maxUtf8Bytes = DefaultMaxUtf8Bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);
        if (message.Length < 8)
        {
            throw new RfbProtocolException("ClientCutText message is truncated.");
        }

        ValidateHeader(message[..4], expectedType: 6, "ClientCutText");
        var length = BinaryPrimitives.ReadUInt32BigEndian(message[4..8]);
        var payloadLength = ValidateLength(length, maxUtf8Bytes);
        if (message.Length != payloadLength + 8)
        {
            throw new RfbProtocolException(
                $"ClientCutText contains {message.Length - 8} payload bytes; expected {payloadLength}.");
        }

        return DecodeText(message[8..], maxUtf8Bytes);
    }

    public static string DecodeText(
        ReadOnlySpan<byte> payload,
        int maxUtf8Bytes = DefaultMaxUtf8Bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);
        ValidateLength(checked((uint)payload.Length), maxUtf8Bytes);
        try
        {
            return NormalizeLineEndings(StrictUtf8.GetString(payload));
        }
        catch (DecoderFallbackException exception)
        {
            throw new RfbProtocolException("Clipboard text is not valid UTF-8.", exception);
        }
    }

    public static ValueTask<string> ReadClientCutTextAsync(
        RfbReader reader,
        CancellationToken cancellationToken,
        int maxUtf8Bytes = DefaultMaxUtf8Bytes) =>
        ReadMessageAsync(reader, expectedType: 6, "ClientCutText", maxUtf8Bytes, cancellationToken);

    public static ValueTask<string> ReadServerCutTextAsync(
        RfbReader reader,
        CancellationToken cancellationToken,
        int maxUtf8Bytes = DefaultMaxUtf8Bytes) =>
        ReadMessageAsync(reader, expectedType: 3, "ServerCutText", maxUtf8Bytes, cancellationToken);

    private static async ValueTask<string> ReadMessageAsync(
        RfbReader reader,
        byte expectedType,
        string messageName,
        int maxUtf8Bytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);
        var type = await reader.ReadByteAsync(cancellationToken);
        var padding = await reader.ReadBytesAsync(3, cancellationToken);
        ValidateHeader([type, .. padding], expectedType, messageName);
        var length = await reader.ReadUInt32Async(cancellationToken);
        var payloadLength = ValidateLength(length, maxUtf8Bytes);
        var payload = await reader.ReadBytesAsync(payloadLength, cancellationToken);
        try
        {
            return DecodeText(payload, maxUtf8Bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void ValidateHeader(
        ReadOnlySpan<byte> header,
        byte expectedType,
        string messageName)
    {
        if (header[0] != expectedType)
        {
            throw new RfbProtocolException(
                $"Expected {messageName} message type {expectedType}, received {header[0]}.");
        }

        if (header[1] != 0 || header[2] != 0 || header[3] != 0)
        {
            throw new RfbProtocolException($"{messageName} padding bytes must be zero.");
        }
    }

    private static int ValidateLength(uint length, int maxUtf8Bytes)
    {
        if (length > int.MaxValue || length > maxUtf8Bytes)
        {
            throw new RfbProtocolException(
                $"Clipboard UTF-8 length {length} exceeds the configured limit of {maxUtf8Bytes} bytes.");
        }

        return checked((int)length);
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
