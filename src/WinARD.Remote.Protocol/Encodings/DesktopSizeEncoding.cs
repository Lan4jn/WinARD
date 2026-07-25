using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class DesktopSizeEncoding : IRfbEncodingDecoder
{
    public int EncodingId => (int)RfbEncodingType.DesktopSize;

    public ValueTask<EncodingDecodeResult> DecodeAsync(
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

        var framebufferBytes = PixelConverter.CheckedBgraLength(
            rectangle.Width,
            rectangle.Height,
            framebuffer.Limits);
        reader.ReserveDesktopSizeRectangle();
        reader.ReserveFramebufferUpdateWorkBytes(framebufferBytes);
        framebuffer.Resize(rectangle.Width, rectangle.Height);
        return ValueTask.FromResult(new EncodingDecodeResult([rectangle], []));
    }
}
