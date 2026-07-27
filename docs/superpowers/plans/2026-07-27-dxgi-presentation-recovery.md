# DXGI 画面呈现恢复实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 当脏矩形 `Present1` 返回 `DXGI_ERROR_INVALID_CALL` 时，WinARD 每帧最多重建一次交换链并用普通全帧 `Present` 恢复，同时输出安全、可定位的呈现阶段诊断。

**架构：** 新增不依赖 WinUI/GPU 的恢复协调器和事务式资源创建助手，通过内部异常保存稳定阶段名及 HRESULT。`D3DFramePresenter` 继续负责 Vortice 调用，但所有关键操作都经过阶段包装；`RemoteSessionViewModel` 保留包装异常并把阶段作为公开诊断字段写入现有安全诊断管线。

**技术栈：** C# 12、.NET 8、WinUI 3、Vortice.Direct3D11/DXGI 3.6.2、xUnit

---

## 文件结构

- 创建 `src/WinARD.Desktop/Rendering/D3DPresentationFailure.cs`：呈现阶段枚举、带 HRESULT 的安全包装异常、Vortice 操作包装助手。
- 创建 `src/WinARD.Desktop/Rendering/D3DPresentationRecovery.cs`：一次性交换链恢复协调器。
- 创建 `src/WinARD.Desktop/Rendering/FrameResourceTransaction.cs`：创建、绑定失败时释放临时资源的通用事务助手。
- 修改 `src/WinARD.Desktop/Rendering/D3DFramePresenter.cs`：接入阶段包装、事务式资源创建、交换链级恢复和普通全帧 `Present`。
- 修改 `src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`：保留呈现包装异常并写入 `PresentationStage` 诊断字段。
- 创建 `tests/WinARD.Desktop.Tests/D3DPresentationRecoveryTests.cs`：恢复决策、次数边界、错误传播及失败元数据测试。
- 创建 `tests/WinARD.Desktop.Tests/FrameResourceTransactionTests.cs`：临时资源所有权和恰好一次释放测试。
- 修改 `tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`：呈现阶段安全诊断集成测试。

### 任务 1：呈现失败元数据与一次性恢复协调器

**文件：**
- 创建：`tests/WinARD.Desktop.Tests/D3DPresentationRecoveryTests.cs`
- 创建：`src/WinARD.Desktop/Rendering/D3DPresentationFailure.cs`
- 创建：`src/WinARD.Desktop/Rendering/D3DPresentationRecovery.cs`

- [ ] **步骤 1：编写失败元数据和恢复行为测试**

创建 `tests/WinARD.Desktop.Tests/D3DPresentationRecoveryTests.cs`：

