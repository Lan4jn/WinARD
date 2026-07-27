using System.Runtime.ExceptionServices;
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

internal sealed class DeviceResourceCreation<TDevice, TContext>
    where TDevice : class, IDisposable
    where TContext : class, IDisposable
{
    internal TDevice? Device;
    internal TContext? Context;
}

internal readonly record struct DeviceResourceSet<TPanel, TDevice, TContext>(
    TPanel Panel,
    TDevice Device,
    TContext Context)
    where TPanel : class, IDisposable
    where TDevice : class, IDisposable
    where TContext : class, IDisposable;

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

        ExecuteWithDeviceRecovery(
            () => CreateFrameResources(width, height),
            IsDeviceRemoved,
            () =>
            {
                RecreateDevice();
                CreateFrameResources(width, height, recovery: true);
            });
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
        catch (Exception exception)
            when (IsDeviceRecoveryCandidate(exception) && IsDeviceRemoved())
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

    internal static bool IsDeviceRecoveryCandidate(Exception exception) =>
        exception is D3DPresentationException or SharpGenException;

    internal static void ExecuteWithDeviceRecovery(
        Action operation,
        Func<bool> isDeviceRemoved,
        Action recovery)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(isDeviceRemoved);
        ArgumentNullException.ThrowIfNull(recovery);

        try
        {
            operation();
        }
        catch (Exception exception)
            when (IsDeviceRecoveryCandidate(exception) && isDeviceRemoved())
        {
            recovery();
        }
    }

    internal static IReadOnlyList<RemoteRectangle> ClipPresentRectangles(
        IReadOnlyList<RemoteRectangle> dirtyRectangles,
        int width,
        int height) =>
        FrameValidation.ClipDirtyRectangles(dirtyRectangles, width, height);

    internal static D3DPresentationStage GetSwapChainCreationStage(bool recovery) =>
        recovery
            ? D3DPresentationStage.RecoveryCreateSwapChain
            : D3DPresentationStage.CreateSwapChainForComposition;

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
        var clipped = ClipPresentRectangles(dirtyRectangles, _width, _height);
        if (clipped.Count == 0)
        {
            return;
        }

        var context = _context ?? throw new InvalidOperationException("The D3D11 device is unavailable.");
        var texture = _texture ?? throw new InvalidOperationException("The D3D11 frame texture is unavailable.");
        var swapChain = _swapChain ?? throw new InvalidOperationException("The DXGI swap chain is unavailable.");
        using var backBuffer = D3DPresentationOperation.Run(
            recovery
                ? D3DPresentationStage.RecoveryGetBuffer
                : D3DPresentationStage.GetBuffer,
            () => swapChain.GetBuffer<ID3D11Texture2D>(0));

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
        var resources = CreateDeviceResources<
            SwapChainPanelNative,
            ID3D11Device,
            ID3D11DeviceContext>(
            _panelNative,
            () => D3DPresentationOperation.Run(
                D3DPresentationStage.SetSwapChain,
                () => new SwapChainPanelNative(_panel)),
            creation => D3DPresentationOperation.Run(
                D3DPresentationStage.CreateDevice,
                () => D3D11CreateDevice(
                    IntPtr.Zero,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                    out creation.Device,
                    out creation.Context).CheckError()));
        _panelNative = resources.Panel;
        _device = resources.Device;
        _context = resources.Context;
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

        var swapChainStage = GetSwapChainCreationStage(recovery);
        using var dxgiDevice = D3DPresentationOperation.Run(
            swapChainStage,
            () => device.QueryInterface<IDXGIDevice>());
        using var adapter = D3DPresentationOperation.Run(
            swapChainStage,
            dxgiDevice.GetAdapter);
        using var factory = D3DPresentationOperation.Run(
            swapChainStage,
            adapter.GetParent<IDXGIFactory2>);
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
                swapChainStage,
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
        RecoverDeviceAfterRemoval(
            () => DisposeFrameResources(detachPanel: true),
            () => _context?.Dispose(),
            () => _device?.Dispose(),
            () =>
            {
                _context = null;
                _device = null;
            },
            CreateDevice);
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
        ThrowFrameResourceFailures(failures);
    }

    internal static void ThrowFrameResourceFailures(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var primaryFailure = failures[0];
        if (primaryFailure is not D3DPresentationException)
        {
            throw new AggregateException("D3D frame resource cleanup failed.", failures);
        }

        if (failures.Count > 1)
        {
            var cleanupFailures = new Exception[failures.Count - 1];
            for (var index = 1; index < failures.Count; index++)
            {
                cleanupFailures[index - 1] = failures[index];
            }

            TryAttachFrameResourceCleanupFailures(
                primaryFailure,
                cleanupFailures);
        }

        ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    internal static void RecoverDeviceAfterRemoval(
        Action disposeFrameResources,
        Action disposeContext,
        Action disposeDevice,
        Action resetReferences,
        Action createDevice)
    {
        ArgumentNullException.ThrowIfNull(disposeFrameResources);
        ArgumentNullException.ThrowIfNull(disposeContext);
        ArgumentNullException.ThrowIfNull(disposeDevice);
        ArgumentNullException.ThrowIfNull(resetReferences);
        ArgumentNullException.ThrowIfNull(createDevice);

        List<Exception> cleanupFailures = [];
        CaptureFailure(disposeFrameResources, cleanupFailures);
        CaptureFailure(disposeContext, cleanupFailures);
        CaptureFailure(disposeDevice, cleanupFailures);
        resetReferences();

        try
        {
            createDevice();
        }
        catch (Exception createFailure)
        {
            TryAttachFrameResourceCleanupFailures(createFailure, cleanupFailures);
            ExceptionDispatchInfo.Capture(createFailure).Throw();
        }
    }

    internal static DeviceResourceSet<TPanel, TDevice, TContext>
        CreateDeviceResources<TPanel, TDevice, TContext>(
            TPanel? existingPanel,
            Func<TPanel> createPanel,
            Action<DeviceResourceCreation<TDevice, TContext>> createDevice)
        where TPanel : class, IDisposable
        where TDevice : class, IDisposable
        where TContext : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(createPanel);
        ArgumentNullException.ThrowIfNull(createDevice);

        var panel = existingPanel;
        var ownsPanel = false;
        var creation = new DeviceResourceCreation<TDevice, TContext>();

        try
        {
            if (panel is null)
            {
                panel = createPanel()
                    ?? throw new InvalidOperationException(
                        "SwapChainPanel native interop creation returned null.");
                ownsPanel = true;
            }

            createDevice(creation);
            var device = creation.Device
                ?? throw new InvalidOperationException("D3D11 device creation returned null.");
            var context = creation.Context
                ?? throw new InvalidOperationException(
                    "D3D11 device context creation returned null.");
            return new DeviceResourceSet<TPanel, TDevice, TContext>(
                panel,
                device,
                context);
        }
        catch (Exception primaryFailure)
        {
            List<Exception> cleanupFailures = [];
            CaptureFailure(() => creation.Context?.Dispose(), cleanupFailures);
            CaptureFailure(() => creation.Device?.Dispose(), cleanupFailures);
            if (ownsPanel)
            {
                CaptureFailure(() => panel?.Dispose(), cleanupFailures);
            }

            TryAttachFrameResourceCleanupFailures(primaryFailure, cleanupFailures);
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
    }

    private static void TryAttachFrameResourceCleanupFailures(
        Exception primaryFailure,
        IReadOnlyList<Exception> cleanupFailures)
    {
        if (cleanupFailures.Count == 0)
        {
            return;
        }

        try
        {
            var data = primaryFailure.Data;
            if (!data.Contains(FrameResourceTransaction.CleanupFailuresDataKey))
            {
                data[FrameResourceTransaction.CleanupFailuresDataKey] =
                    cleanupFailures.ToArray();
                return;
            }

            if (data[FrameResourceTransaction.CleanupFailuresDataKey]
                is not Exception[] existingCleanupFailures)
            {
                return;
            }

            var combinedCleanupFailures =
                new Exception[existingCleanupFailures.Length + cleanupFailures.Count];
            Array.Copy(
                existingCleanupFailures,
                combinedCleanupFailures,
                existingCleanupFailures.Length);
            for (var index = 0; index < cleanupFailures.Count; index++)
            {
                combinedCleanupFailures[existingCleanupFailures.Length + index] =
                    cleanupFailures[index];
            }

            data[FrameResourceTransaction.CleanupFailuresDataKey] =
                combinedCleanupFailures;
        }
        catch (Exception)
        {
            // Diagnostic attachment must never replace the primary failure.
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
