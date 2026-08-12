using System.Collections.ObjectModel;
using WinARD.Remote.Protocol.Framebuffer;

namespace WinARD.Remote.Protocol.Encodings;

public readonly record struct RectangleTransferStatistics(
    int EncodingId,
    long WirePayloadBytes,
    long PixelWireBytes,
    long PixelArea,
    bool HasPixelContent);

public sealed class EncodingDecodeResult
{
    public EncodingDecodeResult(
        IEnumerable<FramebufferRect> dirtyRects,
        IEnumerable<FramebufferRect> pixelContentRects)
        : this(dirtyRects, pixelContentRects, null)
    {
    }

    public EncodingDecodeResult(
        IEnumerable<FramebufferRect> dirtyRects,
        IEnumerable<FramebufferRect> pixelContentRects,
        RectangleTransferStatistics? transferStatistics)
    {
        ArgumentNullException.ThrowIfNull(dirtyRects);
        ArgumentNullException.ThrowIfNull(pixelContentRects);
        DirtyRects = new ReadOnlyCollection<FramebufferRect>(dirtyRects.ToArray());
        PixelContentRects = new ReadOnlyCollection<FramebufferRect>(pixelContentRects.ToArray());
        TransferStatistics = transferStatistics;
    }

    public static EncodingDecodeResult Empty { get; } = new([], []);

    public IReadOnlyList<FramebufferRect> DirtyRects { get; }

    public IReadOnlyList<FramebufferRect> PixelContentRects { get; }

    public RectangleTransferStatistics? TransferStatistics { get; }
}
