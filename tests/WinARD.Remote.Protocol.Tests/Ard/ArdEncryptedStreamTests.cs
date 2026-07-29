using System.Buffers.Binary;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Ard;

public sealed class ArdEncryptedStreamTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
    private static readonly byte[] InitialIv = Enumerable.Range(16, 16).Select(value => (byte)value).ToArray();

    [Fact]
    public async Task Plaintext_mode_forwards_reads_and_writes_unchanged()
    {
        await using var inner = new ScriptedDuplexStream([1, 2, 3], maxRead: 1);
        await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        var read = new byte[3];

        await ReadExactlyAsync(stream, read);
        await stream.WriteAsync(new byte[] { 4, 5, 6 });

        Assert.Equal(new byte[] { 1, 2, 3 }, read);
        Assert.Equal(new byte[] { 4, 5, 6 }, inner.WrittenBytes);
    }

    [Fact]
    public async Task Activate_encrypts_all_later_writes_after_existing_plaintext()
    {
        await using var inner = new ScriptedDuplexStream([]);
        await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        await stream.WriteAsync(new byte[] { 0x12, 0, 0, 2, 0, 1, 0, 0 });
        stream.Activate(new ArdSessionCipherMaterial(Key.ToArray(), InitialIv.ToArray()));

        var pointer = new byte[] { 5, 0, 0, 100, 0, 200 };
        await stream.WriteAsync(pointer);

        var encryptedWire = inner.WrittenBytes[8..];
        var ciphertextLength = BinaryPrimitives.ReadUInt16BigEndian(encryptedWire);
        Assert.Equal(encryptedWire.Length - 2, ciphertextLength);
        using var decoded = ArdEncryptedPacketCodec.Decrypt(
            Key,
            InitialIv,
            0,
            encryptedWire.AsSpan(2));
        Assert.Equal(pointer, decoded.Payload);
    }

    [Fact]
    public async Task ReadAsync_reassembles_fragmented_encrypted_packets_and_chains_iv()
    {
        var firstPayload = new byte[] { 0, 0, 0, 1 };
        var secondPayload = new byte[] { 2, 3, 4, 5, 6 };
        var first = ArdEncryptedPacketCodec.Encrypt(Key, InitialIv, 0, firstPayload);
        var nextIv = first[^16..];
        var second = ArdEncryptedPacketCodec.Encrypt(Key, nextIv, 1, secondPayload);
        await using var inner = new ScriptedDuplexStream([.. first, .. second], maxRead: 3);
        await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        stream.Activate(new ArdSessionCipherMaterial(Key.ToArray(), InitialIv.ToArray()));
        var output = new byte[firstPayload.Length + secondPayload.Length];

        await ReadExactlyAsync(stream, output);

        Assert.Equal(firstPayload.Concat(secondPayload), output);
    }

    [Fact]
    public async Task ReadAsync_skips_empty_encrypted_payload_without_reporting_end_of_stream()
    {
        var empty = ArdEncryptedPacketCodec.Encrypt(Key, InitialIv, 0, []);
        var nextIv = empty[^16..];
        var payload = new byte[] { 9, 8, 7 };
        var data = ArdEncryptedPacketCodec.Encrypt(Key, nextIv, 1, payload);
        await using var inner = new ScriptedDuplexStream([.. empty, .. data], maxRead: 2);
        await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        stream.Activate(new ArdSessionCipherMaterial(Key.ToArray(), InitialIv.ToArray()));
        var output = new byte[payload.Length];

        var received = await stream.ReadAsync(output);

        Assert.Equal(payload.Length, received);
        Assert.Equal(payload, output);
    }

    [Fact]
    public async Task WriteAsync_splits_payloads_that_exceed_one_encrypted_packet()
    {
        await using var inner = new ScriptedDuplexStream([]);
        await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        stream.Activate(new ArdSessionCipherMaterial(Key.ToArray(), InitialIv.ToArray()));
        var payload = Enumerable.Range(0, ArdEncryptedPacketCodec.MaximumPayloadLength + 37)
            .Select(value => (byte)value)
            .ToArray();

        await stream.WriteAsync(payload);

        Assert.Equal(payload, DecodeClientPackets(inner.WrittenBytes).SelectMany(value => value));
        Assert.Equal(2, DecodeClientPackets(inner.WrittenBytes).Count);
    }

    [Fact]
    public async Task Concurrent_writes_use_distinct_sequences_and_chained_ivs()
    {
        await using var inner = new ScriptedDuplexStream([]);
        await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        stream.Activate(new ArdSessionCipherMaterial(Key.ToArray(), InitialIv.ToArray()));
        var writes = Enumerable.Range(0, 100)
            .Select(value => stream.WriteAsync(new byte[] { checked((byte)value) }).AsTask())
            .ToArray();

        await Task.WhenAll(writes);

        var decoded = DecodeClientPackets(inner.WrittenBytes);
        Assert.Equal(100, decoded.Count);
        Assert.Equal(Enumerable.Range(0, 100), decoded.Select(payload => (int)payload[0]).Order());
    }

    [Fact]
    public async Task Write_failure_immediately_clears_active_cipher_material()
    {
        var key = Key.ToArray();
        var initialIv = InitialIv.ToArray();
        await using var inner = new ScriptedDuplexStream([], writeException: new IOException("write failed"));
        await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        stream.Activate(new ArdSessionCipherMaterial(key, initialIv));

        await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(new byte[] { 1 }).AsTask());

        Assert.False(stream.IsEncrypted);
        Assert.All(key, value => Assert.Equal(0, value));
        Assert.All(initialIv, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Integrity_failure_immediately_clears_active_cipher_material()
    {
        var key = Key.ToArray();
        var initialIv = InitialIv.ToArray();
        var packet = ArdEncryptedPacketCodec.Encrypt(key, initialIv, 0, [1, 2, 3]);
        packet[^1] ^= 0x80;
        await using var inner = new ScriptedDuplexStream(packet);
        await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        stream.Activate(new ArdSessionCipherMaterial(key, initialIv));

        await Assert.ThrowsAsync<RfbProtocolException>(() => stream.ReadAsync(new byte[3]).AsTask());

        Assert.False(stream.IsEncrypted);
        Assert.All(key, value => Assert.Equal(0, value));
        Assert.All(initialIv, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    public async Task ReadAsync_rejects_invalid_outer_ciphertext_length_with_receive_packet_context(
        ushort ciphertextLength)
    {
        var header = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(header, ciphertextLength);
        await using var inner = new ScriptedDuplexStream(header);
        await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        stream.Activate(new ArdSessionCipherMaterial(Key.ToArray(), InitialIv.ToArray()));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() => stream.ReadAsync(new byte[1]).AsTask());

        Assert.Equal(RfbProtocolFailureKind.ArdEncryptionPacket, exception.Failure?.Kind);
        Assert.Equal(ArdEncryptedPacketFailureStage.OuterLength, exception.Failure?.ArdEncryptionStage);
        Assert.Equal(ArdEncryptedPacketDirection.Receive, exception.Failure?.ArdEncryptionDirection);
        Assert.Equal((uint)0, exception.Failure?.ArdEncryptionSequence);
        Assert.Equal((int)ciphertextLength, exception.Failure?.ArdCiphertextLength);
    }

    [Fact]
    public async Task ReadAsync_reports_truncated_ciphertext_with_receive_packet_context()
    {
        await using var inner = new ScriptedDuplexStream([0, 32, .. new byte[16]]);
        await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        stream.Activate(new ArdSessionCipherMaterial(Key.ToArray(), InitialIv.ToArray()));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() => stream.ReadAsync(new byte[1]).AsTask());

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(ArdEncryptedPacketFailureStage.TruncatedCiphertext, exception.Failure?.ArdEncryptionStage);
        Assert.Equal(ArdEncryptedPacketDirection.Receive, exception.Failure?.ArdEncryptionDirection);
        Assert.Equal((uint)0, exception.Failure?.ArdEncryptionSequence);
        Assert.Equal(32, exception.Failure?.ArdCiphertextLength);
    }

    [Fact]
    public async Task ReadAsync_ard_packet_context_is_retained_when_server_message_context_is_added()
    {
        await using var inner = new ScriptedDuplexStream([0, 0]);
        await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        stream.Activate(new ArdSessionCipherMaterial(Key.ToArray(), InitialIv.ToArray()));
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() => stream.ReadAsync(new byte[1]).AsTask());

        var contextualized = exception.WithContext(new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.UnexpectedServerMessage,
            RfbProtocolReadStage.ServerMessageType,
            ServerMessageType: 0x14));

        Assert.Equal(RfbProtocolReadStage.ServerMessageType, contextualized.Failure?.ReadStage);
        Assert.Equal((byte)0x14, contextualized.Failure?.ServerMessageType);
        Assert.Equal(ArdEncryptedPacketFailureStage.OuterLength, contextualized.Failure?.ArdEncryptionStage);
        Assert.Equal(ArdEncryptedPacketDirection.Receive, contextualized.Failure?.ArdEncryptionDirection);
        Assert.Equal((uint)0, contextualized.Failure?.ArdEncryptionSequence);
        Assert.Equal(0, contextualized.Failure?.ArdCiphertextLength);
    }

    private static List<byte[]> DecodeClientPackets(byte[] wire)
    {
        var decoded = new List<byte[]>();
        var offset = 0;
        var sequence = 0u;
        var iv = InitialIv.ToArray();
        while (offset < wire.Length)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(offset));
            using var packet = ArdEncryptedPacketCodec.Decrypt(
                Key,
                iv,
                sequence,
                wire.AsSpan(offset + 2, length));
            decoded.Add(packet.Payload.ToArray());
            iv = packet.NextIv.ToArray();
            offset += 2 + length;
            sequence++;
        }

        return decoded;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var received = await stream.ReadAsync(destination[read..]);
            Assert.True(received > 0);
            read += received;
        }
    }

    private sealed class ScriptedDuplexStream(
        byte[] input,
        int maxRead = int.MaxValue,
        Exception? writeException = null) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly MemoryStream _output = new();
        private readonly object _writeSync = new();

        public byte[] WrittenBytes
        {
            get
            {
                lock (_writeSync)
                {
                    return _output.ToArray();
                }
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            _input.Read(buffer, offset, Math.Min(count, maxRead));
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer[..Math.Min(buffer.Length, maxRead)], cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_writeSync)
            {
                _output.Write(buffer, offset, count);
            }
        }
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (writeException is not null)
            {
                return ValueTask.FromException(writeException);
            }

            lock (_writeSync)
            {
                _output.Write(buffer.Span);
            }

            return ValueTask.CompletedTask;
        }
    }
}
