using System.Collections.ObjectModel;
using WinARD.Remote.Protocol.Framebuffer;

namespace WinARD.Remote.Protocol.Encodings;

public sealed class EncodingDecodeResult
{
    public EncodingDecodeResult(
        IEnumerable<FramebufferRect> dirtyRects,
        IEnumerable<FramebufferRect> pixelContentRects)
    {
        ArgumentNullException.ThrowIfNull(dirtyRects);
        ArgumentNullException.ThrowIfNull(pixelContentRects);
        DirtyRects = new ReadOnlyCollection<FramebufferRect>(dirtyRects.ToArray());
        PixelContentRects = new ReadOnlyCollection<FramebufferRect>(pixelContentRects.ToArray());
    }

    public static EncodingDecodeResult Empty { get; } = new([], []);

    public IReadOnlyList<FramebufferRect> DirtyRects { get; }

    public IReadOnlyList<FramebufferRect> PixelContentRects { get; }
}
