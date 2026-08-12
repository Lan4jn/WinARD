using WinARD.Remote.Protocol.Framebuffer;

namespace WinARD.Remote.Protocol.Encodings;

internal interface IReconfigurablePixelFormatDecoder
{
    void ValidatePixelFormat(PixelFormat pixelFormat);

    void CommitPixelFormat(PixelFormat pixelFormat);
}
