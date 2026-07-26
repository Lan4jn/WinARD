using WinARD.Application.Ports;

namespace WinARD.Desktop.Rendering;

public interface IFramePresenter : IAsyncDisposable
{
    void Resize(int width, int height);

    void Present(
        ReadOnlySpan<byte> bgra32,
        int stride,
        IReadOnlyList<RemoteRectangle> dirtyRectangles);
}
