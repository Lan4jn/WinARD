using WinARD.Application.Ports;

namespace WinARD.Desktop.Rendering;

internal delegate void FramePresentOperation(
    ReadOnlySpan<byte> bgra32,
    int stride,
    IReadOnlyList<RemoteRectangle> dirtyRectangles);

internal static class D3DPresentationRecovery
{
    public static void Execute(
        ReadOnlySpan<byte> bgra32,
        int stride,
        IReadOnlyList<RemoteRectangle> dirtyRectangles,
        FramePresentOperation presentOptimized,
        Func<bool> deviceRemoved,
        Action rebuildSwapChain,
        FramePresentOperation presentFullFrame)
    {
        ArgumentNullException.ThrowIfNull(dirtyRectangles);
        ArgumentNullException.ThrowIfNull(presentOptimized);
        ArgumentNullException.ThrowIfNull(deviceRemoved);
        ArgumentNullException.ThrowIfNull(rebuildSwapChain);
        ArgumentNullException.ThrowIfNull(presentFullFrame);

        try
        {
            presentOptimized(bgra32, stride, dirtyRectangles);
        }
        catch (D3DPresentationException exception)
            when (exception.IsPresent1InvalidCall && !deviceRemoved())
        {
            rebuildSwapChain();
            presentFullFrame(bgra32, stride, dirtyRectangles);
        }
    }
}
