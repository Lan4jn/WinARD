using System.IO.Compression;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;

namespace WinARD.Remote.Protocol.Encodings;

internal sealed class PersistentZlibInflater : IAsyncDisposable
{
    private readonly string _encodingName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private SegmentedReadStream? _compressedInput;
    private ZLibStream? _zlib;
    private InflaterState _state = InflaterState.Active;
    private Task? _disposeTask;

    public PersistentZlibInflater(string encodingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodingName);
        _encodingName = encodingName;
        InitializeContext();
    }

    public async ValueTask<TResult> RunAsync<TResult>(
        Func<Operation, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ThrowIfUnavailable();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            var operation = new Operation(this);
            try
            {
                return await callback(operation, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (operation.ContextTouched)
                {
                    FaultContext();
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            DisposeContext();
            InitializeContext();
            lock (_stateLock)
            {
                _state = InflaterState.Active;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposeTask is null)
            {
                _state = InflaterState.Disposing;
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeContext();
            lock (_stateLock)
            {
                _state = InflaterState.Disposed;
            }
        }
        catch
        {
            lock (_stateLock)
            {
                _state = InflaterState.Faulted;
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void InitializeContext()
    {
        _compressedInput = new SegmentedReadStream(_encodingName);
        _zlib = new ZLibStream(_compressedInput, CompressionMode.Decompress, leaveOpen: true);
    }

    private void DisposeContext()
    {
        _zlib?.Dispose();
        _compressedInput?.Dispose();
        _zlib = null;
        _compressedInput = null;
    }

    private void FaultContext()
    {
        DisposeContext();
        lock (_stateLock)
        {
            if (_state == InflaterState.Active)
            {
                _state = InflaterState.Faulted;
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        lock (_stateLock)
        {
            switch (_state)
            {
                case InflaterState.Active:
                    return;
                case InflaterState.Faulted:
                    throw new RfbProtocolException(
                        $"The {_encodingName} decompression context is faulted and must be reset for a new connection.");
                case InflaterState.Disposing:
                case InflaterState.Disposed:
                    throw new ObjectDisposedException(_encodingName);
                default:
                    throw new InvalidOperationException($"Unknown {_encodingName} inflater state.");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, _encodingName);
        }
    }

    private async Task<DecompressedChunk> DecompressChunkAsync(
        byte[] compressed,
        int maximumOutputLength,
        int configuredLimit,
        RfbDecoderFailureReason overflowReason,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateSyncFlushBoundary(compressed);
            _compressedInput!.SetSegment(compressed);
            var output = new byte[maximumOutputLength];
            var length = 0;
            try
            {
                while (true)
                {
                    if (length == output.Length)
                    {
                        var overflow = new byte[1];
                        var overflowRead = await _zlib!.ReadAsync(overflow, cancellationToken)
                            .ConfigureAwait(false);
                        CryptographicOperations.ZeroMemory(overflow);
                        if (overflowRead != 0)
                        {
                            throw DecoderFailure(
                                $"{_encodingName} decompressed data exceeds the allowed output length of " +
                                $"{maximumOutputLength} bytes (configured limit {configuredLimit} bytes).",
                                overflowReason);
                        }

                        break;
                    }

                    var read = await _zlib!.ReadAsync(output.AsMemory(length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    length = checked(length + read);
                }

                if (_compressedInput.Position != compressed.Length)
                {
                    throw DecoderFailure(
                        $"{_encodingName} compressed data contains a completed zlib stream or trailing bytes before its Z_SYNC_FLUSH boundary.",
                        RfbDecoderFailureReason.CompletedStreamOrTrailingBytes);
                }

                _compressedInput.ReleaseSegment();
                return new DecompressedChunk(output, length);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(output);
                throw;
            }
        }
        catch (InvalidDataException exception)
        {
            throw new RfbProtocolException(
                $"{_encodingName} payload is not a valid zlib stream.",
                exception,
                DecoderFailureInfo(RfbDecoderFailureReason.InvalidCompressedStream));
        }
    }

    private void ValidateSyncFlushBoundary(ReadOnlySpan<byte> compressed)
    {
        var hasSyncFlushBoundary =
            compressed.Length >= 4 &&
            compressed[^4] == 0 &&
            compressed[^3] == 0 &&
            compressed[^2] == byte.MaxValue &&
            compressed[^1] == byte.MaxValue;
        if (!hasSyncFlushBoundary)
        {
            throw DecoderFailure(
                $"{_encodingName} compressed data must end at a Z_SYNC_FLUSH boundary.",
                RfbDecoderFailureReason.MissingSyncFlushBoundary);
        }
    }

    private static RfbProtocolFailureInfo DecoderFailureInfo(RfbDecoderFailureReason reason) =>
        new(RfbProtocolFailureKind.DecoderFailure, DecoderFailureReason: reason);

    private static RfbProtocolException DecoderFailure(string message, RfbDecoderFailureReason reason) =>
        RfbProtocolException.Create(message, DecoderFailureInfo(reason));

    internal sealed class Operation(PersistentZlibInflater owner)
    {
        public bool ContextTouched { get; private set; }

        public Task<DecompressedChunk> DecompressChunkAsync(
            byte[] compressed,
            int maximumOutputLength,
            int configuredLimit,
            CancellationToken cancellationToken,
            RfbDecoderFailureReason overflowReason = RfbDecoderFailureReason.OutputLimitExceeded)
        {
            ArgumentNullException.ThrowIfNull(compressed);
            ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputLength);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuredLimit);
            ContextTouched = true;
            return owner.DecompressChunkAsync(
                compressed,
                maximumOutputLength,
                configuredLimit,
                overflowReason,
                cancellationToken);
        }
    }

    internal readonly record struct DecompressedChunk(byte[] Buffer, int Length);

    private enum InflaterState
    {
        Active,
        Faulted,
        Disposing,
        Disposed,
    }

    private sealed class SegmentedReadStream(string encodingName) : Stream
    {
        private byte[]? _segment;
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public void SetSegment(byte[] segment)
        {
            ArgumentNullException.ThrowIfNull(segment);
            if (_segment is not null && _position != _segment.Length)
            {
                throw RfbProtocolException.Create(
                    $"The previous {encodingName} compressed segment was not fully consumed.",
                    new RfbProtocolFailureInfo(
                        RfbProtocolFailureKind.DecoderFailure,
                        DecoderFailureReason: RfbDecoderFailureReason.IncompleteCompressedSegment));
            }

            _segment = segment;
            _position = 0;
        }

        public void ReleaseSegment()
        {
            if (_segment is null || _position != _segment.Length)
            {
                throw new InvalidOperationException(
                    $"The current {encodingName} compressed segment has not been fully consumed.");
            }

            _segment = null;
            _position = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_segment is null || _position == _segment.Length)
            {
                return 0;
            }

            var prefixLength = _segment.Length - 4;
            var remainingInPortion = _position < prefixLength
                ? prefixLength - _position
                : _segment.Length - _position;
            var count = Math.Min(buffer.Length, remainingInPortion);
            _segment.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
