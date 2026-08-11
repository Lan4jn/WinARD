using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinARD.ProtocolProbe.RdmCapture;

public sealed record RfbClientDeclarationResult
{
    public RfbClientDeclarationResult(
        CapturedPixelFormat? pixelFormat,
        IReadOnlyList<int> encodings,
        IReadOnlyList<CapturedClientMessage> messages,
        bool reachedFramebufferRequest,
        byte? stoppedAtUnknownMessageType)
    {
        PixelFormat = pixelFormat;
        Encodings = Array.AsReadOnly(encodings.ToArray());
        Messages = Array.AsReadOnly(messages.ToArray());
        ReachedFramebufferRequest = reachedFramebufferRequest;
        StoppedAtUnknownMessageType = stoppedAtUnknownMessageType;
    }

    public CapturedPixelFormat? PixelFormat { get; }

    public IReadOnlyList<int> Encodings { get; }

    public IReadOnlyList<CapturedClientMessage> Messages { get; }

    public bool ReachedFramebufferRequest { get; }

    public byte? StoppedAtUnknownMessageType { get; }
}

public static class RfbClientDeclarationReader
{
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumMessages = 64;
    private const int MaximumEncodings = 4096;

    public static async Task<RfbClientDeclarationResult> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var input = new BoundedDeclarationInput(stream);
        CapturedPixelFormat? pixelFormat = null;
        IReadOnlyList<int> encodings = [];
        var messages = new List<CapturedClientMessage>();
        var messageCount = 0;
        while (true)
        {
            if (messageCount == MaximumMessages)
            {
                throw new InvalidDataException("The RFB client declaration exceeds 64 messages.");
            }

            messageCount++;
            var type = await input.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            switch (type)
            {
                case 0x00:
                    {
                        var payload = await input.ReadAsync(19, cancellationToken).ConfigureAwait(false);
                        pixelFormat = new CapturedPixelFormat(
                            payload[3],
                            payload[4],
                            payload[5] != 0,
                            payload[6] != 0,
                            BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(7)),
                            BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(9)),
                            BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(11)),
                            payload[13],
                            payload[14],
                            payload[15]);
                        messages.Add(new CapturedClientMessage(type, "SetPixelFormat", 20, null, null));
                        break;
                    }

                case 0x02:
                    {
                        var header = await input.ReadAsync(3, cancellationToken).ConfigureAwait(false);
                        var count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
                        if (count > MaximumEncodings)
                        {
                            throw new InvalidDataException("The RFB SetEncodings count exceeds 4096.");
                        }

                        var payload = await input.ReadAsync(checked(count * sizeof(int)), cancellationToken)
                            .ConfigureAwait(false);
                        var capturedEncodings = new int[count];
                        for (var index = 0; index < capturedEncodings.Length; index++)
                        {
                            capturedEncodings[index] = BinaryPrimitives.ReadInt32BigEndian(
                                payload.AsSpan(index * sizeof(int)));
                        }

                        encodings = capturedEncodings;
                        messages.Add(new CapturedClientMessage(
                            type,
                            "SetEncodings",
                            4 + payload.Length,
                            null,
                            null));
                        break;
                    }

                case 0x03:
                    {
                        _ = await input.ReadAsync(9, cancellationToken).ConfigureAwait(false);
                        messages.Add(new CapturedClientMessage(
                            type,
                            "FramebufferUpdateRequest",
                            10,
                            null,
                            null));
                        return new RfbClientDeclarationResult(pixelFormat, encodings, messages, true, null);
                    }

                case 0x09:
                    messages.Add(await ReadNumericMessageAsync(
                        input,
                        type,
                        "AutoFramebufferUpdate",
                        16,
                        cancellationToken).ConfigureAwait(false));
                    break;

                case 0x0A:
                    messages.Add(await ReadNumericMessageAsync(
                        input,
                        type,
                        "SetMode",
                        4,
                        cancellationToken).ConfigureAwait(false));
                    break;

                case 0x0D:
                    messages.Add(await ReadNumericMessageAsync(
                        input,
                        type,
                        "SetDisplay",
                        8,
                        cancellationToken).ConfigureAwait(false));
                    break;

                case 0x12:
                    {
                        var header = await input.ReadAsync(3, cancellationToken).ConfigureAwait(false);
                        var opcode = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
                        var wireLength = opcode switch
                        {
                            1 => 12,
                            2 => 8,
                            _ => throw new InvalidDataException("The RFB SetEncryption opcode is unsupported."),
                        };
                        var remainder = await input.ReadAsync(wireLength - 4, cancellationToken)
                            .ConfigureAwait(false);
                        messages.Add(new CapturedClientMessage(
                            type,
                            "SetEncryption",
                            wireLength,
                            Convert.ToHexString([.. header, .. remainder]),
                            null));
                        break;
                    }

                case 0x21:
                    {
                        var payload = await input.ReadAsync(65, cancellationToken).ConfigureAwait(false);
                        var message = new byte[66];
                        message[0] = type;
                        payload.CopyTo(message, 1);
                        messages.Add(new CapturedClientMessage(
                            type,
                            "ViewerInfo",
                            message.Length,
                            null,
                            Convert.ToHexString(SHA256.HashData(message))));
                        CryptographicOperations.ZeroMemory(message);
                        CryptographicOperations.ZeroMemory(payload);
                        break;
                    }

                default:
                    return new RfbClientDeclarationResult(pixelFormat, encodings, messages, false, type);
            }
        }
    }

    private static async Task<CapturedClientMessage> ReadNumericMessageAsync(
        BoundedDeclarationInput input,
        byte type,
        string name,
        int wireLength,
        CancellationToken cancellationToken)
    {
        var payload = await input.ReadAsync(wireLength - 1, cancellationToken).ConfigureAwait(false);
        return new CapturedClientMessage(type, name, wireLength, Convert.ToHexString(payload), null);
    }

    private sealed class BoundedDeclarationInput(Stream stream)
    {
        private int _bytesRead;

        public async Task<byte> ReadByteAsync(CancellationToken cancellationToken)
        {
            var bytes = await ReadAsync(1, cancellationToken).ConfigureAwait(false);
            return bytes[0];
        }

        public async Task<byte[]> ReadAsync(int length, CancellationToken cancellationToken)
        {
            if (_bytesRead > MaximumBytes - length)
            {
                throw new InvalidDataException("The RFB client declaration exceeds 65536 bytes.");
            }

            var payload = new byte[length];
            var offset = 0;
            while (offset < payload.Length)
            {
                int count;
                try
                {
                    count = await stream.ReadAsync(payload.AsMemory(offset), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        "The RFB client declaration was cancelled.",
                        cancellationToken);
                }

                if (count == 0)
                {
                    throw new InvalidDataException("The RFB client declaration was truncated.");
                }

                offset += count;
                _bytesRead += count;
            }

            return payload;
        }
    }
}
