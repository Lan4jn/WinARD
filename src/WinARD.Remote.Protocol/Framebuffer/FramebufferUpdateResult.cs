using System.Collections.ObjectModel;

namespace WinARD.Remote.Protocol.Framebuffer;

public sealed class FramebufferUpdateResult
{
    public FramebufferUpdateResult(
        IEnumerable<FramebufferRect> dirtyRects,
        RemoteCursor? cursor,
        bool desktopResized)
    {
        ArgumentNullException.ThrowIfNull(dirtyRects);
        DirtyRects = new ReadOnlyCollection<FramebufferRect>(dirtyRects.ToArray());
        Cursor = cursor;
        DesktopResized = desktopResized;
    }

    public IReadOnlyList<FramebufferRect> DirtyRects { get; }
    public RemoteCursor? Cursor { get; }
    public bool DesktopResized { get; }
}
