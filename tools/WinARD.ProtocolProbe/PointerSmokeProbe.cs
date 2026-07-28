using WinARD.Remote.Protocol.Input;
using WinARD.Remote.Protocol.IO;

namespace WinARD.ProtocolProbe;

internal static class PointerSmokeProbe
{
    public static async Task<ProbePointerSmoke> SendAsync(
        Stream stream,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ushort.MaxValue);

        var x = width / 2;
        var y = height / 2;
        await new PointerEventWriter(new RfbWriter(stream))
            .WriteAsync(0, x, y, cancellationToken)
            .ConfigureAwait(false);
        return new ProbePointerSmoke(width, height, x, y);
    }
}