```csharp
using System.Runtime.InteropServices;
using WinARD.Desktop.Rendering;
using Xunit;

namespace WinARD.Desktop.Tests;

public sealed class D3DPresentationRecoveryTests
{
    private const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);

    [Fact]
    public void Successful_optimized_present_does_not_rebuild_or_fallback()
    {
        var optimized = 0;
        var rebuild = 0;
        var fallback = 0;

        D3DPresentationRecovery.Execute(
            ReadOnlySpan<byte>.Empty,
            stride: 0,
            dirtyRectangles: [],
            presentOptimized: (_, _, _) => optimized++,
            deviceRemoved: static () => false,
            rebuildSwapChain: () => rebuild++,
            presentFullFrame: (_, _, _) => fallback++);

        Assert.Equal(1, optimized);
        Assert.Equal(0, rebuild);
        Assert.Equal(0, fallback);
    }

    [Fact]
    public void Present1_invalid_call_rebuilds_once_and_uses_full_frame_present_once()
    {
        var rebuild = 0;
        var fallback = 0;

        D3DPresentationRecovery.Execute(
            ReadOnlySpan<byte>.Empty,
            stride: 0,
            dirtyRectangles: [],
            presentOptimized: static (_, _, _) =>
                throw Failure(D3DPresentationStage.Present1, DxgiErrorInvalidCall),
            deviceRemoved: static () => false,
            rebuildSwapChain: () => rebuild++,
            presentFullFrame: (_, _, _) => fallback++);

        Assert.Equal(1, rebuild);
        Assert.Equal(1, fallback);
    }

    [Fact]
    public void Invalid_call_outside_Present1_is_not_recovered()
    {
        var failure = Failure(
            D3DPresentationStage.GetBuffer,
            DxgiErrorInvalidCall);

        var actual = Assert.Throws<D3DPresentationException>(() =>
            D3DPresentationRecovery.Execute(
                ReadOnlySpan<byte>.Empty,
                stride: 0,
                dirtyRectangles: [],
                presentOptimized: (_, _, _) => throw failure,
                deviceRemoved: static () => false,
                rebuildSwapChain: static () => throw new Xunit.Sdk.XunitException("must not rebuild"),
                presentFullFrame: static (_, _, _) => throw new Xunit.Sdk.XunitException("must not present")));

        Assert.Same(failure, actual);
    }

    [Fact]
    public void Device_removed_state_bypasses_swap_chain_recovery()
    {
        var failure = Failure(
            D3DPresentationStage.Present1,
            DxgiErrorInvalidCall);

        var actual = Assert.Throws<D3DPresentationException>(() =>
            D3DPresentationRecovery.Execute(
                ReadOnlySpan<byte>.Empty,
                stride: 0,
                dirtyRectangles: [],
                presentOptimized: (_, _, _) => throw failure,
                deviceRemoved: static () => true,
                rebuildSwapChain: static () => throw new Xunit.Sdk.XunitException("must not rebuild"),
                presentFullFrame: static (_, _, _) => throw new Xunit.Sdk.XunitException("must not present")));

        Assert.Same(failure, actual);
    }

    [Fact]
    public void Rebuild_failure_propagates_without_retry()
    {
        var rebuild = 0;
        var expected = Failure(
            D3DPresentationStage.RecoveryCreateSwapChain,
            DxgiErrorInvalidCall);

        var actual = Assert.Throws<D3DPresentationException>(() =>
            D3DPresentationRecovery.Execute(
                ReadOnlySpan<byte>.Empty,
                stride: 0,
                dirtyRectangles: [],
                presentOptimized: static (_, _, _) =>
                    throw Failure(D3DPresentationStage.Present1, DxgiErrorInvalidCall),
                deviceRemoved: static () => false,
                rebuildSwapChain: () =>
                {
                    rebuild++;
                    throw expected;
                },
                presentFullFrame: static (_, _, _) => throw new Xunit.Sdk.XunitException("must not present")));

        Assert.Same(expected, actual);
        Assert.Equal(1, rebuild);
    }

    [Fact]
    public void Fallback_failure_propagates_without_second_recovery()
    {
        var rebuild = 0;
        var fallback = 0;
        var expected = Failure(
            D3DPresentationStage.RecoveryPresent,
            DxgiErrorInvalidCall);

        var actual = Assert.Throws<D3DPresentationException>(() =>
            D3DPresentationRecovery.Execute(
                ReadOnlySpan<byte>.Empty,
                stride: 0,
                dirtyRectangles: [],
                presentOptimized: static (_, _, _) =>
                    throw Failure(D3DPresentationStage.Present1, DxgiErrorInvalidCall),
                deviceRemoved: static () => false,
                rebuildSwapChain: () => rebuild++,
                presentFullFrame: (_, _, _) =>
                {
                    fallback++;
                    throw expected;
                }));

        Assert.Same(expected, actual);
        Assert.Equal(1, rebuild);
        Assert.Equal(1, fallback);
    }

    [Fact]
    public void Failure_exposes_stable_stage_and_original_hresult()
    {
        var failure = Failure(
            D3DPresentationStage.CreateSwapChainForComposition,
            DxgiErrorInvalidCall);

        Assert.Equal(
            D3DPresentationStage.CreateSwapChainForComposition,
            failure.Stage);
        Assert.Equal(DxgiErrorInvalidCall, failure.HResult);
        Assert.Equal(
            "D3D presentation operation 'CreateSwapChainForComposition' failed.",
            failure.Message);
        Assert.IsType<COMException>(failure.InnerException);
    }

    private static D3DPresentationException Failure(
        D3DPresentationStage stage,
        int hResult) =>
        new(stage, new COMException("synthetic native failure", hResult));
}
```

- [ ] **步骤 2：运行测试并确认因类型缺失而失败**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~D3DPresentationRecoveryTests
```

预期：编译失败，提示 `D3DPresentationRecovery`、`D3DPresentationStage` 和
`D3DPresentationException` 不存在。

- [ ] **步骤 3：实现最小失败元数据**

创建 `src/WinARD.Desktop/Rendering/D3DPresentationFailure.cs`：

```csharp
using SharpGen.Runtime;

