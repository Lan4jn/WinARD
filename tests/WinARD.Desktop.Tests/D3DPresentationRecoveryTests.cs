using System.Runtime.InteropServices;
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
            (_, _, _) => fallbackCount++);

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

        D3DPresentationRecovery.Execute(
            [1, 2, 3, 4],
            4,
            [new RemoteRectangle(0, 0, 1, 1)],
            (_, _, _) => throw failure,
            () => false,
            () => rebuildCount++,
            (_, _, _) => fallbackCount++);

        Assert.Equal(1, rebuildCount);
        Assert.Equal(1, fallbackCount);
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
                (_, _, _) => fallbackCount++));

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
                (_, _, _) => fallbackCount++));

        Assert.Same(failure, thrown);
        Assert.Equal(0, rebuildCount);
        Assert.Equal(0, fallbackCount);
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
                (_, _, _) => fallbackCount++));

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
                (_, _, _) =>
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

    private static D3DPresentationException PresentationFailure(D3DPresentationStage stage) =>
        new(stage, new COMException("synthetic native failure", DxgiErrorInvalidCall));
}
