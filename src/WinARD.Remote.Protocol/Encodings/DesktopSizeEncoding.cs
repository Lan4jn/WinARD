using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class DesktopSizeEncoding : IRfbEncodingDecoder
{
    public int EncodingId => (int)RfbEncodingType.DesktopSize;

    public ValueTask<IReadOnlyList<FramebufferRect>> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (rectangle.X != 0 || rectangle.Y != 0)
        {
            throw new RfbProtocolException("DesktopSize rectangle origin must be (0,0).");
        }

        framebuffer.Resize(rectangle.Width, rectangle.Height);
        return ValueTask.FromResult<IReadOnlyList<FramebufferRect>>(new[] { rectangle });
    }
}
