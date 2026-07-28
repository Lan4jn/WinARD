using WinARD.ProtocolProbe;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Tools;

public sealed class PointerSmokeProbeTests
{
    [Fact]
    public void Pointer_smoke_probe_is_not_public_api()
    {
        Assert.False(typeof(PointerSmokeProbe).IsPublic);
    }

    [Fact]
    public async Task Pointer_smoke_writes_buttonless_move_to_framebuffer_center()
    {
        await using var stream = new MemoryStream();

        var result = await PointerSmokeProbe.SendAsync(stream, 3360, 2100, CancellationToken.None);

        Assert.Equal([5, 0, 0x06, 0x90, 0x04, 0x1A], stream.ToArray());
        Assert.Equal(new ProbePointerSmoke(3360, 2100, 1680, 1050), result);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(65536, 1)]
    [InlineData(1, 65536)]
    public async Task Pointer_smoke_rejects_invalid_dimensions(int width, int height)
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PointerSmokeProbe.SendAsync(stream, width, height, CancellationToken.None));

        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task Pointer_smoke_rejects_null_stream()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            PointerSmokeProbe.SendAsync(null!, 1, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Pointer_smoke_propagates_write_failure_without_returning_result()
    {
        await using var stream = new FailingWriteStream();

        await Assert.ThrowsAsync<IOException>(() =>
            PointerSmokeProbe.SendAsync(stream, 4, 2, CancellationToken.None));
    }

    private sealed class FailingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
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
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Injected pointer write failure."));
    }
}
