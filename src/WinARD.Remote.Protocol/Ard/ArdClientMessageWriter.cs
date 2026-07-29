using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Ard;

public sealed class ArdClientMessageWriter
{
    private const int ViewerInfoLength = 66;
    private const int SessionCommandLength = 74;
    private const int SessionCommandUsernameOffset = 10;
    private const int SessionCommandUsernameLength = 63;
    private const int AutoFramebufferUpdateLength = 16;

    private readonly RfbWriter _writer;

    public ArdClientMessageWriter(RfbWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public ValueTask WriteAutoFramebufferUpdateAsync(
        ushort width,
        ushort height,
        CancellationToken cancellationToken)
    {
        if (width == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var message = new byte[AutoFramebufferUpdateLength];
        message[0] = ArdProtocolConstants.AutoFramebufferUpdate;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(12), width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(14), height);
        return _writer.WriteMessageAsync(message, cancellationToken);
    }

    public ValueTask WriteViewerInfoAsync(CancellationToken cancellationToken)
    {
        var message = new byte[ViewerInfoLength];
        message[0] = ArdProtocolConstants.ViewerInfo;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 62);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(10), 6);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(18), 0);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(22), 15);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(26), 0);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(30), 0);
        message[34] = 0xB0;
        message[36] = 0x0C;
        message[37] = 0x03;
        message[38] = 0x90;
        message[44] = 0x40;
        return _writer.WriteMessageAsync(message, cancellationToken);
    }

    public ValueTask WriteSetModeAsync(ArdControlMode mode, CancellationToken cancellationToken)
    {
        if (mode is not ArdControlMode.Observe and not ArdControlMode.Shared and not ArdControlMode.Exclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return _writer.WriteMessageAsync(
            new byte[] { ArdProtocolConstants.SetMode, 0, 0, (byte)mode },
            cancellationToken);
    }

    public ValueTask WriteSetDisplayAsync(CancellationToken cancellationToken) =>
        _writer.WriteMessageAsync(
            new byte[] { ArdProtocolConstants.SetDisplay, 1, 0, 0, 0, 0, 0, 0 },
            cancellationToken);

    public ValueTask WriteSessionCommandAsync(
        byte command,
        string username,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(username);
        if (command > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        var message = new byte[SessionCommandLength];
        BinaryPrimitives.WriteUInt16BigEndian(message, 72);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 1);
        message[8] = command;
        WriteUsername(message.AsSpan(SessionCommandUsernameOffset, SessionCommandUsernameLength), username);
        return _writer.WriteMessageAsync(message, cancellationToken);
    }

    private static void WriteUsername(Span<byte> destination, string username)
    {
        var bytesWritten = 0;
        var source = username.AsSpan();
        var truncated = false;
        while (!source.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(source, out var rune, out var charsConsumed);
            if (status != OperationStatus.Done)
            {
                throw new ArgumentException("The username must contain valid UTF-16 text.", nameof(username));
            }

            if (rune.Value == 0)
            {
                throw new ArgumentException("The username must not contain NUL characters.", nameof(username));
            }

            var encodedLength = rune.Utf8SequenceLength;
            if (!truncated && bytesWritten + encodedLength <= destination.Length)
            {
                rune.EncodeToUtf8(destination[bytesWritten..]);
                bytesWritten += encodedLength;
            }
            else
            {
                truncated = true;
            }

            source = source[charsConsumed..];
        }
    }
}
