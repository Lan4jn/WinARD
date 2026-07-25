using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class DesktopSizeEncoding : IRfbEncodingDecoder
{
    public int EncodingId => (int)RfbEncodingType.DesktopSize;

    public async ValueTask<IReadOnlyList<FramebufferRect>> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        var result = await DecodeWithContextAsync(
            reader,
            framebuffer,
            checked((ushort)rectangle.X),
            checked((ushort)rectangle.Y),
            checked((ushort)rectangle.Width),
            checked((ushort)rectangle.Height),
            PixelFormat.WinArdBgra32,
            cancellationToken);
        return result.DirtyRect is { } dirtyRect ? new[] { dirtyRect } : Array.Empty<FramebufferRect>();
    }

    internal static ValueTask<EncodingDecodeResult> DecodeWithContextAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        ushort x,
        ushort y,
        ushort width,
        ushort height,
        PixelFormat pixelFormat,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (x != 0 || y != 0)
        {
            throw new RfbProtocolException("DesktopSize rectangle origin must be (0,0).");
        }

        framebuffer.Resize(width, height);
        return ValueTask.FromResult(new EncodingDecodeResult(new FramebufferRect(0, 0, width, height), DesktopResized: true));
    }
}
