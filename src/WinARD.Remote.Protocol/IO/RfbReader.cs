using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;

namespace WinARD.Remote.Protocol.IO;

public sealed class RfbReader
{
    private readonly Stream _stream;
    private readonly ProtocolLimits _limits;
    private int _framebufferUpdateBytes;

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

    public async ValueTask<int> ReadInt32Async(CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(sizeof(int), cancellationToken);
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
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

    internal void ReserveFramebufferUpdateBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var total = checked((long)_framebufferUpdateBytes + count);
        if (total > _limits.MaxFramebufferUpdateBytes)
        {
            throw new RfbProtocolException(
                $"Framebuffer update payload length {total} exceeds the configured limit of " +
                $"{_limits.MaxFramebufferUpdateBytes} bytes.");
        }

        _framebufferUpdateBytes = checked((int)total);
    }

    internal async ValueTask<byte[]> ReadFramebufferPayloadBytesAsync(
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
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
