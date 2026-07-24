using System.Buffers.Binary;

namespace WinARD.Remote.Protocol.IO;

public sealed class RfbWriter
{
    private readonly Stream _stream;

    public RfbWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    public ValueTask WriteByteAsync(byte value, CancellationToken cancellationToken) =>
        WriteBytesAsync(new byte[] { value }, cancellationToken);

    public ValueTask WriteUInt16Async(ushort value, CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return WriteBytesAsync(bytes, cancellationToken);
    }

    public ValueTask WriteUInt32Async(uint value, CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return WriteBytesAsync(bytes, cancellationToken);
    }

    public ValueTask WriteBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
        _stream.WriteAsync(value, cancellationToken);
}
