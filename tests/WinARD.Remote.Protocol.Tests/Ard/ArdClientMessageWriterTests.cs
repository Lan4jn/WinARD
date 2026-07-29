using System.Text;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.IO;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Ard;

public sealed class ArdClientMessageWriterTests
{
    [Fact]
    public async Task Set_mode_shared_and_set_display_write_exact_control_messages()
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));

        await writer.WriteSetModeAsync(ArdControlMode.Shared, CancellationToken.None);
        await writer.WriteSetDisplayAsync(CancellationToken.None);

        Assert.Equal(
            [0x0A, 0x00, 0x00, 0x01, 0x0D, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            stream.ToArray());
        Assert.Equal(2, stream.WriteCount);
    }

    [Fact]
    public async Task Auto_framebuffer_update_writes_the_exact_full_screen_request()
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));

        await writer.WriteAutoFramebufferUpdateAsync(0x1234, 0x5678, CancellationToken.None);

        Assert.Equal(
            [0x09, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x12, 0x34, 0x56, 0x78],
            stream.ToArray());
        Assert.Equal(1, stream.WriteCount);
    }

    [Theory]
    [InlineData((ushort)0, (ushort)1)]
    [InlineData((ushort)1, (ushort)0)]
    public async Task Auto_framebuffer_update_rejects_zero_dimensions_before_writing(ushort width, ushort height)
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            writer.WriteAutoFramebufferUpdateAsync(width, height, CancellationToken.None).AsTask());

        Assert.Empty(stream.ToArray());
        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public async Task Viewer_info_writes_expected_header_versions_and_bitmap()
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));

        await writer.WriteViewerInfoAsync(CancellationToken.None);

        var message = stream.ToArray();
        Assert.Equal(66, message.Length);
        Assert.Equal([0x21, 0x00, 0x00, 0x3E], message[..4]);
        Assert.Equal([0x00, 0x01], message[4..6]);
        Assert.Equal([0x00, 0x00, 0x00, 0x02], message[6..10]);
        Assert.Equal(
            [
                0x00, 0x00, 0x00, 0x06,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x0F,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
            ],
            message[10..34]);

        var bitmap = message[34..];
        Assert.Equal(0xB0, bitmap[0]);
        Assert.Equal(0x0C, bitmap[2]);
        Assert.Equal(0x03, bitmap[3]);
        Assert.Equal(0x90, bitmap[4]);
        Assert.Equal(0x40, bitmap[10]);
        Assert.All(
            bitmap.Where((_, index) => index is not 0 and not 2 and not 3 and not 4 and not 10),
            value => Assert.Equal(0, value));
        Assert.Equal(1, stream.WriteCount);
    }

    [Fact]
    public async Task Session_command_writes_ascii_username_and_terminal_nul()
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));

        await writer.WriteSessionCommandAsync(2, "alice", CancellationToken.None);

        var message = stream.ToArray();
        Assert.Equal(74, message.Length);
        Assert.Equal([0x00, 0x48, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00], message[..10]);
        Assert.Equal(Encoding.UTF8.GetBytes("alice"), message[10..15]);
        Assert.All(message[15..], value => Assert.Equal(0, value));
        Assert.Equal(0, message[^1]);
        Assert.Equal(1, stream.WriteCount);
    }

    [Fact]
    public async Task Session_command_allows_an_empty_username()
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));

        await writer.WriteSessionCommandAsync(0, string.Empty, CancellationToken.None);

        var message = stream.ToArray();
        Assert.Equal(74, message.Length);
        Assert.All(message[10..], value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Session_command_keeps_a_multibyte_username_that_exactly_fills_the_field()
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));
        var username = new string('你', 21);

        await writer.WriteSessionCommandAsync(1, username, CancellationToken.None);

        var message = stream.ToArray();
        Assert.Equal(Encoding.UTF8.GetBytes(username), message[10..73]);
        Assert.Equal(0, message[73]);
    }

    [Fact]
    public async Task Session_command_truncates_multibyte_username_without_splitting_utf8()
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));
        var username = new string('你', 20) + "😀";

        await writer.WriteSessionCommandAsync(1, username, CancellationToken.None);

        var message = stream.ToArray();
        Assert.Equal(Encoding.UTF8.GetBytes(new string('你', 20)), message[10..70]);
        Assert.All(message[70..], value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Session_command_rejects_embedded_nul_before_writing()
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.WriteSessionCommandAsync(1, "alice\0bob", CancellationToken.None).AsTask());

        Assert.Empty(stream.ToArray());
        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public async Task Session_command_rejects_an_unpaired_high_surrogate_before_writing()
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.WriteSessionCommandAsync(1, "\uD800", CancellationToken.None).AsTask());

        Assert.Empty(stream.ToArray());
        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public async Task Session_command_rejects_an_unpaired_low_surrogate_before_writing()
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.WriteSessionCommandAsync(1, "\uDC00", CancellationToken.None).AsTask());

        Assert.Empty(stream.ToArray());
        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public async Task Invalid_control_mode_and_command_are_rejected_before_writing()
    {
        await using var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            writer.WriteSetModeAsync((ArdControlMode)3, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            writer.WriteSessionCommandAsync(3, "alice", CancellationToken.None).AsTask());

        Assert.Empty(stream.ToArray());
        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public async Task Constructor_and_session_command_reject_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ArdClientMessageWriter(null!));

        var writer = new ArdClientMessageWriter(new RfbWriter(new MemoryStream()));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            writer.WriteSessionCommandAsync(0, null!, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Successful_write_does_not_flush_or_dispose_the_stream()
    {
        var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));

        await writer.WriteViewerInfoAsync(CancellationToken.None);

        Assert.Equal(1, stream.WriteCount);
        Assert.Equal(0, stream.FlushCount);
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task Cancellation_propagates_without_writing()
    {
        var stream = new TrackingMemoryStream();
        var writer = new ArdClientMessageWriter(new RfbWriter(stream));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.WriteViewerInfoAsync(cancellation.Token).AsTask());

        Assert.Equal(0, stream.FlushCount);
        Assert.Equal(0, stream.DisposeCount);
        Assert.Equal(0, stream.WriteCount);
        Assert.Empty(stream.ToArray());
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public int DisposeCount { get; private set; }
        public int FlushCount { get; private set; }
        public int WriteCount { get; private set; }

        public override void Flush() => FlushCount++;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return base.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
