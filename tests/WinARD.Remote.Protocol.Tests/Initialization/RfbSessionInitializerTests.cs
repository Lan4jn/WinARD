using System.Buffers.Binary;
using System.Text;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Initialization;
using WinARD.Remote.Protocol.IO;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Initialization;

public sealed class RfbSessionInitializerTests
{
    [Fact]
    public async Task Initialize_writes_shared_init_pixel_format_and_encodings_in_order()
    {
        var input = ServerInit(640, 480, PixelFormat.WinArdBgra32, "Studio Mac");
        await using var stream = new ScriptedDuplexStream(input);

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(640, server.Width);
        Assert.Equal(480, server.Height);
        Assert.Equal("Studio Mac", server.Name);
        Assert.Equal(PixelFormat.WinArdBgra32, server.PixelFormat);
        Assert.Equal(ExpectedClientInitialization(), stream.WrittenBytes);
        Assert.Equal(0, stream.FlushCount);
        Assert.False(stream.WasDisposed);
    }

    [Fact]
    public async Task Update_request_uses_exact_big_endian_wire_format()
    {
        await using var stream = new ScriptedDuplexStream([]);

        await RfbSessionInitializer.WriteFramebufferUpdateRequestAsync(
            stream,
            incremental: false,
            x: 1,
            y: 2,
            width: 0x1234,
            height: 0x5678,
            CancellationToken.None);

        Assert.Equal([3, 0, 0, 1, 0, 2, 0x12, 0x34, 0x56, 0x78], stream.WrittenBytes);
        Assert.Equal(0, stream.FlushCount);
        Assert.False(stream.WasDisposed);
    }

    [Fact]
    public async Task Name_uses_replacement_cleans_controls_and_truncates_display()
    {
        var name = new byte[5000];
        Array.Fill(name, (byte)'a');
        name[0] = 0xFF;
        name[1] = (byte)'\r';
        name[2] = (byte)'\n';
        name[3] = 0;
        await using var stream = new ScriptedDuplexStream(ServerInit(1, 1, PixelFormat.WinArdBgra32, name));

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            new ProtocolLimits(6000, 1024),
            CancellationToken.None);

        Assert.Equal(4096, server.Name.Length);
        Assert.StartsWith("�   ", server.Name, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', server.Name);
        Assert.DoesNotContain('\n', server.Name);
        Assert.True(server.IsNameTruncated);
    }

    [Fact]
    public async Task Name_cleans_unicode_line_separators()
    {
        await using var stream = new ScriptedDuplexStream(
            ServerInit(1, 1, PixelFormat.WinArdBgra32, "one\u2028two\u2029three"));

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal("one two three", server.Name);
    }

    [Fact]
    public async Task Name_truncation_does_not_split_surrogate_pair()
    {
        var name = new string('a', 4095) + "😀tail";
        await using var stream = new ScriptedDuplexStream(
            ServerInit(1, 1, PixelFormat.WinArdBgra32, name));

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(4095, server.Name.Length);
        Assert.False(char.IsSurrogate(server.Name[^1]));
        Assert.True(server.IsNameTruncated);
    }

