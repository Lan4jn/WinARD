using System.Buffers.Binary;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Ard;

public sealed class ArdDisplayBootstrapReaderTests
{
    [Fact]
    public async Task Ack_nop_and_display_info_return_payload_size_with_exact_consumption()
    {
        var input = new List<byte> { 0x04, 0x07 };
        input.AddRange(FramebufferUpdate(
            DisplayInfoRectangle(320, 200, 1440, 900, displayCount: 2, flags: 0x1234, fill: 0xA5)));
        var expectedBytesRead = input.Count;
        input.Add(0xEE);
        var stream = new TrackingDuplexStream(input.ToArray());

        var size = await ArdDisplayBootstrapReader.ReadAsync(
            stream,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(new ArdDisplaySize(1440, 900), size);
        Assert.Equal(expectedBytesRead, stream.BytesRead);
        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public async Task Display_info_two_returns_rectangle_size_and_consumes_size_prefixed_payload()
    {
        var message = FramebufferUpdate(
            DisplayInfo2Rectangle(11, 22, 1920, 1080, [0x10, 0x20, 0x30, 0x40]));
        var input = new byte[message.Length + 1];
        message.CopyTo(input, 0);
        input[^1] = 0xEE;
        var stream = new TrackingDuplexStream(input);

        var size = await ArdDisplayBootstrapReader.ReadAsync(
            stream,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(new ArdDisplaySize(1920, 1080), size);
        Assert.Equal(message.Length, stream.BytesRead);
    }

    [Fact]
    public async Task Desktop_size_returns_rectangle_size()
    {
        var message = FramebufferUpdate(DesktopSizeRectangle(2560, 1440));
        var stream = new TrackingDuplexStream(message);

        var size = await ArdDisplayBootstrapReader.ReadAsync(
            stream,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(new ArdDisplaySize(2560, 1440), size);
        Assert.Equal(message.Length, stream.BytesRead);
    }

    [Fact]
    public async Task Desktop_size_with_nonzero_origin_is_malformed()
    {
        var rectangle = RectangleHeader(1, 2, 2560, 1440, (int)RfbEncodingType.DesktopSize);
        var stream = new TrackingDuplexStream(FramebufferUpdate(rectangle));

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, ProtocolLimits.Default, CancellationToken.None));

        AssertSafe(exception);
    }

    [Fact]
    public async Task First_nonzero_size_is_returned_only_after_the_whole_framebuffer_update_is_consumed()
    {
        var message = FramebufferUpdate(
            DesktopSizeRectangle(1280, 720),
            DisplayInfo2Rectangle(0, 0, 3840, 2160, [0xDE, 0xAD, 0xBE, 0xEF]));
        var input = new byte[message.Length + 1];
        message.CopyTo(input, 0);
        input[^1] = 0xEE;
        var stream = new TrackingDuplexStream(input);

        var size = await ArdDisplayBootstrapReader.ReadAsync(
            stream,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(new ArdDisplaySize(1280, 720), size);
        Assert.Equal(message.Length, stream.BytesRead);
    }

    [Fact]
    public async Task Zero_size_update_is_skipped_until_a_later_nonzero_size()
    {
        var first = FramebufferUpdate(
            DesktopSizeRectangle(0, 0),
            DisplayInfoRectangle(0, 0, 0, 0));
        var second = FramebufferUpdate(DesktopSizeRectangle(1024, 768));
        var stream = new TrackingDuplexStream([.. first, .. second]);

        var size = await ArdDisplayBootstrapReader.ReadAsync(
            stream,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(new ArdDisplaySize(1024, 768), size);
        Assert.Equal(first.Length + second.Length, stream.BytesRead);
    }

    [Fact]
    public async Task Unknown_top_level_message_is_malformed_without_reading_its_payload()
    {
        var stream = new TrackingDuplexStream([0x02, 0x53, 0x45, 0x43, 0x52, 0x45, 0x54]);

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, ProtocolLimits.Default, CancellationToken.None));

        AssertSafe(exception);
        Assert.Equal(1, stream.BytesRead);
    }

    [Fact]
    public async Task Unknown_rectangle_encoding_is_malformed_without_reading_unknown_payload()
    {
        var rectangle = RectangleHeader(1, 2, 3, 4, 0x01020304);
        var message = FramebufferUpdate([.. rectangle, 0x53, 0x45, 0x43, 0x52, 0x45, 0x54]);
        var stream = new TrackingDuplexStream(message);

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, ProtocolLimits.Default, CancellationToken.None));

        AssertSafe(exception);
        Assert.Equal(16, stream.BytesRead);
    }

