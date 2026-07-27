using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using WinARD.Application.Ports;
using WinARD.Desktop.Rendering;
using Xunit;

#pragma warning disable CA1707
#pragma warning disable CA2201

namespace WinARD.Desktop.Tests;

public sealed class D3DPresentationRecoveryTests
{
    private const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);

    [Fact]
    public void Optimized_presentation_success_does_not_rebuild_or_fall_back()
    {
        var optimizedCount = 0;
        var rebuildCount = 0;
        var fallbackCount = 0;

        D3DPresentationRecovery.Execute(
            [1, 2, 3, 4],
            4,
            [new RemoteRectangle(0, 0, 1, 1)],
            (_, _, _) => optimizedCount++,
            () => false,
            () => rebuildCount++,
            (_, _) => fallbackCount++);

        Assert.Equal(1, optimizedCount);
        Assert.Equal(0, rebuildCount);
        Assert.Equal(0, fallbackCount);
    }

    [Fact]
    public void Present1_invalid_call_with_live_device_rebuilds_and_presents_full_frame_once()
    {
        var rebuildCount = 0;
        var fallbackCount = 0;
        var failure = PresentationFailure(D3DPresentationStage.Present1);
        byte[] pixels = [1, 2, 3, 4];
        byte[]? fallbackPixels = null;
        var fallbackStride = 0;

        D3DPresentationRecovery.Execute(
            pixels,
            4,
            [new RemoteRectangle(0, 0, 1, 1)],
            (_, _, _) => throw failure,
            () => false,
            () => rebuildCount++,
            (bgra32, stride) =>
            {
                fallbackCount++;
                fallbackPixels = bgra32.ToArray();
                fallbackStride = stride;
            });

        Assert.Equal(1, rebuildCount);
        Assert.Equal(1, fallbackCount);
        Assert.Equal(pixels, fallbackPixels);
        Assert.Equal(4, fallbackStride);
    }

    [Fact]
    public void GetBuffer_invalid_call_propagates_original_failure_without_recovery()
    {
        var rebuildCount = 0;
        var fallbackCount = 0;
        var failure = PresentationFailure(D3DPresentationStage.GetBuffer);

        var thrown = Assert.Throws<D3DPresentationException>(() =>
            D3DPresentationRecovery.Execute(
                [1, 2, 3, 4],
                4,
                [new RemoteRectangle(0, 0, 1, 1)],
                (_, _, _) => throw failure,
                () => false,
                () => rebuildCount++,
                (_, _) => fallbackCount++));

        Assert.Same(failure, thrown);
        Assert.Equal(0, rebuildCount);
        Assert.Equal(0, fallbackCount);
    }

    [Fact]
    public void Present1_invalid_call_with_removed_device_propagates_without_recovery()
    {
        var rebuildCount = 0;
        var fallbackCount = 0;
        var failure = PresentationFailure(D3DPresentationStage.Present1);

        var thrown = Assert.Throws<D3DPresentationException>(() =>
            D3DPresentationRecovery.Execute(
                [1, 2, 3, 4],
                4,
                [new RemoteRectangle(0, 0, 1, 1)],
                (_, _, _) => throw failure,
                () => true,
                () => rebuildCount++,
                (_, _) => fallbackCount++));

        Assert.Same(failure, thrown);
        Assert.Equal(0, rebuildCount);
        Assert.Equal(0, fallbackCount);
    }

    [Fact]
    public void Non_target_hresult_is_not_recovered()
    {
        var rebuildCount = 0;
        var fallbackCount = 0;
        var failure = new D3DPresentationException(
            D3DPresentationStage.Present1,
            new COMException(
                "synthetic non-target native failure",
                unchecked((int)0x887A0005)));

        var thrown = Assert.Throws<D3DPresentationException>(() =>
            D3DPresentationRecovery.Execute(
                [1, 2, 3, 4],
                4,
                [new RemoteRectangle(0, 0, 1, 1)],
                (_, _, _) => throw failure,
                () => false,
                () => rebuildCount++,
                (_, _) => fallbackCount++));

        Assert.Same(failure, thrown);
        Assert.Equal(0, rebuildCount);
        Assert.Equal(0, fallbackCount);
    }

    [Fact]
    public void No_frame_resource_failures_returns()
    {
        D3DFramePresenter.ThrowFrameResourceFailures([]);
    }

    [Fact]
    public void Single_recovery_detach_failure_preserves_original_exception()
    {
        var failure = PresentationFailure(D3DPresentationStage.RecoveryDetachSwapChain);

        var thrown = Assert.Throws<D3DPresentationException>(() =>
            D3DFramePresenter.ThrowFrameResourceFailures([failure]));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public void Recovery_detach_failure_preserves_original_throw_site()
    {
        var failure = CaptureThrownPresentationFailure(
            D3DPresentationStage.RecoveryDetachSwapChain);

        var thrown = Assert.Throws<D3DPresentationException>(() =>
            D3DFramePresenter.ThrowFrameResourceFailures([failure]));

        Assert.Same(failure, thrown);
        Assert.Contains(nameof(ThrowPresentationFailure), thrown.StackTrace);
    }

    [Fact]
    public void Recovery_detach_failure_preserves_metadata_and_attaches_later_cleanup_failures()
    {
        var failure = PresentationFailure(D3DPresentationStage.RecoveryDetachSwapChain);
        var swapChainCleanupFailure =
            new InvalidOperationException("synthetic swap-chain cleanup failure");
        var textureCleanupFailure =
            new InvalidOperationException("synthetic texture cleanup failure");

        var thrown = Assert.Throws<D3DPresentationException>(() =>
            D3DFramePresenter.ThrowFrameResourceFailures(
                [failure, swapChainCleanupFailure, textureCleanupFailure]));

        Assert.Same(failure, thrown);
        var cleanupFailures = Assert.IsType<Exception[]>(
            thrown.Data[FrameResourceTransaction.CleanupFailuresDataKey]);
        Assert.Equal(
            [swapChainCleanupFailure, textureCleanupFailure],
            cleanupFailures);
    }

    [Fact]
    public void Ordinary_frame_resource_failure_remains_aggregate_exception()
    {
        var cleanupFailure = new InvalidOperationException("synthetic cleanup failure");

        var thrown = Assert.Throws<AggregateException>(() =>
            D3DFramePresenter.ThrowFrameResourceFailures([cleanupFailure]));

        Assert.StartsWith("D3D frame resource cleanup failed.", thrown.Message);
        Assert.Equal([cleanupFailure], thrown.InnerExceptions);
    }

    [Fact]
    public void Device_recovery_continues_cleanup_and_creates_device_after_cleanup_failure()
    {
        var operations = new List<string>();
        var cleanupFailure = new InvalidOperationException("synthetic frame cleanup failure");

        D3DFramePresenter.RecoverDeviceAfterRemoval(
            () =>
            {
                operations.Add("frame-resources");
                throw cleanupFailure;
            },
            () => operations.Add("context"),
            () => operations.Add("device"),
            () => operations.Add("reset"),
            () => operations.Add("create"));

        Assert.Equal(
            ["frame-resources", "context", "device", "reset", "create"],
            operations);
    }

    [Fact]
    public void Device_creation_failure_preserves_primary_and_attaches_cleanup_failures_in_order()
    {
        var frameCleanupFailure =
            new InvalidOperationException("synthetic frame cleanup failure");
        var contextCleanupFailure =
            new InvalidOperationException("synthetic context cleanup failure");
        var deviceCleanupFailure =
            new InvalidOperationException("synthetic device cleanup failure");
        var createFailure = new InvalidOperationException("synthetic create failure");
        var resetCount = 0;

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            D3DFramePresenter.RecoverDeviceAfterRemoval(
                () => throw frameCleanupFailure,
                () => throw contextCleanupFailure,
                () => throw deviceCleanupFailure,
                () => resetCount++,
                () => ThrowFailure(createFailure)));

        Assert.Same(createFailure, thrown);
        Assert.Contains(nameof(ThrowFailure), thrown.StackTrace);
        Assert.Equal(1, resetCount);
        var cleanupFailures = Assert.IsType<Exception[]>(
            thrown.Data[FrameResourceTransaction.CleanupFailuresDataKey]);
        Assert.Equal(
            [frameCleanupFailure, contextCleanupFailure, deviceCleanupFailure],
            cleanupFailures);
    }

    [Fact]
    public void Device_recovery_attempts_device_creation_once_without_looping()
    {
        var createCount = 0;
        var createFailure = new InvalidOperationException("synthetic create failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            D3DFramePresenter.RecoverDeviceAfterRemoval(
                () => { },
                () => { },
                () => { },
                () => { },
                () =>
                {
                    createCount++;
                    throw createFailure;
                }));

        Assert.Same(createFailure, thrown);
        Assert.Equal(1, createCount);
    }

    [Fact]
    public void Device_recovery_reset_failure_propagates_without_creating_device()
    {
        var resetFailure = new InvalidOperationException("synthetic reset failure");
        var createCount = 0;

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            D3DFramePresenter.RecoverDeviceAfterRemoval(
                () => { },
                () => { },
                () => { },
                () => throw resetFailure,
                () => createCount++));

        Assert.Same(resetFailure, thrown);
        Assert.Equal(0, createCount);
    }

    [Fact]
    public void Device_recovery_appends_existing_cleanup_failure_diagnostics()
    {
        var existingFailure = new InvalidOperationException("existing cleanup failure");
        var frameCleanupFailure =
            new InvalidOperationException("synthetic frame cleanup failure");
        var createFailure = new InvalidOperationException("synthetic create failure");
        createFailure.Data[FrameResourceTransaction.CleanupFailuresDataKey] =
            new Exception[] { existingFailure };

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            D3DFramePresenter.RecoverDeviceAfterRemoval(
                () => throw frameCleanupFailure,
                () => { },
                () => { },
                () => { },
                () => throw createFailure));

        Assert.Same(createFailure, thrown);
        var cleanupFailures = Assert.IsType<Exception[]>(
            thrown.Data[FrameResourceTransaction.CleanupFailuresDataKey]);
        Assert.Equal([existingFailure, frameCleanupFailure], cleanupFailures);
    }

    [Fact]
    public void Device_recovery_data_failure_does_not_replace_create_failure()
    {
        var cleanupFailure = new InvalidOperationException("synthetic cleanup failure");
        var createFailure = new ThrowingDataException(
            () => throw new InvalidOperationException("synthetic Data getter failure"));

        var thrown = Assert.Throws<ThrowingDataException>(() =>
            D3DFramePresenter.RecoverDeviceAfterRemoval(
                () => throw cleanupFailure,
                () => { },
                () => { },
                () => { },
                () => throw createFailure));

        Assert.Same(createFailure, thrown);
    }

    [Fact]
    public void Device_recovery_data_contains_failure_does_not_replace_create_failure()
    {
        var cleanupFailure = new InvalidOperationException("synthetic cleanup failure");
        var createFailure = new ThrowingDataException(
            () => new ThrowingContainsDictionary());

        var thrown = Assert.Throws<ThrowingDataException>(() =>
            D3DFramePresenter.RecoverDeviceAfterRemoval(
                () => throw cleanupFailure,
                () => { },
                () => { },
                () => { },
                () => throw createFailure));

        Assert.Same(createFailure, thrown);
    }

    [Fact]
    public void Device_recovery_data_indexer_failure_does_not_replace_create_failure()
    {
        var cleanupFailure = new InvalidOperationException("synthetic cleanup failure");
        var createFailure = new ThrowingDataException(
            () => new ThrowingGetterDictionary());

        var thrown = Assert.Throws<ThrowingDataException>(() =>
            D3DFramePresenter.RecoverDeviceAfterRemoval(
                () => throw cleanupFailure,
                () => { },
                () => { },
                () => { },
                () => throw createFailure));

        Assert.Same(createFailure, thrown);
    }

    [Fact]
    public void Device_recovery_data_setter_failure_does_not_replace_create_failure()
    {
        var cleanupFailure = new InvalidOperationException("synthetic cleanup failure");
        var createFailure = new ThrowingDataException(
            () => new ThrowingSetterDictionary());

        var thrown = Assert.Throws<ThrowingDataException>(() =>
            D3DFramePresenter.RecoverDeviceAfterRemoval(
                () => throw cleanupFailure,
                () => { },
                () => { },
                () => { },
                () => throw createFailure));

        Assert.Same(createFailure, thrown);
    }

    [Fact]
    public void Rebuild_failure_propagates_original_exception_without_fallback()
    {
        var rebuildCount = 0;
        var fallbackCount = 0;
        var rebuildFailure = new InvalidOperationException("synthetic rebuild failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            D3DPresentationRecovery.Execute(
                [1, 2, 3, 4],
                4,
                [new RemoteRectangle(0, 0, 1, 1)],
                (_, _, _) => throw PresentationFailure(D3DPresentationStage.Present1),
                () => false,
                () =>
                {
                    rebuildCount++;
                    throw rebuildFailure;
                },
                (_, _) => fallbackCount++));

        Assert.Same(rebuildFailure, thrown);
        Assert.Equal(1, rebuildCount);
        Assert.Equal(0, fallbackCount);
    }

    [Fact]
    public void Fallback_failure_propagates_original_exception_without_second_recovery()
    {
        var rebuildCount = 0;
        var fallbackCount = 0;
        var fallbackFailure = new InvalidOperationException("synthetic fallback failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            D3DPresentationRecovery.Execute(
                [1, 2, 3, 4],
                4,
                [new RemoteRectangle(0, 0, 1, 1)],
                (_, _, _) => throw PresentationFailure(D3DPresentationStage.Present1),
                () => false,
                () => rebuildCount++,
                (_, _) =>
                {
                    fallbackCount++;
                    throw fallbackFailure;
                }));

        Assert.Same(fallbackFailure, thrown);
        Assert.Equal(1, rebuildCount);
        Assert.Equal(1, fallbackCount);
    }

    [Fact]
    public void Presentation_failure_exposes_stable_metadata_and_preserves_inner_exception()
    {
        var inner = new COMException("synthetic native failure", DxgiErrorInvalidCall);

        var failure = new D3DPresentationException(
            D3DPresentationStage.CreateSwapChainForComposition,
            inner);

        Assert.Equal(D3DPresentationStage.CreateSwapChainForComposition, failure.Stage);
        Assert.Equal(DxgiErrorInvalidCall, failure.HResult);
        Assert.Equal(
            "D3D presentation operation 'CreateSwapChainForComposition' failed.",
            failure.Message);
        Assert.Same(inner, failure.InnerException);
    }

    [Fact]
    public void Action_operation_wraps_SharpGen_failure_with_stage_and_native_metadata()
    {
        var nativeFailure = SharpGenFailure();

        var failure = Assert.Throws<D3DPresentationException>(() =>
            D3DPresentationOperation.Run(
                D3DPresentationStage.CreateDevice,
                () => throw nativeFailure));

        Assert.Equal(D3DPresentationStage.CreateDevice, failure.Stage);
        Assert.Equal(DxgiErrorInvalidCall, failure.HResult);
        Assert.Same(nativeFailure, failure.InnerException);
    }

    [Fact]
    public void Action_operation_propagates_non_SharpGen_failure_unchanged()
    {
        var operationFailure = new InvalidOperationException("synthetic managed failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            D3DPresentationOperation.Run(
                D3DPresentationStage.CreateDevice,
                () => throw operationFailure));

        Assert.Same(operationFailure, thrown);
    }

    [Fact]
    public void Generic_operation_returns_value()
    {
        var result = D3DPresentationOperation.Run(
            D3DPresentationStage.GetBuffer,
            () => 42);

        Assert.Equal(42, result);
    }

    [Fact]
    public void Generic_operation_wraps_SharpGen_failure()
    {
        var nativeFailure = SharpGenFailure();

        var failure = Assert.Throws<D3DPresentationException>(() =>
            D3DPresentationOperation.Run<int>(
                D3DPresentationStage.GetBuffer,
                () => throw nativeFailure));

        Assert.Equal(D3DPresentationStage.GetBuffer, failure.Stage);
        Assert.Equal(DxgiErrorInvalidCall, failure.HResult);
        Assert.Same(nativeFailure, failure.InnerException);
    }

    [Fact]
    public void Span_operation_receives_original_byte_content()
    {
        byte[] pixels = [4, 3, 2, 1];
        byte[]? received = null;

        D3DPresentationOperation.Run(
            D3DPresentationStage.UpdateSubresource,
            pixels,
            bytes => received = bytes.ToArray());

        Assert.Equal(pixels, received);
    }

    [Fact]
    public void Span_operation_wraps_SharpGen_failure()
    {
        var nativeFailure = SharpGenFailure();

        var failure = Assert.Throws<D3DPresentationException>(() =>
            D3DPresentationOperation.Run(
                D3DPresentationStage.UpdateSubresource,
                [1, 2, 3, 4],
                _ => throw nativeFailure));

        Assert.Equal(D3DPresentationStage.UpdateSubresource, failure.Stage);
        Assert.Equal(DxgiErrorInvalidCall, failure.HResult);
        Assert.Same(nativeFailure, failure.InnerException);
    }

    [Fact]
    public void Presentation_failure_rejects_null_inner_exception()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new D3DPresentationException(D3DPresentationStage.Present1, null!));
    }

    [Fact]
    public void Presentation_operations_reject_null_delegates()
    {
        Assert.Throws<ArgumentNullException>(() =>
            D3DPresentationOperation.Run(
                D3DPresentationStage.CreateDevice,
                (Action)null!));
        Assert.Throws<ArgumentNullException>(() =>
            D3DPresentationOperation.Run<int>(
                D3DPresentationStage.GetBuffer,
                null!));
        Assert.Throws<ArgumentNullException>(() =>
            D3DPresentationOperation.Run(
                D3DPresentationStage.UpdateSubresource,
                [],
                null!));
    }

    private static D3DPresentationException PresentationFailure(D3DPresentationStage stage) =>
        new(stage, new COMException("synthetic native failure", DxgiErrorInvalidCall));

    private static D3DPresentationException CaptureThrownPresentationFailure(
        D3DPresentationStage stage)
    {
        try
        {
            ThrowPresentationFailure(stage);
            throw new InvalidOperationException("Expected presentation failure was not thrown.");
        }
        catch (D3DPresentationException failure)
        {
            return failure;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPresentationFailure(D3DPresentationStage stage) =>
        throw PresentationFailure(stage);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFailure(Exception failure) => throw failure;

    private static SharpGenException SharpGenFailure() =>
        new(
            new Result(DxgiErrorInvalidCall),
            "synthetic native failure",
            new COMException("synthetic native failure", DxgiErrorInvalidCall));

    private sealed class ThrowingDataException : Exception
    {
        private readonly Func<IDictionary> _getData;

        public ThrowingDataException(Func<IDictionary> getData)
        {
            _getData = getData;
        }

        public override IDictionary Data => _getData();
    }

    private sealed class ThrowingContainsDictionary : Hashtable
    {
        public override bool Contains(object key) =>
            throw new InvalidOperationException("synthetic Data Contains failure");
    }

    private sealed class ThrowingGetterDictionary : Hashtable
    {
        public override bool Contains(object key) => true;

        public override object? this[object key]
        {
            get => throw new InvalidOperationException("synthetic Data getter failure");
            set => base[key] = value;
        }
    }

    private sealed class ThrowingSetterDictionary : Hashtable
    {
        public override object? this[object key]
        {
            get => base[key];
            set => throw new InvalidOperationException("synthetic Data setter failure");
        }
    }
}
