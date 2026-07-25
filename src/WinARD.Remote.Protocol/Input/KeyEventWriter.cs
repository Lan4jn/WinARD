using System.Buffers.Binary;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Input;

public sealed class KeyEventWriter
{
    private readonly RfbWriter _writer;

    public KeyEventWriter(RfbWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public ValueTask WriteAsync(
        bool isDown,
        uint keysym,
        CancellationToken cancellationToken)
    {
        var message = new byte[8];
        message[0] = 4;
        message[1] = isDown ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), keysym);
        return _writer.WriteBytesAsync(message, cancellationToken);
    }
}
