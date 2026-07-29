using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Ard;

public sealed class ArdSessionEncryptionTests
{
    private static readonly byte[] AuthenticationKey = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
    private static readonly byte[] SessionKey = Enumerable.Range(32, 16).Select(value => (byte)value).ToArray();
    private static readonly byte[] SessionIv = Enumerable.Range(64, 16).Select(value => (byte)value).ToArray();

    [Fact]
    public async Task Request_writes_plaintext_message_and_enters_requested_state()
    {
        await using var inner = new ScriptedDuplexStream([]);
        await using var transport = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        await using var encryption = new ArdSessionEncryption(
            transport,
            new ArdAuthenticationResult(AuthenticationKey.ToArray()));

        await encryption.RequestAsync(CancellationToken.None);

        Assert.Equal(ArdSessionEncryptionState.Requested, encryption.State);
        Assert.False(transport.IsEncrypted);
        Assert.Equal(
            new byte[] { 0x12, 0, 0, 1, 0, 1, 0, 1, 0, 0, 0, 1 },
            inner.WrittenBytes);
    }

    [Fact]
    public async Task Encryption_rectangle_stays_pending_until_frame_completion_then_switches_atomically()
    {
        var update = CreateSessionEncryptionUpdate();
        await using var inner = new ScriptedDuplexStream(update, maxRead: 3);
        await using var transport = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        await using var encryption = new ArdSessionEncryption(
            transport,
            new ArdAuthenticationResult(AuthenticationKey.ToArray()));
        await encryption.RequestAsync(CancellationToken.None);
        using var framebuffer = new FramebufferModel(10, 10, ProtocolLimits.Default);
        await using var updates = FramebufferUpdateReader.CreateSession(
            framebuffer,
            PixelFormat.WinArdBgra32,
            encryption.CreateDecoder());

        _ = await updates.ApplyAsync(transport, CancellationToken.None);

        Assert.Equal(ArdSessionEncryptionState.PendingActivation, encryption.State);
        Assert.False(transport.IsEncrypted);

        await encryption.CompleteFramebufferUpdateAsync(CancellationToken.None);
        var pointer = new byte[] { 5, 0, 0, 100, 0, 200 };
        await transport.WriteAsync(pointer);

        Assert.Equal(ArdSessionEncryptionState.Encrypted, encryption.State);
        Assert.True(transport.IsEncrypted);
        var wire = inner.WrittenBytes;
        Assert.Equal(
            new byte[] { 0x12, 0, 0, 1, 0, 1, 0, 1, 0, 0, 0, 1 },
            wire[..12]);
        Assert.Equal(
            new byte[] { 0x12, 0, 0, 2, 0, 1, 0, 0 },
            wire[12..20]);
        using var decoded = ArdEncryptedPacketCodec.Decrypt(SessionKey, SessionIv, 0, wire.AsSpan(22));
        Assert.Equal(pointer, decoded.Payload);
    }

    [Fact]
    public async Task Pointer_write_waiting_during_ack_is_encrypted_after_atomic_switch()
    {
        await using var inner = new ScriptedDuplexStream(
            CreateSessionEncryptionUpdate(),
            maxRead: 3,
            blockedWriteNumber: 2);
        await using var transport = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        await using var encryption = new ArdSessionEncryption(
            transport,
            new ArdAuthenticationResult(AuthenticationKey.ToArray()));
        await encryption.RequestAsync(CancellationToken.None);
        using var framebuffer = new FramebufferModel(10, 10, ProtocolLimits.Default);
        await using var updates = FramebufferUpdateReader.CreateSession(
            framebuffer,
            PixelFormat.WinArdBgra32,
            encryption.CreateDecoder());
        _ = await updates.ApplyAsync(transport, CancellationToken.None);

        var activation = encryption.CompleteFramebufferUpdateAsync(CancellationToken.None).AsTask();
        await inner.BlockedWriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var pointer = new byte[] { 5, 0, 0, 12, 0, 34 };
        var pointerWrite = transport.WriteAsync(pointer).AsTask();
        Assert.False(pointerWrite.IsCompleted);

        inner.ReleaseBlockedWrite();
        await Task.WhenAll(activation, pointerWrite);

        var wire = inner.WrittenBytes;
        Assert.Equal(new byte[] { 0x12, 0, 0, 2, 0, 1, 0, 0 }, wire[12..20]);
        using var decoded = ArdEncryptedPacketCodec.Decrypt(SessionKey, SessionIv, 0, wire.AsSpan(22));
        Assert.Equal(pointer, decoded.Payload);
    }

    [Fact]
    public async Task First_completed_framebuffer_update_without_1103_fails_closed()
    {
        await using var inner = new ScriptedDuplexStream([]);
        await using var transport = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        await using var encryption = new ArdSessionEncryption(
            transport,
            new ArdAuthenticationResult(AuthenticationKey.ToArray()));
        await encryption.RequestAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            encryption.CompleteFramebufferUpdateAsync(CancellationToken.None).AsTask());

        Assert.Equal(
            RfbProtocolFailureKind.ArdEncryptionNegotiation,
            exception.Failure?.Kind);
        Assert.False(transport.IsEncrypted);
    }

    private static byte[] CreateSessionEncryptionUpdate()
    {
        var message = new byte[4 + 12 + 36];
        message[0] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 1);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(12), (int)RfbEncodingType.ArdSessionEncryption);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(16), 1);
        EncryptEcb(SessionKey).CopyTo(message, 20);
        EncryptEcb(SessionIv).CopyTo(message, 36);
        return message;
    }

    private static byte[] EncryptEcb(byte[] plaintext)
    {
#pragma warning disable CA5358 // AES-ECB is required to construct an Apple Remote Desktop test fixture.
        using var aes = Aes.Create();
        aes.Key = AuthenticationKey;
        return aes.EncryptEcb(plaintext, PaddingMode.None);
#pragma warning restore CA5358
    }

    private sealed class ScriptedDuplexStream(
        byte[] input,
        int maxRead = int.MaxValue,
        int blockedWriteNumber = 0) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly MemoryStream _output = new();
        private readonly TaskCompletionSource _blockedWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseBlockedWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;

        public byte[] WrittenBytes => _output.ToArray();
        public Task BlockedWriteStarted => _blockedWriteStarted.Task;
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
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var writeNumber = Interlocked.Increment(ref _writeCount);
            if (writeNumber == blockedWriteNumber)
            {
                _blockedWriteStarted.TrySetResult();
                await _releaseBlockedWrite.Task.WaitAsync(cancellationToken);
            }

            _output.Write(buffer.Span);
        }

        public void ReleaseBlockedWrite() => _releaseBlockedWrite.TrySetResult();
    }
}
