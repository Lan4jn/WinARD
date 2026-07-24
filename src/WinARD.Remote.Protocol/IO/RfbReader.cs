using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;

namespace WinARD.Remote.Protocol.IO;

public sealed class RfbReader
{
    private readonly Stream _stream;
    private readonly ProtocolLimits _limits;

    public RfbReader(Stream stream, ProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(limits);

        _stream = stream;
        _limits = limits;
    }

    public async ValueTask<byte> ReadByteAsync(CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(1, cancellationToken);
        return bytes[0];
    }

    public async ValueTask<ushort> ReadUInt16Async(CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(sizeof(ushort), cancellationToken);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    public async ValueTask<uint> ReadUInt32Async(CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(sizeof(uint), cancellationToken);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    public async ValueTask<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count > _limits.MaxMessageBytes)
        {
            throw new RfbProtocolException(
                $"Requested message length {count} exceeds the configured limit of {_limits.MaxMessageBytes} bytes.");
        }

        if (count == 0)
        {
            return Array.Empty<byte>();
        }

        return await ReadBytesExactlyAsync(count, cancellationToken);
    }

    private async ValueTask<byte[]> ReadBytesExactlyAsync(int expectedByteCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var buffer = new byte[expectedByteCount];
        var read = 0;

        try
        {
            while (read < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var received = await _stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
                if (received == 0)
                {
                    throw new EndOfStreamException($"Expected {expectedByteCount} bytes but the stream ended after {read} bytes.");
                }

                read += received;
            }
        }
        catch (EndOfStreamException exception)
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new RfbProtocolException(
                $"Unexpected end of stream while reading {expectedByteCount} bytes.",
                exception);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw;
        }

        return buffer;
    }
}