namespace WinARD.Desktop.Rendering;

internal enum D3DPresentationStage
{
    CreateDevice,
    CreateTexture2D,
    CreateSwapChainForComposition,
    SetSwapChain,
    GetBuffer,
    UpdateSubresource,
    CopySubresourceRegion,
    Present1,
    RecoveryDetachSwapChain,
    RecoveryCreateSwapChain,
    RecoverySetSwapChain,
    RecoveryGetBuffer,
    RecoveryPresent,
}

internal sealed class D3DPresentationException : Exception
{
    internal const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);

    public D3DPresentationException(
        D3DPresentationStage stage,
        Exception innerException)
        : base($"D3D presentation operation '{stage}' failed.", innerException)
    {
        Stage = stage;
        HResult = innerException.HResult;
    }

    public D3DPresentationStage Stage { get; }

    public bool IsPresent1InvalidCall =>
        Stage == D3DPresentationStage.Present1 &&
        HResult == DxgiErrorInvalidCall;
}

internal static class D3DPresentationOperation
{
    internal delegate void ReadOnlySpanOperation(ReadOnlySpan<byte> value);

    public static void Run(D3DPresentationStage stage, Action operation)
    {
        try
        {
            operation();
        }
        catch (SharpGenException exception)
        {
            throw new D3DPresentationException(stage, exception);
        }
    }

    public static T Run<T>(
        D3DPresentationStage stage,
        Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (SharpGenException exception)
        {
            throw new D3DPresentationException(stage, exception);
        }
    }

    public static void Run(
        D3DPresentationStage stage,
        ReadOnlySpan<byte> value,
        ReadOnlySpanOperation operation)
    {
        try
        {
            operation(value);
        }
        catch (SharpGenException exception)
        {
            throw new D3DPresentationException(stage, exception);
        }
    }
}
```

- [ ] **步骤 4：实现一次性恢复协调器**

创建 `src/WinARD.Desktop/Rendering/D3DPresentationRecovery.cs`：

```csharp
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
```

- [ ] **步骤 5：运行聚焦测试并确认通过**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~D3DPresentationRecoveryTests
```

预期：7 个测试通过，0 个失败。

- [ ] **步骤 6：提交恢复策略**

```powershell
git add src/WinARD.Desktop/Rendering/D3DPresentationFailure.cs src/WinARD.Desktop/Rendering/D3DPresentationRecovery.cs tests/WinARD.Desktop.Tests/D3DPresentationRecoveryTests.cs
git commit -m "fix: add bounded DXGI presentation recovery policy"
```

### 任务 2：事务式帧资源创建

**文件：**
- 创建：`tests/WinARD.Desktop.Tests/FrameResourceTransactionTests.cs`
- 创建：`src/WinARD.Desktop/Rendering/FrameResourceTransaction.cs`

- [ ] **步骤 1：编写临时资源所有权测试**

创建 `tests/WinARD.Desktop.Tests/FrameResourceTransactionTests.cs`：

```csharp
using WinARD.Desktop.Rendering;
using Xunit;

namespace WinARD.Desktop.Tests;

public sealed class FrameResourceTransactionTests
{
    [Fact]
    public void Successful_creation_transfers_ownership_to_caller()
    {
        var texture = new TrackingDisposable();
        var swapChain = new TrackingDisposable();

        var result = FrameResourceTransaction.Create(
            createTexture: () => texture,
            createSwapChain: () => swapChain,
            bindSwapChain: static _ => { });

        Assert.Same(texture, result.Texture);
        Assert.Same(swapChain, result.SwapChain);
        Assert.Equal(0, texture.DisposeCount);
        Assert.Equal(0, swapChain.DisposeCount);

        result.SwapChain.Dispose();
        result.Texture.Dispose();
        Assert.Equal(1, texture.DisposeCount);
        Assert.Equal(1, swapChain.DisposeCount);
    }

    [Fact]
    public void Swap_chain_creation_failure_disposes_texture_once()
    {
        var texture = new TrackingDisposable();

        Assert.Throws<InvalidOperationException>(() =>
            FrameResourceTransaction.Create<TrackingDisposable, TrackingDisposable>(
                createTexture: () => texture,
                createSwapChain: static () => throw new InvalidOperationException("create failed"),
                bindSwapChain: static _ => { }));

        Assert.Equal(1, texture.DisposeCount);
    }

    [Fact]
    public void Bind_failure_disposes_both_resources_once()
    {
        var texture = new TrackingDisposable();
        var swapChain = new TrackingDisposable();

        Assert.Throws<InvalidOperationException>(() =>
            FrameResourceTransaction.Create(
                createTexture: () => texture,
                createSwapChain: () => swapChain,
                bindSwapChain: static _ => throw new InvalidOperationException("bind failed")));

        Assert.Equal(1, texture.DisposeCount);
        Assert.Equal(1, swapChain.DisposeCount);
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
```

