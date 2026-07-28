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
        cancellationToken.ThrowIfCancellationRequested();
        var message = EncodeClientCutText(text, _maxUtf8Bytes);
        try
        {
            await _writer.WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
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
        var byteCount = GetNormalizedUtf8Length(text, maxUtf8Bytes);
        var payload = new byte[byteCount];
        EncodeNormalizedUtf8(text, payload);
        return payload;
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
            throw MalformedClipboard(
                "ClientCutText message is truncated.",
                RfbProtocolReadStage.ClipboardHeader);
        }

        ValidateHeader(message[..4], expectedType: 6, "ClientCutText");
        var length = BinaryPrimitives.ReadUInt32BigEndian(message[4..8]);
        var payloadLength = ValidateLength(length, maxUtf8Bytes, RfbProtocolReadStage.ClipboardHeader);
        if (message.Length != payloadLength + 8)
        {
            throw MalformedClipboard(
                $"ClientCutText contains {message.Length - 8} payload bytes; expected {payloadLength}.",
                RfbProtocolReadStage.ClipboardPayload);
        }

        return DecodeText(message[8..], maxUtf8Bytes);
    }

    public static string DecodeText(
        ReadOnlySpan<byte> payload,
        int maxUtf8Bytes = DefaultMaxUtf8Bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);
        ValidateLength(checked((uint)payload.Length), maxUtf8Bytes, RfbProtocolReadStage.ClipboardPayload);
        try
        {
            return NormalizeLineEndings(StrictUtf8.GetString(payload));
        }
        catch (DecoderFallbackException exception)
        {
            throw new RfbProtocolException(
                "Clipboard text is not valid UTF-8.",
                exception,
                new RfbProtocolFailureInfo(
                    RfbProtocolFailureKind.MalformedClipboard,
                    RfbProtocolReadStage.ClipboardPayload));
        }
    }

    public static ValueTask<string> ReadClientCutTextAsync(
        RfbReader reader,
        CancellationToken cancellationToken,
        int maxUtf8Bytes = DefaultMaxUtf8Bytes) =>
        ReadMessageAsync(
            reader,
            expectedType: 6,
            "ClientCutText",
            maxUtf8Bytes,
            serverMessageType: null,
            cancellationToken);

    public static ValueTask<string> ReadServerCutTextAsync(
        RfbReader reader,
        CancellationToken cancellationToken,
        int maxUtf8Bytes = DefaultMaxUtf8Bytes) =>
        ReadMessageAsync(
            reader,
            expectedType: 3,
            "ServerCutText",
            maxUtf8Bytes,
            serverMessageType: 3,
            cancellationToken);

    public static ValueTask<string> ReadServerCutTextBodyAsync(
        RfbReader reader,
        CancellationToken cancellationToken,
        int maxUtf8Bytes = DefaultMaxUtf8Bytes) =>
        ReadMessageBodyAsync(reader, "ServerCutText", maxUtf8Bytes, serverMessageType: 3, cancellationToken);

    private static async ValueTask<string> ReadMessageAsync(
        RfbReader reader,
        byte expectedType,
        string messageName,
        int maxUtf8Bytes,
        byte? serverMessageType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);
        byte type;
        try
        {
            type = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(ClipboardContext(
                RfbProtocolReadStage.ClipboardHeader,
                serverMessageType));
        }

        if (type != expectedType)
        {
            throw MalformedClipboard(
                $"Expected {messageName} message type {expectedType}, received {type}.",
                RfbProtocolReadStage.ClipboardHeader,
                serverMessageType);
        }

        return await ReadMessageBodyAsync(
            reader,
            messageName,
            maxUtf8Bytes,
            serverMessageType,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<string> ReadMessageBodyAsync(
        RfbReader reader,
        string messageName,
        int maxUtf8Bytes,
        byte? serverMessageType,
        CancellationToken cancellationToken)
    {
        byte[] padding;
        try
        {
            padding = await reader.ReadBytesAsync(3, cancellationToken).ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(ClipboardContext(
                RfbProtocolReadStage.ClipboardHeader,
                serverMessageType));
        }

        if (padding[0] != 0 || padding[1] != 0 || padding[2] != 0)
        {
            throw MalformedClipboard(
                $"{messageName} padding bytes must be zero.",
                RfbProtocolReadStage.ClipboardHeader,
                serverMessageType);
        }

        uint length;
        try
        {
            length = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(ClipboardContext(
                RfbProtocolReadStage.ClipboardHeader,
                serverMessageType));
        }

        var payloadLength = ValidateLength(
            length,
            maxUtf8Bytes,
            RfbProtocolReadStage.ClipboardHeader,
            serverMessageType);
        byte[] payload;
        try
        {
            payload = await reader.ReadBytesAsync(payloadLength, cancellationToken).ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(ClipboardContext(
                RfbProtocolReadStage.ClipboardPayload,
                serverMessageType));
        }

        try
        {
            try
            {
                return DecodeText(payload, maxUtf8Bytes);
            }
            catch (RfbProtocolException exception)
            {
                throw exception.WithContext(ClipboardContext(
                    RfbProtocolReadStage.ClipboardPayload,
                    serverMessageType));
            }
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
            throw MalformedClipboard(
                $"Expected {messageName} message type {expectedType}, received {header[0]}.",
                RfbProtocolReadStage.ClipboardHeader);
        }

        if (header[1] != 0 || header[2] != 0 || header[3] != 0)
        {
            throw MalformedClipboard(
                $"{messageName} padding bytes must be zero.",
                RfbProtocolReadStage.ClipboardHeader);
        }
    }

    private static int ValidateLength(
        uint length,
        int maxUtf8Bytes,
        RfbProtocolReadStage readStage,
        byte? serverMessageType = null)
    {
        if (length > int.MaxValue || length > maxUtf8Bytes)
        {
            throw MalformedClipboard(
                $"Clipboard UTF-8 length {length} exceeds the configured limit of {maxUtf8Bytes} bytes.",
                readStage,
                serverMessageType);
        }

        return checked((int)length);
    }

    private static RfbProtocolFailureInfo ClipboardContext(
        RfbProtocolReadStage readStage,
        byte? serverMessageType = null) =>
        new(RfbProtocolFailureKind.MalformedClipboard, readStage, serverMessageType);

    private static RfbProtocolException MalformedClipboard(
        string message,
        RfbProtocolReadStage readStage,
        byte? serverMessageType = null) =>
        RfbProtocolException.Create(message, ClipboardContext(readStage, serverMessageType));

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int GetNormalizedUtf8Length(string text, int maxUtf8Bytes)
    {
        var byteCount = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (value == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                value = '\n';
                index++;
            }

            int encodedLength;
            if (value <= 0x7F)
            {
                encodedLength = 1;
            }
            else if (value <= 0x7FF)
            {
                encodedLength = 2;
            }
            else if (char.IsHighSurrogate(value))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    throw InvalidUtf16();
                }

                encodedLength = 4;
                index++;
            }
            else if (char.IsLowSurrogate(value))
            {
                throw InvalidUtf16();
            }
            else
            {
                encodedLength = 3;
            }

            byteCount = checked(byteCount + encodedLength);
            if (byteCount > maxUtf8Bytes)
            {
                throw new RfbProtocolException(
                    $"Clipboard UTF-8 length exceeds the configured limit of {maxUtf8Bytes} bytes.");
            }
        }

        return byteCount;
    }

    private static void EncodeNormalizedUtf8(string text, Span<byte> destination)
    {
        var offset = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (value == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                value = '\n';
                index++;
            }

            if (value <= 0x7F)
            {
                destination[offset++] = checked((byte)value);
            }
            else if (value <= 0x7FF)
            {
                destination[offset++] = checked((byte)(0xC0 | (value >> 6)));
                destination[offset++] = checked((byte)(0x80 | (value & 0x3F)));
            }
            else if (char.IsHighSurrogate(value))
            {
                var codePoint = char.ConvertToUtf32(value, text[++index]);
                destination[offset++] = checked((byte)(0xF0 | (codePoint >> 18)));
                destination[offset++] = checked((byte)(0x80 | ((codePoint >> 12) & 0x3F)));
                destination[offset++] = checked((byte)(0x80 | ((codePoint >> 6) & 0x3F)));
                destination[offset++] = checked((byte)(0x80 | (codePoint & 0x3F)));
            }
            else
            {
                destination[offset++] = checked((byte)(0xE0 | (value >> 12)));
                destination[offset++] = checked((byte)(0x80 | ((value >> 6) & 0x3F)));
                destination[offset++] = checked((byte)(0x80 | (value & 0x3F)));
            }
        }
    }

    private static RfbProtocolException InvalidUtf16() =>
        new("Clipboard text contains an invalid UTF-16 surrogate sequence.");
}