    [Fact]
    public async Task Sixty_four_zero_only_messages_are_malformed_without_reading_the_sixty_fifth()
    {
        var zeroSizeUpdate = FramebufferUpdate(DesktopSizeRectangle(0, 0));
        var input = new List<byte>();
        for (var messageIndex = 0; messageIndex < 64; messageIndex++)
        {
            input.AddRange(zeroSizeUpdate);
        }

        var acceptedInputLength = input.Count;
        input.AddRange(FramebufferUpdate(DesktopSizeRectangle(800, 600)));
        var stream = new TrackingDuplexStream(input.ToArray());

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, ProtocolLimits.Default, CancellationToken.None));

        AssertSafe(exception);
        Assert.Equal(acceptedInputLength, stream.BytesRead);
    }

    [Fact]
    public async Task Sixty_third_ack_or_nop_can_be_followed_by_a_valid_sixty_fourth_message()
    {
        var input = new List<byte>();
        for (var messageIndex = 0; messageIndex < 63; messageIndex++)
        {
            input.Add(messageIndex % 2 == 0 ? (byte)0x04 : (byte)0x07);
        }

        input.AddRange(FramebufferUpdate(DesktopSizeRectangle(800, 600)));
        var stream = new TrackingDuplexStream(input.ToArray());

        var size = await ArdDisplayBootstrapReader.ReadAsync(
            stream,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(new ArdDisplaySize(800, 600), size);
        Assert.Equal(input.Count, stream.BytesRead);
    }

    [Fact]
    public async Task More_than_four_thousand_ninety_six_rectangles_are_rejected_before_any_rectangle_header()
    {
        var message = new List<byte> { 0x00, 0x00 };
        AddUInt16(message, 4097);
        message.AddRange(DesktopSizeRectangle(800, 600));
        var stream = new TrackingDuplexStream(message.ToArray());

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, ProtocolLimits.Default, CancellationToken.None));

        AssertSafe(exception);
        Assert.Equal(4, stream.BytesRead);
    }

    [Fact]
    public async Task Budget_equal_to_the_complete_framebuffer_update_length_is_accepted()
    {
        var message = FramebufferUpdate(DesktopSizeRectangle(800, 600));
        var stream = new TrackingDuplexStream(message);

        var size = await ArdDisplayBootstrapReader.ReadAsync(
            stream,
            Limits(message.Length),
            CancellationToken.None);

        Assert.Equal(new ArdDisplaySize(800, 600), size);
        Assert.Equal(message.Length, stream.BytesRead);
    }

    [Fact]
    public async Task Budget_one_byte_below_the_complete_framebuffer_update_length_is_malformed()
    {
        var message = FramebufferUpdate(DesktopSizeRectangle(800, 600));
        var stream = new TrackingDuplexStream(message);

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, Limits(message.Length - 1), CancellationToken.None));

        AssertSafe(exception);
        Assert.Equal(4, stream.BytesRead);
    }

    [Fact]
    public async Task Display_info_display_count_product_over_the_budget_is_malformed()
    {
        var rectangle = new List<byte>(RectangleHeader(0, 0, 1, 1, (int)RfbEncodingType.ArdDisplayInfo));
        AddUInt16(rectangle, 640);
        AddUInt16(rectangle, 480);
        AddUInt16(rectangle, ushort.MaxValue);
        AddUInt16(rectangle, 0);
        var stream = new TrackingDuplexStream(FramebufferUpdate(rectangle.ToArray()));

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, Limits(1024), CancellationToken.None));

        AssertSafe(exception);
        Assert.Equal(24, stream.BytesRead);
    }

    [Fact]
    public async Task Cumulative_framebuffer_update_payload_over_the_budget_is_malformed()
    {
        var first = DisplayInfo2Rectangle(0, 0, 1, 1, new byte[12]);
        var second = DisplayInfo2Rectangle(0, 0, 2, 2, new byte[12]);
        var stream = new TrackingDuplexStream(FramebufferUpdate(first, second));

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, Limits(50), CancellationToken.None));

        AssertSafe(exception);
    }

    [Fact]
    public async Task Display_info_two_payload_size_over_the_budget_is_malformed_without_reading_payload()
    {
        var rectangle = new List<byte>(RectangleHeader(0, 0, 1, 1, (int)RfbEncodingType.ArdDisplayInfo2));
        AddUInt16(rectangle, 64);
        rectangle.AddRange(Enumerable.Repeat((byte)0x5A, 64));
        var stream = new TrackingDuplexStream(FramebufferUpdate(rectangle.ToArray()));

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, Limits(32), CancellationToken.None));

        AssertSafe(exception);
        Assert.Equal(18, stream.BytesRead);
    }

    [Fact]
    public async Task Rectangle_headers_over_the_budget_are_rejected_before_reading_any_rectangle()
    {
        var message = new List<byte> { 0x00, 0x00 };
        AddUInt16(message, 3);
        message.AddRange(new byte[36]);
        var stream = new TrackingDuplexStream(message.ToArray());

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, Limits(32), CancellationToken.None));

        AssertSafe(exception);
        Assert.Equal(4, stream.BytesRead);
    }

    [Theory]
    [MemberData(nameof(TruncatedMessages))]
    public async Task Truncated_message_is_wrapped_as_safe_malformed(byte[] message)
    {
        var stream = new TrackingDuplexStream(message);

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, ProtocolLimits.Default, CancellationToken.None));

        AssertSafe(exception);
    }

    [Fact]
    public async Task Null_arguments_fail_before_io()
    {
        var stream = new TrackingDuplexStream(Array.Empty<byte>());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(null!, ProtocolLimits.Default, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, null!, CancellationToken.None));

        Assert.Equal(0, stream.ReadCount);
        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public async Task Pre_cancelled_operation_fails_before_io()
    {
        var stream = new TrackingDuplexStream(FramebufferUpdate(DesktopSizeRectangle(800, 600)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArdDisplayBootstrapReader.ReadAsync(stream, ProtocolLimits.Default, cancellation.Token));

        Assert.Equal(0, stream.ReadCount);
        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public async Task Cancellation_interrupts_a_blocked_read()
    {
        var stream = new TrackingDuplexStream(Array.Empty<byte>(), blockWhenInputEnds: true);
        using var cancellation = new CancellationTokenSource();

        var reading = ArdDisplayBootstrapReader.ReadAsync(
            stream,
            ProtocolLimits.Default,
            cancellation.Token);
        await stream.ReadObserved.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public async Task Reading_does_not_write_flush_or_dispose_the_stream()
    {
        var stream = new TrackingDuplexStream(FramebufferUpdate(DesktopSizeRectangle(800, 600)));

        await ArdDisplayBootstrapReader.ReadAsync(stream, ProtocolLimits.Default, CancellationToken.None);

        Assert.Equal(0, stream.WriteCount);
        Assert.Equal(0, stream.FlushCount);
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public void Ard_display_encoding_values_match_the_big_endian_wire_values()
    {
        Assert.Equal(1101, (int)RfbEncodingType.ArdDisplayInfo);
        Assert.Equal(1105, (int)RfbEncodingType.ArdDisplayInfo2);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x04, 0x4D }, RectangleHeader(0, 0, 0, 0, (int)RfbEncodingType.ArdDisplayInfo)[8..]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x04, 0x51 }, RectangleHeader(0, 0, 0, 0, (int)RfbEncodingType.ArdDisplayInfo2)[8..]);
    }

    public static TheoryData<byte[]> TruncatedMessages => new()
    {
        Array.Empty<byte>(),
        new byte[] { 0x00 },
        new byte[] { 0x00, 0x00, 0x00 },
        new byte[] { 0x00, 0x00, 0x00, 0x01, 0x00 },
        FramebufferUpdate(RectangleHeader(0, 0, 1, 1, (int)RfbEncodingType.ArdDisplayInfo)),
        FramebufferUpdate([.. RectangleHeader(0, 0, 1, 1, (int)RfbEncodingType.ArdDisplayInfo), 0x00, 0x01]),
        FramebufferUpdate([.. RectangleHeader(0, 0, 1, 1, (int)RfbEncodingType.ArdDisplayInfo2), 0x00]),
        FramebufferUpdate(
            [
                .. RectangleHeader(0, 0, 1, 1, (int)RfbEncodingType.ArdDisplayInfo2),
                0x00,
                0x07,
                0x53,
                0x45,
                0x43,
                0x52,
                0x45,
                0x54,
            ]),
    };

    private static void AssertSafe(ArdSessionMalformedException exception)
    {
        Assert.Equal("Apple Remote Desktop session response was malformed.", exception.Message);
        Assert.DoesNotContain("SECRET", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(exception.InnerException);
    }

    private static ProtocolLimits Limits(int budget) => new(budget, budget, budget);

    private static byte[] FramebufferUpdate(params byte[][] rectangles)
    {
        var message = new List<byte> { 0x00, 0x00 };
        AddUInt16(message, checked((ushort)rectangles.Length));
        foreach (var rectangle in rectangles)
        {
            message.AddRange(rectangle);
        }

        return message.ToArray();
    }

    private static byte[] DesktopSizeRectangle(ushort width, ushort height) =>
        RectangleHeader(0, 0, width, height, (int)RfbEncodingType.DesktopSize);

    private static byte[] DisplayInfoRectangle(
        ushort x,
        ushort y,
        ushort rectangleWidth,
        ushort rectangleHeight,
        ushort displayCount = 0,
        ushort flags = 0,
        byte fill = 0)
    {
        var rectangle = new List<byte>(RectangleHeader(
            x,
            y,
            rectangleWidth,
            rectangleHeight,
            (int)RfbEncodingType.ArdDisplayInfo));
        AddUInt16(rectangle, rectangleWidth);
        AddUInt16(rectangle, rectangleHeight);
        AddUInt16(rectangle, displayCount);
        AddUInt16(rectangle, flags);
        rectangle.AddRange(Enumerable.Repeat(fill, checked(displayCount * 28)));
        return rectangle.ToArray();
    }

    private static byte[] DisplayInfo2Rectangle(
        ushort x,
        ushort y,
        ushort width,
        ushort height,
        byte[] payload)
    {
        var rectangle = new List<byte>(RectangleHeader(
            x,
            y,
            width,
            height,
            (int)RfbEncodingType.ArdDisplayInfo2));
        AddUInt16(rectangle, checked((ushort)payload.Length));
        rectangle.AddRange(payload);
        return rectangle.ToArray();
    }

    private static byte[] RectangleHeader(ushort x, ushort y, ushort width, ushort height, int encoding)
    {
        var rectangle = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(rectangle, x);
        BinaryPrimitives.WriteUInt16BigEndian(rectangle.AsSpan(2), y);
        BinaryPrimitives.WriteUInt16BigEndian(rectangle.AsSpan(4), width);
        BinaryPrimitives.WriteUInt16BigEndian(rectangle.AsSpan(6), height);
        BinaryPrimitives.WriteInt32BigEndian(rectangle.AsSpan(8), encoding);
        return rectangle;
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)(value & byte.MaxValue));
    }

    private sealed class TrackingDuplexStream : Stream
    {
        private readonly bool _blockWhenInputEnds;
        private readonly MemoryStream _input;
        private readonly TaskCompletionSource _readObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TrackingDuplexStream(byte[] input, bool blockWhenInputEnds = false)
        {
            _input = new MemoryStream(input, writable: false);
            _blockWhenInputEnds = blockWhenInputEnds;
        }

        public int BytesRead { get; private set; }
        public int DisposeCount { get; private set; }
        public int FlushCount { get; private set; }
        public int ReadCount { get; private set; }
        public Task ReadObserved => _readObserved.Task;
        public int WriteCount { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => FlushCount++;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            _readObserved.TrySetResult();
            if (_input.Position < _input.Length)
            {
                var read = _input.Read(buffer.Span);
                BytesRead += read;
                return ValueTask.FromResult(read);
            }

            return _blockWhenInputEnds
                ? new ValueTask<int>(WaitForCancellationAsync(cancellationToken))
                : ValueTask.FromResult(0);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteCount++;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            if (disposing)
            {
                _input.Dispose();
            }

            base.Dispose(disposing);
        }

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