- [ ] **步骤 2：运行测试并确认因助手缺失而失败**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~FrameResourceTransactionTests
```

预期：编译失败，提示 `FrameResourceTransaction` 不存在。

- [ ] **步骤 3：实现最小事务助手**

创建 `src/WinARD.Desktop/Rendering/FrameResourceTransaction.cs`：

```csharp
namespace WinARD.Desktop.Rendering;

internal readonly record struct FrameResourcePair<TTexture, TSwapChain>(
    TTexture Texture,
    TSwapChain SwapChain)
    where TTexture : class, IDisposable
    where TSwapChain : class, IDisposable;

internal static class FrameResourceTransaction
{
    public static FrameResourcePair<TTexture, TSwapChain> Create<TTexture, TSwapChain>(
        Func<TTexture> createTexture,
        Func<TSwapChain> createSwapChain,
        Action<TSwapChain> bindSwapChain)
        where TTexture : class, IDisposable
        where TSwapChain : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(createTexture);
        ArgumentNullException.ThrowIfNull(createSwapChain);
        ArgumentNullException.ThrowIfNull(bindSwapChain);

        TTexture? texture = null;
        TSwapChain? swapChain = null;
        try
        {
            texture = createTexture();
            swapChain = createSwapChain();
            bindSwapChain(swapChain);
            var result = new FrameResourcePair<TTexture, TSwapChain>(
                texture,
                swapChain);
            texture = null;
            swapChain = null;
            return result;
        }
        finally
        {
            swapChain?.Dispose();
            texture?.Dispose();
        }
    }
}
```

- [ ] **步骤 4：运行事务助手测试并确认通过**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~FrameResourceTransactionTests
```

预期：3 个测试通过，0 个失败。

- [ ] **步骤 5：提交事务助手**

```powershell
git add src/WinARD.Desktop/Rendering/FrameResourceTransaction.cs tests/WinARD.Desktop.Tests/FrameResourceTransactionTests.cs
git commit -m "fix: make DXGI frame resource creation transactional"
```

### 任务 3：接入 D3DFramePresenter

**文件：**
- 修改：`src/WinARD.Desktop/Rendering/D3DFramePresenter.cs`
- 修改：`tests/WinARD.Desktop.Tests/D3DPresentationRecoveryTests.cs`

- [ ] **步骤 1：添加非目标 HRESULT 不恢复的失败测试**

在 `D3DPresentationRecoveryTests` 中加入：

```csharp
[Fact]
public void Non_target_hresult_is_not_recovered()
{
    var failure = Failure(
        D3DPresentationStage.Present1,
        unchecked((int)0x887A0005));

    var actual = Assert.Throws<D3DPresentationException>(() =>
        D3DPresentationRecovery.Execute(
            ReadOnlySpan<byte>.Empty,
            stride: 0,
            dirtyRectangles: [],
            presentOptimized: (_, _, _) => throw failure,
            deviceRemoved: static () => false,
            rebuildSwapChain: static () => throw new Xunit.Sdk.XunitException("must not rebuild"),
            presentFullFrame: static (_, _, _) => throw new Xunit.Sdk.XunitException("must not present")));

    Assert.Same(failure, actual);
}
```

- [ ] **步骤 2：临时破坏目标 HRESULT 常量并验证测试会失败，然后恢复**

