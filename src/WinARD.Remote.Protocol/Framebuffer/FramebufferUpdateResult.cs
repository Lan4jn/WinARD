using System.Collections.ObjectModel;

namespace WinARD.Remote.Protocol.Framebuffer;

public sealed class FramebufferUpdateResult
{
    public FramebufferUpdateResult(
        IEnumerable<FramebufferRect> dirtyRects,
        IEnumerable<FramebufferRect> pixelContentRects,
        RemoteCursor? cursor,
        bool desktopResized)
        : this(dirtyRects, pixelContentRects, cursor, desktopResized, new Dictionary<int, int>())
    {
    }

    public FramebufferUpdateResult(
        IEnumerable<FramebufferRect> dirtyRects,
        IEnumerable<FramebufferRect> pixelContentRects,
        RemoteCursor? cursor,
        bool desktopResized,
        IReadOnlyDictionary<int, int> encodingCounts)
    {
        ArgumentNullException.ThrowIfNull(dirtyRects);
        ArgumentNullException.ThrowIfNull(pixelContentRects);
        ArgumentNullException.ThrowIfNull(encodingCounts);
        DirtyRects = new ReadOnlyCollection<FramebufferRect>(dirtyRects.ToArray());
        PixelContentRects = new ReadOnlyCollection<FramebufferRect>(pixelContentRects.ToArray());
        Cursor = cursor;
        DesktopResized = desktopResized;
        EncodingCounts = new ReadOnlyDictionary<int, int>(new Dictionary<int, int>(encodingCounts));
    }

    public IReadOnlyList<FramebufferRect> DirtyRects { get; }
    public IReadOnlyList<FramebufferRect> PixelContentRects { get; }
    public RemoteCursor? Cursor { get; }
    public bool DesktopResized { get; }
    public IReadOnlyDictionary<int, int> EncodingCounts { get; }
}
