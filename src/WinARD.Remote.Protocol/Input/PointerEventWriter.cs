using System.Buffers.Binary;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Input;

public sealed class PointerEventWriter
{
    private readonly RfbWriter _writer;

    public PointerEventWriter(RfbWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public ValueTask WriteAsync(
        byte buttons,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(x, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(y, ushort.MaxValue);

        var message = new byte[6];
        message[0] = 5;
        message[1] = buttons;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), checked((ushort)x));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), checked((ushort)y));
        return _writer.WriteBytesAsync(message, cancellationToken);
    }
}
