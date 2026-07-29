using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Ard;

public sealed class ArdEncryptedStream : Stream
{
    private readonly Stream _inner;
    private readonly ProtocolLimits _limits;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _stateSync = new();
    private ArdSessionCipherMaterial? _material;
    private byte[]? _sendIv;
    private byte[]? _receiveIv;
    private byte[]? _decryptedPayload;
    private int _decryptedOffset;
    private uint _sendSequence;
    private uint _receiveSequence;
    private bool _faulted;
    private bool _disposed;

    public ArdEncryptedStream(Stream inner, ProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(limits);
        if (!inner.CanRead || !inner.CanWrite)
        {
            throw new ArgumentException("The ARD transport stream must support reads and writes.", nameof(inner));
        }

        _inner = inner;
        _limits = limits;
    }

    public bool IsEncrypted
    {
        get
        {
            lock (_stateSync)
            {
                return _material is not null && !_disposed;
            }
        }
    }

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal void Activate(ArdSessionCipherMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        _writeGate.Wait();
        try
        {
            _readGate.Wait();
            try
            {
                ActivateLocked(material);
            }
            finally
            {
                _readGate.Release();
            }
        }
        catch
        {
            material.Dispose();
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal async ValueTask WritePlaintextAndActivateAsync(
        ReadOnlyMemory<byte> plaintext,
        ArdSessionCipherMaterial material,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(material);
        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (IsEncrypted)
            {
                throw new InvalidOperationException("ARD stream encryption is already active.");
            }

            await _inner.WriteAsync(plaintext, cancellationToken).ConfigureAwait(false);
            await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ActivateLocked(material);
            }
            finally
            {
                _readGate.Release();
            }
        }
        catch
        {
            material.Dispose();
            Fault();
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        ThrowIfUnavailable();
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (!IsEncrypted)
            {
                return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            while (true)
            {
                if (TryCopyDecryptedPayload(buffer.Span, out var copied))
                {
                    return copied;
                }

                await ReadEncryptedPacketAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            Fault();
            throw;
        }
        finally
        {
            _readGate.Release();
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (!IsEncrypted)
            {
                await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                return;
            }

            var offset = 0;
            while (offset < buffer.Length)
            {
                var count = Math.Min(ArdEncryptedPacketCodec.MaximumPayloadLength, buffer.Length - offset);
                await WriteEncryptedPacketAsync(buffer.Slice(offset, count), cancellationToken).ConfigureAwait(false);
                offset += count;
            }
        }
        catch
        {
            Fault();
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override void Flush()
    {
        ThrowIfUnavailable();
        _inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return _inner.FlushAsync(cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeState();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        DisposeState();
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask WriteEncryptedPacketAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        byte[]? key = null;
        byte[]? sendIv = null;
        byte[]? packet = null;
        byte[]? nextIv = null;
        try
        {
            uint sequence;
            lock (_stateSync)
            {
                ThrowIfUnavailableLocked();
                var material = _material ??
                    throw new InvalidOperationException("ARD stream encryption is not active.");
                key = material.Key.ToArray();
                sendIv = (_sendIv ??
                    throw new InvalidOperationException("ARD send IV is unavailable.")).ToArray();
                sequence = _sendSequence;
            }

            packet = ArdEncryptedPacketCodec.Encrypt(key, sendIv, sequence, payload.Span);
            await _inner.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            nextIv = packet[^16..].ToArray();
            lock (_stateSync)
            {
                ThrowIfUnavailableLocked();
                CryptographicOperations.ZeroMemory(_sendIv!);
                _sendIv = nextIv;
                nextIv = null;
                _sendSequence++;
            }
        }
        finally
        {
            ClearAndNull(ref key);
            ClearAndNull(ref sendIv);
            ClearAndNull(ref nextIv);
            ClearAndNull(ref packet);
        }
    }

    private async ValueTask ReadEncryptedPacketAsync(CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(ushort)];
        byte[]? ciphertext = null;
        byte[]? key = null;
        byte[]? receiveIv = null;
        byte[]? payload = null;
        byte[]? nextIv = null;
        try
        {
            uint sequence;
            lock (_stateSync)
            {
                ThrowIfUnavailableLocked();
                sequence = _receiveSequence;
            }

            await ReadExactlyFromInnerAsync(header, cancellationToken).ConfigureAwait(false);
            var ciphertextLength = BinaryPrimitives.ReadUInt16BigEndian(header);
            if (ciphertextLength == 0 ||
                ciphertextLength % 16 != 0 ||
                ciphertextLength > _limits.MaxMessageBytes)
            {
                throw RfbProtocolException.Create(
                    "ARD encrypted stream packet length is invalid.",
                    ReceivePacketFailureInfo(
                        RfbProtocolFailureKind.ArdEncryptionPacket,
                        ArdEncryptedPacketFailureStage.OuterLength,
                        sequence,
                        ciphertextLength));
            }

            ciphertext = new byte[ciphertextLength];
            try
            {
                await ReadExactlyFromInnerAsync(ciphertext, cancellationToken).ConfigureAwait(false);
            }
            catch (RfbProtocolException exception) when (
                exception.Failure?.Kind == RfbProtocolFailureKind.TruncatedRead)
            {
                throw exception.WithContext(ReceivePacketFailureInfo(
                    RfbProtocolFailureKind.TruncatedRead,
                    ArdEncryptedPacketFailureStage.TruncatedCiphertext,
                    sequence,
                    ciphertextLength));
            }

            lock (_stateSync)
            {
                ThrowIfUnavailableLocked();
                var material = _material ?? throw new InvalidOperationException("ARD stream encryption is not active.");
                key = material.Key.ToArray();
                receiveIv = (_receiveIv ??
                    throw new InvalidOperationException("ARD receive IV is unavailable.")).ToArray();
            }

            using var decoded = ArdEncryptedPacketCodec.Decrypt(key, receiveIv, sequence, ciphertext);
            payload = decoded.Payload.ToArray();
            nextIv = decoded.NextIv.ToArray();
            try
            {
                lock (_stateSync)
                {
                    ThrowIfUnavailableLocked();
                    ClearDecryptedPayloadLocked();
                    CryptographicOperations.ZeroMemory(_receiveIv!);
                    _decryptedPayload = payload;
                    payload = null;
                    _decryptedOffset = 0;
                    _receiveIv = nextIv;
                    nextIv = null;
                    _receiveSequence++;
                }
            }
            catch (RfbProtocolException exception)
            {
                throw exception.WithContext(ReceivePacketFailureInfo(
                    RfbProtocolFailureKind.ArdEncryptionPacket,
                    ArdEncryptedPacketFailureStage.StateCommit,
                    sequence,
                    ciphertextLength));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            ClearAndNull(ref payload);
            ClearAndNull(ref nextIv);
            ClearAndNull(ref key);
            ClearAndNull(ref receiveIv);
        }
    }

    private bool TryCopyDecryptedPayload(Span<byte> destination, out int copied)
    {
        lock (_stateSync)
        {
            if (_decryptedPayload is null || _decryptedOffset >= _decryptedPayload.Length)
            {
                ClearDecryptedPayloadLocked();
                copied = 0;
                return false;
            }

            copied = Math.Min(destination.Length, _decryptedPayload.Length - _decryptedOffset);
            _decryptedPayload.AsSpan(_decryptedOffset, copied).CopyTo(destination);
            _decryptedOffset += copied;
            if (_decryptedOffset == _decryptedPayload.Length)
            {
                ClearDecryptedPayloadLocked();
            }

            return true;
        }
    }

    private async ValueTask ReadExactlyFromInnerAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var received = await _inner.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (received == 0)
            {
                throw RfbProtocolException.Create(
                    "Unexpected end of stream while reading an ARD encrypted packet.",
                    new RfbProtocolFailureInfo(RfbProtocolFailureKind.TruncatedRead));
            }

            read += received;
        }
    }

    private static RfbProtocolFailureInfo ReceivePacketFailureInfo(
        RfbProtocolFailureKind kind,
        ArdEncryptedPacketFailureStage stage,
        uint sequence,
        int ciphertextLength) =>
        new(
            kind,
            ArdEncryptionStage: stage,
            ArdEncryptionDirection: ArdEncryptedPacketDirection.Receive,
            ArdEncryptionSequence: sequence,
            ArdCiphertextLength: ciphertextLength);

    private void Fault()
    {
        lock (_stateSync)
        {
            _faulted = true;
            ClearCipherStateLocked();
        }
    }

    private void ActivateLocked(ArdSessionCipherMaterial material)
    {
        lock (_stateSync)
        {
            ThrowIfUnavailableLocked();
            if (_material is not null)
            {
                throw new InvalidOperationException("ARD stream encryption is already active.");
            }

            _sendIv = material.InitialIv.ToArray();
            _receiveIv = material.InitialIv.ToArray();
            _material = material;
        }
    }

    private void ThrowIfUnavailable()
    {
        lock (_stateSync)
        {
            ThrowIfUnavailableLocked();
        }
    }

    private void ThrowIfUnavailableLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw RfbProtocolException.Create(
                "The ARD encrypted stream is faulted; discard the connection.",
                new RfbProtocolFailureInfo(RfbProtocolFailureKind.ArdEncryptionPacket));
        }
    }

    private void DisposeState()
    {
        _writeGate.Wait();
        try
        {
            _readGate.Wait();
            try
            {
                lock (_stateSync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    ClearCipherStateLocked();
                }
            }
            finally
            {
                _readGate.Release();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void ClearDecryptedPayloadLocked()
    {
        ClearAndNull(ref _decryptedPayload);
        _decryptedOffset = 0;
    }

    private void ClearCipherStateLocked()
    {
        _material?.Dispose();
        _material = null;
        ClearAndNull(ref _sendIv);
        ClearAndNull(ref _receiveIv);
        ClearDecryptedPayloadLocked();
        _sendSequence = 0;
        _receiveSequence = 0;
    }

    private static void ClearAndNull(ref byte[]? value)
    {
        var bytes = value;
        value = null;
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
