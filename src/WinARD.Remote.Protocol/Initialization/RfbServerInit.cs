using WinARD.Remote.Protocol.Framebuffer;

namespace WinARD.Remote.Protocol.Initialization;

public sealed record RfbServerInit(
    int Width,
    int Height,
    PixelFormat PixelFormat,
    string Name,
    bool IsNameTruncated);