将 `D3DPresentationException.DxgiErrorInvalidCall` 临时改为
`unchecked((int)0x887A0005)`，运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~D3DPresentationRecoveryTests
```

预期：`Non_target_hresult_is_not_recovered` 失败。立即把常量恢复为
`unchecked((int)0x887A0001)`，重新运行并确认全部通过。此步骤证明回归测试确实约束目标 HRESULT，
不提交临时破坏。

- [ ] **步骤 3：将正常和恢复呈现接入协调器**

修改 `D3DFramePresenter.Present`：

```csharp
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
        CreateFrameResources(
            _width,
            _height,
            detachPanel: false,
            recovery: true);
        PresentCore(
            bgra32,
            stride,
            [new RemoteRectangle(0, 0, _width, _height)],
            useDirtyRectanglePresent: false,
            recovery: true);
    }
}

private bool IsDeviceRemoved() =>
    _device?.DeviceRemovedReason.Failure == true;

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
    CreateFrameResources(
        _width,
        _height,
        detachPanel: true,
        recovery: true);

private void PresentRecoveredFullFrame(
    ReadOnlySpan<byte> bgra32,
    int stride,
    IReadOnlyList<RemoteRectangle> dirtyRectangles) =>
    PresentCore(
        bgra32,
        stride,
        [new RemoteRectangle(0, 0, _width, _height)],
        useDirtyRectanglePresent: false,
        recovery: true);
```

将 `PresentCore` 增加 `useDirtyRectanglePresent` 和 `recovery` 参数。保留现有裁剪、上传和复制循环，
但用 `D3DPresentationOperation.Run` 包装 `GetBuffer`、`UpdateSubresource` 和
`CopySubresourceRegion`。阶段按 `recovery` 选择正常或恢复名称：

```csharp
var getBufferStage = recovery
    ? D3DPresentationStage.RecoveryGetBuffer
    : D3DPresentationStage.GetBuffer;
using var backBuffer = D3DPresentationOperation.Run(
    getBufferStage,
    () => swapChain.GetBuffer<ID3D11Texture2D>(0));
