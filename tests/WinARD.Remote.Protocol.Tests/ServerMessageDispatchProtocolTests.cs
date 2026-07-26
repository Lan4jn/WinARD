using System.Buffers.Binary;
using System.Text;
using WinARD.Remote.Protocol.Clipboard;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests;

public sealed class ServerMessageDispatchProtocolTests
{
    [Fact]
    public async Task Framebuffer_body_can_be_applied_after_dispatcher_consumes_type()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        await using var session = FramebufferUpdateReader.CreateSession(
            framebuffer,
            PixelFormat.WinArdBgra32);
        await using var stream = new MemoryStream(
            [0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 4, 3, 2, 1]);
        Assert.Equal(0, stream.ReadByte());

        var result = await session.ApplyBodyAsync(stream, CancellationToken.None);

        Assert.Equal(0xff020304u, framebuffer.GetBgra32(0, 0));
        Assert.Equal(new FramebufferRect(0, 0, 1, 1), Assert.Single(result.DirtyRects));
    }

    [Fact]
    public async Task Clipboard_body_can_be_read_after_dispatcher_consumes_type()
    {
        var payload = Encoding.UTF8.GetBytes("remote");
        var body = new byte[7 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(3), checked((uint)payload.Length));
        payload.CopyTo(body, 7);
        await using var stream = new MemoryStream(body);

        var text = await ClipboardProtocol.ReadServerCutTextBodyAsync(
            new RfbReader(stream, ProtocolLimits.Default),
            CancellationToken.None);

        Assert.Equal("remote", text);
    }
}
