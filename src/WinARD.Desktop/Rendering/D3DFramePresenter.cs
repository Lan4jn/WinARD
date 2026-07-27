using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WinUI;
using WinARD.Application.Ports;
using static Vortice.Direct3D11.D3D11;
using SwapChainPanelNative = Vortice.WinUI.ISwapChainPanelNative;

namespace WinARD.Desktop.Rendering;

public sealed class D3DFramePresenter : IFramePresenter
{
    private readonly SwapChainPanel _panel;
    private SwapChainPanelNative? _panelNative;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _texture;
    private IDXGISwapChain1? _swapChain;
    private int _width;
    private int _height;
    private bool _disposed;

    public D3DFramePresenter(SwapChainPanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
    }

    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_width == width && _height == height && _texture is not null && _swapChain is not null)
        {
            return;
        }

        if (_device is null)
        {
            CreateDevice();
        }

        CreateFrameResources(width, height);
    }

    public void Present(
        ReadOnlySpan<byte> bgra32,
        int stride,
        IReadOnlyList<RemoteRectangle> dirtyRectangles)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dirtyRectangles);
        FrameValidation.ValidateBgra32(_width, _height, stride, bgra32.Length);
        try
        {
            D3DPresentationRecovery.Execute(
                bgra32,
                stride,
                dirtyRectangles,
                PresentOptimized,
                IsDeviceRemoved,
                RebuildSwapChainForRecovery,
                PresentRecoveredFullFrame);
        }
        catch (D3DPresentationException) when (IsDeviceRemoved())
        {
            RecreateDevice();
            CreateFrameResources(_width, _height, recovery: true);
            PresentRecoveredFullFrame(bgra32, stride);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        List<Exception> failures = [];
        CaptureFailure(() => DisposeFrameResources(detachPanel: true), failures);
        CaptureFailure(() => _context?.Dispose(), failures);
        CaptureFailure(() => _device?.Dispose(), failures);
        CaptureFailure(() => _panelNative?.Dispose(), failures);
        _context = null;
        _device = null;
        _panelNative = null;
        if (failures.Count != 0)
        {
            return ValueTask.FromException(
                new AggregateException("D3D frame presenter cleanup failed.", failures));
        }

        return ValueTask.CompletedTask;
    }

    private bool IsDeviceRemoved() => _device?.DeviceRemovedReason.Failure == true;

    private void PresentOptimized(
        ReadOnlySpan<byte> bgra32,
        int stride,
        IReadOnlyList<RemoteRectangle> dirtyRectangles) =>
        PresentCore(
            bgra32,
            stride,
            dirtyRectangles,
            useDirtyRectanglePresent: true,
            recovery: false);

    private void RebuildSwapChainForRecovery() =>
        CreateFrameResources(_width, _height, detachPanel: true, recovery: true);

    private void PresentRecoveredFullFrame(ReadOnlySpan<byte> bgra32, int stride) =>
        PresentCore(
            bgra32,
            stride,
            [new RemoteRectangle(0, 0, _width, _height)],
            useDirtyRectanglePresent: false,
            recovery: true);

    private void PresentCore(
        ReadOnlySpan<byte> bgra32,
        int stride,
        IReadOnlyList<RemoteRectangle> dirtyRectangles,
        bool useDirtyRectanglePresent,
        bool recovery)
    {
        var context = _context ?? throw new InvalidOperationException("The D3D11 device is unavailable.");
        var texture = _texture ?? throw new InvalidOperationException("The D3D11 frame texture is unavailable.");
        var swapChain = _swapChain ?? throw new InvalidOperationException("The DXGI swap chain is unavailable.");
        using var backBuffer = D3DPresentationOperation.Run(
            recovery
                ? D3DPresentationStage.RecoveryGetBuffer
                : D3DPresentationStage.GetBuffer,
            () => swapChain.GetBuffer<ID3D11Texture2D>(0));
        var clipped = FrameValidation.ClipDirtyRectangles(dirtyRectangles, _width, _height);
        if (clipped.Count == 0)
        {
            return;
        }

        var presentRectangles = new RawRect[clipped.Count];
        for (var index = 0; index < clipped.Count; index++)
        {
            var rectangle = clipped[index];
            var sourceOffset = checked((rectangle.Y * stride) + (rectangle.X * 4));
            var source = bgra32[sourceOffset..];
            var box = new Box(
                rectangle.X,
                rectangle.Y,
                0,
                checked(rectangle.X + rectangle.Width),
                checked(rectangle.Y + rectangle.Height),
                1);
            D3DPresentationOperation.Run(
                D3DPresentationStage.UpdateSubresource,
                source,
                bytes => context.UpdateSubresource(
                    bytes,
                    texture,
                    0,
                    checked((uint)stride),
                    0,
                    box));
            D3DPresentationOperation.Run(
                D3DPresentationStage.CopySubresourceRegion,
                () => context.CopySubresourceRegion(
                    backBuffer,
                    0,
                    checked((uint)rectangle.X),
                    checked((uint)rectangle.Y),
                    0,
                    texture,
                    0,
                    box));
            presentRectangles[index] = new RawRect(
                rectangle.X,
                rectangle.Y,
                checked(rectangle.X + rectangle.Width),
                checked(rectangle.Y + rectangle.Height));
        }

        if (useDirtyRectanglePresent)
        {
            D3DPresentationOperation.Run(
                D3DPresentationStage.Present1,
                () => swapChain.Present1(
                    0,
                    PresentFlags.None,
                    presentRectangles,
                    scrollRectangle: null,
                    scrollOffset: null).CheckError());
            return;
        }

        D3DPresentationOperation.Run(
            D3DPresentationStage.RecoveryPresent,
            () => swapChain.Present(0, PresentFlags.None).CheckError());
    }

    private void CreateDevice()
    {
        ID3D11Device device = null!;
        ID3D11DeviceContext context = null!;
        D3DPresentationOperation.Run(
            D3DPresentationStage.CreateDevice,
            () => D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                out device,
                out context).CheckError());
        _device = device;
        _context = context;
        _panelNative = new SwapChainPanelNative(_panel);
    }

    private void CreateFrameResources(
        int width,
        int height,
        bool detachPanel = false,
        bool recovery = false)
    {
        var device = _device ?? throw new InvalidOperationException("The D3D11 device is unavailable.");
        DisposeFrameResources(detachPanel);
        var textureDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            BindFlags.None,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var swapChainDescription = new SwapChainDescription1(
            checked((uint)width),
            checked((uint)height),
            Format.B8G8R8A8_UNorm,
            stereo: false,
            Usage.RenderTargetOutput,
            2,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            AlphaMode.Ignore,
            SwapChainFlags.None);
        var panelNative = _panelNative
            ?? throw new InvalidOperationException("SwapChainPanel native interop is unavailable.");
        var resources = FrameResourceTransaction.Create(
            () => D3DPresentationOperation.Run(
                D3DPresentationStage.CreateTexture2D,
                () => device.CreateTexture2D(textureDescription)),
            () => D3DPresentationOperation.Run(
                recovery
                    ? D3DPresentationStage.RecoveryCreateSwapChain
                    : D3DPresentationStage.CreateSwapChainForComposition,
                () => factory.CreateSwapChainForComposition(
                    device,
                    swapChainDescription,
                    null!)),
            swapChain => D3DPresentationOperation.Run(
                recovery
                    ? D3DPresentationStage.RecoverySetSwapChain
                    : D3DPresentationStage.SetSwapChain,
                () => panelNative.SetSwapChain(swapChain).CheckError()));
        _texture = resources.Texture;
        _swapChain = resources.SwapChain;
        _width = width;
        _height = height;
    }

    private void RecreateDevice()
    {
        DisposeFrameResources(detachPanel: true);
        _context?.Dispose();
        _device?.Dispose();
        _context = null;
        _device = null;
        CreateDevice();
    }

    private void DisposeFrameResources(bool detachPanel)
    {
        List<Exception> failures = [];
        if (detachPanel && _panelNative is not null)
        {
            CaptureFailure(
                () => D3DPresentationOperation.Run(
                    D3DPresentationStage.RecoveryDetachSwapChain,
                    () => _panelNative.SetSwapChain(null!).CheckError()),
                failures);
        }

        CaptureFailure(() => _swapChain?.Dispose(), failures);
        CaptureFailure(() => _texture?.Dispose(), failures);
        _swapChain = null;
        _texture = null;
        if (failures.Count != 0)
        {
            throw new AggregateException("D3D frame resource cleanup failed.", failures);
        }
    }

    private static void CaptureFailure(Action operation, List<Exception> failures)
    {
        try
        {
            operation();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
