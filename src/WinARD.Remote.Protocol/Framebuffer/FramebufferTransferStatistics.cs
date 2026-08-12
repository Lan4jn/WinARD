using System.Collections.ObjectModel;
using WinARD.Remote.Protocol.Encodings;

namespace WinARD.Remote.Protocol.Framebuffer;

public sealed class FramebufferTransferStatistics
{
    public FramebufferTransferStatistics(IEnumerable<RectangleTransferStatistics> rectangles)
    {
        ArgumentNullException.ThrowIfNull(rectangles);
        var rectangleCounts = new Dictionary<int, int>();
        var wirePayloadBytesByEncoding = new Dictionary<int, long>();
        var pixelWireBytesByEncoding = new Dictionary<int, long>();

        foreach (var rectangle in rectangles)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(rectangle.WirePayloadBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(rectangle.PixelWireBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(rectangle.PixelArea);
            if (!rectangle.HasPixelContent)
            {
                continue;
            }

            RectangleCount = SaturatingAdd(RectangleCount, 1);
            WirePayloadBytes = SaturatingAdd(WirePayloadBytes, rectangle.WirePayloadBytes);
            PixelWireBytes = SaturatingAdd(PixelWireBytes, rectangle.PixelWireBytes);
            PixelArea = SaturatingAdd(PixelArea, rectangle.PixelArea);
            rectangleCounts.TryGetValue(rectangle.EncodingId, out var count);
            rectangleCounts[rectangle.EncodingId] = count == int.MaxValue ? count : count + 1;
            wirePayloadBytesByEncoding.TryGetValue(rectangle.EncodingId, out var wireBytes);
            wirePayloadBytesByEncoding[rectangle.EncodingId] =
                SaturatingAdd(wireBytes, rectangle.WirePayloadBytes);
            pixelWireBytesByEncoding.TryGetValue(rectangle.EncodingId, out var pixelWireBytes);
            pixelWireBytesByEncoding[rectangle.EncodingId] =
                SaturatingAdd(pixelWireBytes, rectangle.PixelWireBytes);
        }

        RectangleCounts = Snapshot(rectangleCounts);
        WirePayloadBytesByEncoding = Snapshot(wirePayloadBytesByEncoding);
        PixelWireBytesByEncoding = Snapshot(pixelWireBytesByEncoding);
        BytesPerPixelMilli = PixelArea == 0
            ? null
            : (long)Math.Min(long.MaxValue, decimal.Truncate((decimal)WirePayloadBytes * 1000 / PixelArea));
    }

    public static FramebufferTransferStatistics Empty { get; } = new([]);

    public long RectangleCount { get; }
    public long WirePayloadBytes { get; }
    public long PixelWireBytes { get; }
    public long PixelArea { get; }
    public long? BytesPerPixelMilli { get; }
    public IReadOnlyDictionary<int, int> RectangleCounts { get; }
    public IReadOnlyDictionary<int, long> WirePayloadBytesByEncoding { get; }
    public IReadOnlyDictionary<int, long> PixelWireBytesByEncoding { get; }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static ReadOnlyDictionary<TKey, TValue> Snapshot<TKey, TValue>(
        IDictionary<TKey, TValue> source)
        where TKey : notnull =>
        new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>(source));
}
