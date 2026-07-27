using WinARD.Application.Ports;
using WinARD.Desktop.Rendering;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests;

public sealed class D3DFramePresenterSourceContractTests
{
    [Fact]
    public void Present_clipping_returns_empty_for_empty_rectangles()
    {
        var clipped = D3DFramePresenter.ClipPresentRectangles([], 10, 10);

        Assert.Empty(clipped);
    }

    [Fact]
    public void Present_clipping_returns_empty_when_all_rectangles_are_outside_frame()
    {
        var clipped = D3DFramePresenter.ClipPresentRectangles(
            [new RemoteRectangle(20, 20, 5, 5)],
            10,
            10);

        Assert.Empty(clipped);
    }

    [Fact]
    public void PresentCore_returns_for_empty_clipping_before_GPU_or_COM_access()
    {
        var source = PresenterSource();
        var method = MethodSource(
            source,
            "    private void PresentCore(",
            "    private void CreateDevice()");
        var clip = method.IndexOf(
            "var clipped = ClipPresentRectangles(",
            StringComparison.Ordinal);
        var emptyCheck = method.IndexOf(
            "if (clipped.Count == 0)",
            StringComparison.Ordinal);
        var emptyReturn = method.IndexOf("return;", emptyCheck, StringComparison.Ordinal);
        var context = method.IndexOf("var context = _context", StringComparison.Ordinal);
        var texture = method.IndexOf("var texture = _texture", StringComparison.Ordinal);
        var swapChain = method.IndexOf("var swapChain = _swapChain", StringComparison.Ordinal);
        var getBuffer = method.IndexOf("swapChain.GetBuffer", StringComparison.Ordinal);

        Assert.True(clip >= 0);
        Assert.True(emptyCheck > clip);
        Assert.True(emptyReturn > emptyCheck);
        Assert.True(context > emptyReturn);
        Assert.True(texture > emptyReturn);
        Assert.True(swapChain > emptyReturn);
        Assert.True(getBuffer > swapChain);
    }

    [Fact]
    public void Swap_chain_factory_stage_selector_maps_normal_and_recovery_paths()
    {
        Assert.Equal(
            D3DPresentationStage.CreateSwapChainForComposition,
            D3DFramePresenter.GetSwapChainCreationStage(recovery: false));
        Assert.Equal(
            D3DPresentationStage.RecoveryCreateSwapChain,
            D3DFramePresenter.GetSwapChainCreationStage(recovery: true));
    }

    [Fact]
    public void Native_panel_and_factory_acquisition_calls_are_wrapped_with_allowed_stages()
    {
        var source = PresenterSource();
        var createDevice = MethodSource(
            source,
            "    private void CreateDevice()",
            "    private void CreateFrameResources(");
        var createFrameResources = MethodSource(
            source,
            "    private void CreateFrameResources(",
            "    private void RecreateDevice()");

        AssertWrappedCall(
            createDevice,
            "new SwapChainPanelNative(_panel)",
            "D3DPresentationStage.SetSwapChain");
        AssertWrappedCall(
            createFrameResources,
            "device.QueryInterface<IDXGIDevice>()",
            "swapChainStage");
        AssertWrappedCall(
            createFrameResources,
            "dxgiDevice.GetAdapter",
            "swapChainStage");
        AssertWrappedCall(
            createFrameResources,
            "adapter.GetParent<IDXGIFactory2>",
            "swapChainStage");
    }

    private static string PresenterSource() =>
        File.ReadAllText(RepositoryFile(
            "src",
            "WinARD.Desktop",
            "Rendering",
            "D3DFramePresenter.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string MethodSource(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.True(end > start);
        return source[start..end];
    }

    private static void AssertWrappedCall(
        string methodSource,
        string callMarker,
        string stageMarker)
    {
        var call = methodSource.IndexOf(callMarker, StringComparison.Ordinal);
        Assert.True(call >= 0);
        var wrapper = methodSource.LastIndexOf(
            "D3DPresentationOperation.Run(",
            call,
            StringComparison.Ordinal);
        Assert.True(wrapper >= 0);
        Assert.Contains(
            stageMarker,
            methodSource[wrapper..call],
            StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException("Could not locate repository root.")
            : Path.Combine([directory.FullName, .. segments]);
    }
}
