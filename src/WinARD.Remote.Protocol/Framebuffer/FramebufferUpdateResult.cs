using System.Collections.ObjectModel;

namespace WinARD.Remote.Protocol.Framebuffer;

public sealed class FramebufferUpdateResult
{
    public FramebufferUpdateResult(
        IEnumerable<FramebufferRect> dirtyRects,
        IEnumerable<FramebufferRect> pixelContentRects,
        RemoteCursor? cursor,
        bool desktopResized)
    {
        ArgumentNullException.ThrowIfNull(dirtyRects);
        ArgumentNullException.ThrowIfNull(pixelContentRects);
        DirtyRects = new ReadOnlyCollection<FramebufferRect>(dirtyRects.ToArray());
        PixelContentRects = new ReadOnlyCollection<FramebufferRect>(pixelContentRects.ToArray());
        Cursor = cursor;
        DesktopResized = desktopResized;
    }

    public IReadOnlyList<FramebufferRect> DirtyRects { get; }
    public IReadOnlyList<FramebufferRect> PixelContentRects { get; }
    public RemoteCursor? Cursor { get; }
    public bool DesktopResized { get; }
}
