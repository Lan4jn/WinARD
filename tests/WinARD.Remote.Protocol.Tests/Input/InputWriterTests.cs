using WinARD.Remote.Protocol.Input;
using WinARD.Remote.Protocol.IO;
using WinARD.Remote.Protocol.Errors;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Input;

public sealed class InputWriterTests
{
    [Fact]
    public async Task Pointer_event_writes_type_buttons_and_big_endian_coordinates()
    {
        await using var stream = new MemoryStream();
        var pointer = new PointerEventWriter(new RfbWriter(stream));

        await pointer.WriteAsync(0b0000_0101, 0x1234, 0xABCD, CancellationToken.None);

        Assert.Equal([5, 5, 0x12, 0x34, 0xAB, 0xCD], stream.ToArray());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(65536, 0)]
    [InlineData(0, 65536)]
    public async Task Pointer_event_rejects_out_of_range_coordinates_without_writing(int x, int y)
    {
        await using var stream = new MemoryStream();
        var pointer = new PointerEventWriter(new RfbWriter(stream));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            pointer.WriteAsync(0, x, y, CancellationToken.None).AsTask());

        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task Key_event_writes_down_flag_padding_and_full_keysym_range()
    {
        await using var stream = new MemoryStream();
        var key = new KeyEventWriter(new RfbWriter(stream));

        await key.WriteAsync(true, uint.MaxValue, CancellationToken.None);
        await key.WriteAsync(false, 0, CancellationToken.None);

        Assert.Equal(
            [
                4, 1, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF,
                4, 0, 0, 0, 0, 0, 0, 0,
            ],
            stream.ToArray());
    }

    [Fact]
    public async Task Cancellation_poisons_shared_writer_without_writing()
    {
        await using var stream = new MemoryStream();
        var pointer = new PointerEventWriter(new RfbWriter(stream));
        var key = new KeyEventWriter(new RfbWriter(stream));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pointer.WriteAsync(0, 0, 0, cancellation.Token).AsTask());
        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            key.WriteAsync(true, 1, cancellation.Token).AsTask());

        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task Keyboard_adapter_tracks_chord_in_press_order()
    {
        await using var stream = new MemoryStream();
        var keyboard = new KeyboardInputAdapter(new KeyEventWriter(new RfbWriter(stream)));

        await keyboard.KeyDownAsync(0xFFE3, CancellationToken.None);
        await keyboard.KeyDownAsync(0xFFE9, CancellationToken.None);
        await keyboard.KeyDownAsync((uint)'A', CancellationToken.None);

        Assert.Equal([0xFFE3u, 0xFFE9u, (uint)'A'], keyboard.PressedKeysyms);
    }

    [Fact]
    public async Task Repeated_keydown_is_sent_but_tracked_once()
    {
        await using var stream = new MemoryStream();
        var keyboard = new KeyboardInputAdapter(new KeyEventWriter(new RfbWriter(stream)));

        await keyboard.KeyDownAsync((uint)'A', CancellationToken.None);
        await keyboard.KeyDownAsync((uint)'A', CancellationToken.None);

        Assert.Equal([(uint)'A'], keyboard.PressedKeysyms);
        Assert.Equal(16, stream.Length);
    }

    [Fact]
    public async Task Unknown_keyup_is_ignored()
    {
        await using var stream = new MemoryStream();
        var keyboard = new KeyboardInputAdapter(new KeyEventWriter(new RfbWriter(stream)));

        await keyboard.KeyUpAsync((uint)'A', CancellationToken.None);

        Assert.Empty(keyboard.PressedKeysyms);
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task Failed_keydown_does_not_update_state()
    {
        await using var stream = new ControlledFailureWriteStream { FailOnWriteNumber = 1 };
        var keyboard = new KeyboardInputAdapter(new KeyEventWriter(new RfbWriter(stream)));

        await Assert.ThrowsAsync<IOException>(() =>
            keyboard.KeyDownAsync((uint)'A', CancellationToken.None).AsTask());

        Assert.Empty(keyboard.PressedKeysyms);
    }

    [Fact]
    public async Task Failed_keyup_faults_adapter_and_discards_inferred_state()
    {
        await using var stream = new ControlledFailureWriteStream { FailOnWriteNumber = 2 };
        var keyboard = new KeyboardInputAdapter(new KeyEventWriter(new RfbWriter(stream)));
        await keyboard.KeyDownAsync((uint)'A', CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() =>
            keyboard.KeyUpAsync((uint)'A', CancellationToken.None).AsTask());

        Assert.Empty(keyboard.PressedKeysyms);
        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            keyboard.KeyUpAsync((uint)'B', CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Focus_loss_releases_keys_in_reverse_press_order()
    {
        await using var stream = new MemoryStream();
        var keyboard = new KeyboardInputAdapter(new KeyEventWriter(new RfbWriter(stream)));
        await keyboard.KeyDownAsync(1, CancellationToken.None);
        await keyboard.KeyDownAsync(2, CancellationToken.None);
        await keyboard.KeyDownAsync(3, CancellationToken.None);

        await keyboard.OnWindowFocusLostAsync(CancellationToken.None);

        Assert.Empty(keyboard.PressedKeysyms);
        Assert.Equal([3u, 2u, 1u], WrittenKeysyms(stream.ToArray().AsSpan(24)));
        Assert.All(
            stream.ToArray().AsSpan(24).ToArray().Chunk(8),
            message => Assert.Equal(0, message[1]));
    }

    [Fact]
    public async Task Disconnect_release_failure_faults_adapter_without_retry()
    {
        await using var stream = new ControlledFailureWriteStream { FailOnWriteNumber = 5 };
        var keyboard = new KeyboardInputAdapter(new KeyEventWriter(new RfbWriter(stream)));
        await keyboard.KeyDownAsync(1, CancellationToken.None);
        await keyboard.KeyDownAsync(2, CancellationToken.None);
        await keyboard.KeyDownAsync(3, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() =>
            keyboard.OnDisconnectedAsync(CancellationToken.None).AsTask());

        Assert.Empty(keyboard.PressedKeysyms);
        stream.FailOnWriteNumber = null;

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            keyboard.OnDisconnectedAsync(CancellationToken.None).AsTask());

        Assert.Empty(keyboard.PressedKeysyms);
        Assert.Equal([3u], WrittenKeysyms(stream.Bytes.ToArray().AsSpan(24)));
    }

    private static uint[] WrittenKeysyms(ReadOnlySpan<byte> bytes)
    {
        var result = new uint[bytes.Length / 8];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                bytes.Slice(index * 8 + 4, 4));
        }

        return result;
    }

    private sealed class ControlledFailureWriteStream : Stream
    {
        private int _writeCount;

        public int? FailOnWriteNumber { get; set; }
        public List<byte> Bytes { get; } = [];
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes.Count;
        public override long Position
        {
            get => Bytes.Count;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writeCount++;
            if (_writeCount == FailOnWriteNumber)
            {
                throw new IOException("Injected write failure.");
            }

            Bytes.AddRange(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
