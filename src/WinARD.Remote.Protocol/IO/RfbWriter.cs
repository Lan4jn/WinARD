using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using WinARD.Remote.Protocol.Errors;

namespace WinARD.Remote.Protocol.IO;

public sealed class RfbWriter
{
    private static readonly ConditionalWeakTable<Stream, SharedWriteState> SharedStates = new();

    private readonly Stream _stream;
    private readonly SharedWriteState _state;

    public RfbWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _state = SharedStates.GetValue(stream, static _ => new SharedWriteState());
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

    public ValueTask WriteInt32Async(int value, CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return WriteBytesAsync(bytes, cancellationToken);
    }

    public async ValueTask WriteBytesAsync(
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        _state.ThrowIfFaulted();
        var entered = false;
        try
        {
            await _state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            _state.ThrowIfFaulted();
            await _stream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _state.Fault();
            throw;
        }
        finally
        {
            if (entered)
            {
                _state.Gate.Release();
            }
        }
    }

    private sealed class SharedWriteState
    {
        private readonly object _sync = new();
        private bool _faulted;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public void Fault()
        {
            lock (_sync)
            {
                _faulted = true;
            }
        }

        public void ThrowIfFaulted()
        {
            lock (_sync)
            {
                if (_faulted)
                {
                    throw new RfbProtocolException(
                        "The outbound RFB connection writer is faulted; discard the connection.");
                }
            }
        }
    }
}