    [Fact]
    public async Task Oversized_name_is_rejected_before_payload_read()
    {
        var header = ServerInitHeader(1, 1, PixelFormat.WinArdBgra32, 1025);
        await using var stream = new ScriptedDuplexStream(header);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                new ProtocolLimits(1024, 1024),
                CancellationToken.None));

        Assert.Contains("1025", exception.Message, StringComparison.Ordinal);
        Assert.Equal(header.Length, stream.ReadPosition);
    }

    [Fact]
    public async Task Truncated_server_init_is_wrapped_as_protocol_failure()
    {
        await using var stream = new ScriptedDuplexStream([0, 1, 0]);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(stream, ProtocolLimits.Default, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task Zero_server_dimensions_are_rejected(ushort width, ushort height)
    {
        await using var stream = new ScriptedDuplexStream(ServerInit(width, height, PixelFormat.WinArdBgra32, "x"));

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(stream, ProtocolLimits.Default, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(InvalidPixelFormats))]
    public async Task Invalid_server_pixel_format_is_rejected(byte[] pixelFormat)
    {
        await using var stream = new ScriptedDuplexStream(ServerInit(1, 1, pixelFormat, "x"));

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(stream, ProtocolLimits.Default, CancellationToken.None));
    }

    [Fact]
    public async Task Initialize_propagates_asynchronous_cancellation()
    {
        await using var stream = new BlockingDuplexStream();
        using var cancellation = new CancellationTokenSource();

        var task = RfbSessionInitializer.InitializeAsync(stream, ProtocolLimits.Default, cancellation.Token);
        await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, stream.ReceivedCancellationToken);
    }

    public static TheoryData<byte[]> InvalidPixelFormats => new()
    {
        PixelFormatBytes(24, 24, 0, 1, 255, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 0, 0, 1, 255, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 24, 2, 1, 255, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 24, 0, 0, 255, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 24, 0, 1, 0, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 24, 0, 1, 250, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 24, 0, 1, 255, 255, 255, 28, 8, 0),
        PixelFormatBytes(32, 24, 0, 1, 255, 255, 255, 8, 8, 0),
    };

    private static byte[] ExpectedClientInitialization() =>
    [
        1,
        0, 0, 0, 0,
        32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0,
        2, 0, 0, 5,
        0, 0, 0, 16,
        0, 0, 0, 0,
        0, 0, 0, 1,
        0xFF, 0xFF, 0xFF, 0x11,
        0xFF, 0xFF, 0xFF, 0x21,
    ];

    private static byte[] ServerInit(ushort width, ushort height, PixelFormat format, string name) =>
        ServerInit(width, height, format.ToWireBytes(), Encoding.UTF8.GetBytes(name));

    private static byte[] ServerInit(ushort width, ushort height, PixelFormat format, byte[] name) =>
        ServerInit(width, height, format.ToWireBytes(), name);

    private static byte[] ServerInit(ushort width, ushort height, byte[] format, string name) =>
        ServerInit(width, height, format, Encoding.UTF8.GetBytes(name));

    private static byte[] ServerInit(ushort width, ushort height, byte[] format, byte[] name)
    {
        var header = ServerInitHeader(width, height, format, checked((uint)name.Length));
        return [.. header, .. name];
    }

    private static byte[] ServerInitHeader(ushort width, ushort height, PixelFormat format, uint nameLength) =>
        ServerInitHeader(width, height, format.ToWireBytes(), nameLength);

    private static byte[] ServerInitHeader(ushort width, ushort height, byte[] format, uint nameLength)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), height);
        format.CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), nameLength);
        return bytes;
    }

    private static byte[] PixelFormatBytes(
        byte bitsPerPixel,
        byte depth,
        byte bigEndian,
        byte trueColor,
        ushort redMax,
        ushort greenMax,
        ushort blueMax,
        byte redShift,
        byte greenShift,
        byte blueShift)
    {
        var bytes = new byte[16];
        bytes[0] = bitsPerPixel;
        bytes[1] = depth;
        bytes[2] = bigEndian;
        bytes[3] = trueColor;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), redMax);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), greenMax);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), blueMax);
        bytes[10] = redShift;
        bytes[11] = greenShift;
        bytes[12] = blueShift;
        return bytes;
    }

    private sealed class ScriptedDuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly MemoryStream _output = new();

        public byte[] WrittenBytes => _output.ToArray();
        public long ReadPosition => _input.Position;
        public int FlushCount { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => FlushCount++;
        public override Task FlushAsync(CancellationToken cancellationToken) { FlushCount++; return Task.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _output.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            _input.Dispose();
            _output.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingDuplexStream : Stream
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;
        public CancellationToken ReceivedCancellationToken { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReceivedCancellationToken = cancellationToken;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