```

上传和复制分别使用：

```csharp
D3DPresentationOperation.Run(
    D3DPresentationStage.UpdateSubresource,
    source,
    pixels => context.UpdateSubresource(
        pixels,
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
```

最后按模式提交：

```csharp
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
}
else
{
    D3DPresentationOperation.Run(
        D3DPresentationStage.RecoveryPresent,
        () => swapChain.Present(0, PresentFlags.None).CheckError());
}
```

交换链级和设备级回退都使用 `RecoveryPresent`。不允许从普通 `Present` 再进入交换链恢复。

- [ ] **步骤 4：事务式重写设备和帧资源创建**

在 `CreateDevice` 中用 `D3DPresentationOperation.Run(D3DPresentationStage.CreateDevice, ...)`
包装 `D3D11CreateDevice(...).CheckError()`。

把 `CreateFrameResources` 改为：

```csharp
private void CreateFrameResources(
    int width,
    int height,
    bool detachPanel = false,
    bool recovery = false)
{
    var device = _device ?? throw new InvalidOperationException("The D3D11 device is unavailable.");
    DisposeFrameResources(detachPanel);

    using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
    using var adapter = dxgiDevice.GetAdapter();
    using var factory = adapter.GetParent<IDXGIFactory2>();
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
    var panelNative = _panelNative ??
        throw new InvalidOperationException("SwapChainPanel native interop is unavailable.");
    var createSwapChainStage = recovery
        ? D3DPresentationStage.RecoveryCreateSwapChain
        : D3DPresentationStage.CreateSwapChainForComposition;
    var setSwapChainStage = recovery
        ? D3DPresentationStage.RecoverySetSwapChain
        : D3DPresentationStage.SetSwapChain;

    var resources = FrameResourceTransaction.Create(
        createTexture: () => D3DPresentationOperation.Run(
            D3DPresentationStage.CreateTexture2D,
            () => device.CreateTexture2D(textureDescription)),
        createSwapChain: () => D3DPresentationOperation.Run(
            createSwapChainStage,
            () => factory.CreateSwapChainForComposition(
                device,
                swapChainDescription,
                null!)),
        bindSwapChain: swapChain => D3DPresentationOperation.Run(
            setSwapChainStage,
            () => panelNative.SetSwapChain(swapChain).CheckError()));

    _texture = resources.Texture;
    _swapChain = resources.SwapChain;
    _width = width;
    _height = height;
}
```

在 `DisposeFrameResources` 分离面板时，将 `SetSwapChain(null!)` 包装为
`RecoveryDetachSwapChain`。清理仍使用 `CaptureFailure` 聚合，字段在清理结束后统一置空。

- [ ] **步骤 5：构建并运行渲染聚焦测试**

运行：

```powershell
dotnet build src/WinARD.Desktop/WinARD.Desktop.csproj -c Release
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --filter "FullyQualifiedName~D3DPresentationRecoveryTests|FullyQualifiedName~FrameResourceTransactionTests|FullyQualifiedName~FramePresentationTests"
```

预期：构建 0 错误；上述测试全部通过。

- [ ] **步骤 6：提交 D3D 接入**

```powershell
git add src/WinARD.Desktop/Rendering/D3DFramePresenter.cs tests/WinARD.Desktop.Tests/D3DPresentationRecoveryTests.cs
git commit -m "fix: recover invalid DXGI dirty presents"
```

### 任务 4：把呈现阶段写入安全诊断

**文件：**
- 修改：`tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`

- [ ] **步骤 1：编写呈现阶段诊断集成测试**

在 `RemoteSessionViewModelTests` 中加入：

```csharp
[Fact]
public async Task Presentation_failure_diagnostic_preserves_stage_and_hresult()
{
    const int hResult = unchecked((int)0x887A0001);
    var frameOwner = new TrackingMemoryOwner([0, 0, 0, 255]);
    var sink = new RecordingDiagnosticSink();
    var presenter = new FailingWithExceptionPresenter(
        new D3DPresentationException(
            D3DPresentationStage.Present1,
            new System.Runtime.InteropServices.COMException(
                "sensitive native details",
                hResult)));
    await using var viewModel = new RemoteSessionViewModel(
        new SingleFrameThenBlockingRuntime(frameOwner),
        new TrackingLifetime(),
        presenter,
        new InlineDispatcher(),
        clipboardBridge: null,
        diagnosticSink: sink);

    await viewModel.StartAsync(CancellationToken.None);
    await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

    var diagnostic = Assert.Single(sink.Events);
    Assert.Equal("REMOTE_PRESENTATION_FAILED", diagnostic.Code);
    Assert.IsType<D3DPresentationException>(diagnostic.Exception);
    Assert.Equal(hResult, diagnostic.Exception!.HResult);
    var stage = Assert.Single(diagnostic.Fields!);
    Assert.Equal("PresentationStage", stage.Name);
    Assert.Equal("Present1", stage.Value);
    Assert.Equal(DiagnosticFieldCategory.Public, stage.Category);
    Assert.DoesNotContain("sensitive native details", diagnostic.Message, StringComparison.Ordinal);
}
```

加入测试辅助类型：

```csharp
private sealed class FailingWithExceptionPresenter(Exception exception) : IFramePresenter
{
    public void Resize(int width, int height) { }

    public void Present(
        ReadOnlySpan<byte> bgra32,
        int stride,
        IReadOnlyList<RemoteRectangle> dirtyRectangles) =>
        throw exception;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

private sealed class RecordingDiagnosticSink : ISafeDiagnosticSink
{
    public List<SafeDiagnosticEventInput> Events { get; } = [];

    public void Write(SafeDiagnosticEventInput diagnosticEvent) =>
        Events.Add(diagnosticEvent);

    public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
}
```

- [ ] **步骤 2：运行测试并确认阶段信息丢失**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~Presentation_failure_diagnostic_preserves_stage_and_hresult
```

预期：测试失败，因为 `GetBaseException()` 返回内部原生异常，且诊断没有
`PresentationStage` 字段。

- [ ] **步骤 3：保留单一任务异常并生成安全字段**

在 `RemoteSessionViewModel` 中添加：

```csharp
private static Exception GetTerminalException(Task completed) =>
    completed.Exception switch
    {
        { InnerExceptions.Count: 1 } aggregate => aggregate.InnerException!,
        { } aggregate => aggregate.GetBaseException(),
        _ => new InvalidOperationException("Remote session terminated unexpectedly."),
    };

private static IReadOnlyList<DiagnosticField>? GetPresentationFailureFields(
    Exception exception) =>
    exception is D3DPresentationException presentation
        ? [new DiagnosticField(
            "PresentationStage",
            presentation.Stage.ToString(),
            DiagnosticFieldCategory.Public)]
        : null;
```

把 `MonitorLoopsAsync` 中异常提取改为：

```csharp
var exception = GetTerminalException(completed);
```

把诊断写入改为：

```csharp
_diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
    error.Code,
    error.CorrelationId,
    "Remote session loop failed.",
    Fields: ReferenceEquals(completed, present)
        ? GetPresentationFailureFields(exception)
        : null,
    Exception: exception));
```

- [ ] **步骤 4：运行诊断测试和现有会话失败测试**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --filter "FullyQualifiedName~Presentation_failure_diagnostic_preserves_stage_and_hresult|FullyQualifiedName~Permanent_presenter_failure|FullyQualifiedName~Presenter_failure_still_terminates"
```

预期：全部通过；永久呈现失败仍只调用一次 presenter，并释放帧和会话资源。

- [ ] **步骤 5：提交诊断接入**

```powershell
git add src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs
git commit -m "fix: report safe DXGI presentation stages"
```

### 任务 5：完整验证和 4K 冒烟测试

**文件：**
- 不创建生产文件。
- 验证日志只保留在终端；失败时导出到被 `.gitignore` 忽略的 `artifacts/`。

- [ ] **步骤 1：运行格式检查**

```powershell
dotnet format WinARD.sln --verify-no-changes --no-restore
```

预期：退出码 0，无格式差异。

- [ ] **步骤 2：运行完整 Release 构建和测试**

```powershell
dotnet build WinARD.sln -c Release --no-restore
dotnet test WinARD.sln -c Release --no-build
```

预期：构建 0 错误、0 警告；875 个既有测试加本计划新增测试全部通过。

- [ ] **步骤 3：运行供应链检查**

```powershell
pwsh -File packaging/tests/check-licenses.Tests.ps1
pwsh -File packaging/check-vulnerabilities.ps1
pwsh -File packaging/check-licenses.ps1
```

预期：

- 许可证检查脚本自身测试通过。
- 漏洞检查退出码 0。
- 严格许可证门仍会因既有 7 个缺少 SPDX expression/file 声明的上游包退出 1；确认列表没有因本次
  修复增加新包或发生变化，不把该既有红灯报告为通过。

- [ ] **步骤 4：重复运行 4K 本地会话冒烟测试**

先创建被忽略的输出目录并定位 Release 可执行文件：

```powershell
New-Item -ItemType Directory -Force artifacts/dxgi-smoke | Out-Null
$exe = Resolve-Path "src/WinARD.Desktop/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/WinARD.Desktop.exe"
```

连续运行 5 次，每次 15 秒：

```powershell
1..5 | ForEach-Object {
    $errorMarker = Join-Path $PWD "artifacts/dxgi-smoke/error-$_.txt"
    $releaseMarker = Join-Path $PWD "artifacts/dxgi-smoke/released-$_.txt"
    Remove-Item -LiteralPath $errorMarker,$releaseMarker -Force -ErrorAction SilentlyContinue
    $env:WINARD_REMOTE_SMOKE_ERROR_MARKER = $errorMarker
    $env:WINARD_REMOTE_SMOKE_MARKER = $releaseMarker
    $process = Start-Process -FilePath $exe -ArgumentList "--remote-session-smoke" -PassThru
    Start-Sleep -Seconds 15
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            Stop-Process -Id $process.Id
        }
    }

    if (Test-Path -LiteralPath $errorMarker) {
        throw "4K smoke iteration $_ failed: $(Get-Content -Raw -LiteralPath $errorMarker)"
    }

    if (-not (Test-Path -LiteralPath $releaseMarker)) {
        throw "4K smoke iteration $_ did not release session ownership."
    }
}
```

预期：5 次均没有 `REMOTE_PRESENTATION_FAILED` 错误标记，并且每次都生成 ownership release
标记。若仍失败，保留错误标记并立即导出诊断包，按阶段名定位到具体 DXGI 操作，不继续猜测式修改。

- [ ] **步骤 5：检查差异和工作区状态**

```powershell
git diff --check
git status --short
git log --oneline master..HEAD
```

预期：`git diff --check` 退出码 0；没有未提交的源码或测试改动；提交历史只包含规格、计划及本次
修复提交。

- [ ] **步骤 6：提交实现计划执行记录以外的最终修正**

如果步骤 1 至 5 发现格式化产生了源码差异，先重新运行聚焦测试和完整测试，再提交：

```powershell
git add src tests
git commit -m "style: normalize DXGI recovery changes"
```

如果没有差异，不创建空提交。
