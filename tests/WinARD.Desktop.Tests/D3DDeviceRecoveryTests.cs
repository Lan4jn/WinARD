using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using WinARD.Application.Ports;
using WinARD.Desktop.Rendering;
using Xunit;

#pragma warning disable CA1707
#pragma warning disable CA2201

namespace WinARD.Desktop.Tests;

public sealed class D3DDeviceRecoveryTests
{
    private const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);

    [Fact]
    public void Device_recovery_candidate_accepts_only_D3D_and_SharpGen_failures()
    {
        Assert.True(D3DFramePresenter.IsDeviceRecoveryCandidate(
            PresentationFailure(D3DPresentationStage.GetBuffer)));
        Assert.True(D3DFramePresenter.IsDeviceRecoveryCandidate(SharpGenFailure()));
        Assert.False(D3DFramePresenter.IsDeviceRecoveryCandidate(
            new InvalidOperationException("synthetic managed failure")));
    }

    [Fact]
    public void Raw_SharpGen_failure_is_not_treated_as_invalid_call_swap_chain_recovery()
    {
        var rebuildCount = 0;
        var fallbackCount = 0;
        var failure = SharpGenFailure();

        var thrown = Assert.Throws<SharpGenException>(() =>
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
    public void Device_resources_transfer_only_after_all_creation_steps_succeed()
    {
        var panel = new TrackingDisposable();
        var device = new TrackingDisposable();
        var context = new TrackingDisposable();

        var resources = D3DFramePresenter.CreateDeviceResources<
            TrackingDisposable,
            TrackingDisposable,
            TrackingDisposable>(
            existingPanel: null,
            () => panel,
            creation =>
            {
                creation.Device = device;
                creation.Context = context;
            });

        Assert.Same(panel, resources.Panel);
        Assert.Same(device, resources.Device);
        Assert.Same(context, resources.Context);
        Assert.Equal(0, panel.DisposeCount);
        Assert.Equal(0, device.DisposeCount);
        Assert.Equal(0, context.DisposeCount);
    }

    [Fact]
    public void Panel_creation_failure_does_not_attempt_device_creation()
    {
        var createDeviceCount = 0;
        var failure = new InvalidOperationException("synthetic panel creation failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            D3DFramePresenter.CreateDeviceResources<
                TrackingDisposable,
                TrackingDisposable,
                TrackingDisposable>(
                existingPanel: null,
                () => throw failure,
                _ => createDeviceCount++));

        Assert.Same(failure, thrown);
        Assert.Equal(0, createDeviceCount);
    }

    [Fact]
    public void Null_panel_result_does_not_attempt_device_creation()
    {
        var createDeviceCount = 0;

        var failure = Assert.Throws<InvalidOperationException>(() =>
            D3DFramePresenter.CreateDeviceResources<
                TrackingDisposable,
                TrackingDisposable,
                TrackingDisposable>(
                existingPanel: null,
                () => null!,
                _ => createDeviceCount++));

        Assert.Equal(
            "SwapChainPanel native interop creation returned null.",
            failure.Message);
        Assert.Equal(0, createDeviceCount);
    }

    [Fact]
    public void Device_creation_failure_cleans_partial_resources_and_new_panel()
    {
        var cleanupOrder = new List<string>();
        var panel = new TrackingDisposable("panel", cleanupOrder);
        var device = new TrackingDisposable("device", cleanupOrder);
        var context = new TrackingDisposable("context", cleanupOrder);
        var failure = new InvalidOperationException("synthetic device creation failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            D3DFramePresenter.CreateDeviceResources<
                TrackingDisposable,
                TrackingDisposable,
                TrackingDisposable>(
                existingPanel: null,
                () => panel,
                creation =>
                {
                    creation.Device = device;
                    creation.Context = context;
                    throw failure;
                }));

        Assert.Same(failure, thrown);
        Assert.Equal(["context", "device", "panel"], cleanupOrder);
        Assert.Equal(1, panel.DisposeCount);
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(1, context.DisposeCount);
    }

    [Fact]
    public void Existing_panel_is_reused_and_not_disposed_when_device_creation_fails()
    {
        var createPanelCount = 0;
        var existingPanel = new TrackingDisposable();
        var device = new TrackingDisposable();
        var context = new TrackingDisposable();
        var failure = new InvalidOperationException("synthetic device creation failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            D3DFramePresenter.CreateDeviceResources<
                TrackingDisposable,
                TrackingDisposable,
                TrackingDisposable>(
                existingPanel,
                () =>
                {
                    createPanelCount++;
                    return new TrackingDisposable();
                },
                creation =>
                {
                    creation.Device = device;
                    creation.Context = context;
                    throw failure;
                }));

        Assert.Same(failure, thrown);
        Assert.Equal(0, createPanelCount);
        Assert.Equal(0, existingPanel.DisposeCount);
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(1, context.DisposeCount);
    }

    [Fact]
    public void Missing_device_result_cleans_context_and_new_panel()
    {
        var cleanupOrder = new List<string>();
        var panel = new TrackingDisposable("panel", cleanupOrder);
        var context = new TrackingDisposable("context", cleanupOrder);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            D3DFramePresenter.CreateDeviceResources<
                TrackingDisposable,
                TrackingDisposable,
                TrackingDisposable>(
                existingPanel: null,
                () => panel,
                creation => creation.Context = context));

        Assert.Equal("D3D11 device creation returned null.", failure.Message);
        Assert.Equal(["context", "panel"], cleanupOrder);
    }

    [Fact]
    public void Missing_context_result_cleans_device_and_new_panel()
    {
        var cleanupOrder = new List<string>();
        var panel = new TrackingDisposable("panel", cleanupOrder);
        var device = new TrackingDisposable("device", cleanupOrder);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            D3DFramePresenter.CreateDeviceResources<
                TrackingDisposable,
                TrackingDisposable,
                TrackingDisposable>(
                existingPanel: null,
                () => panel,
                creation => creation.Device = device));

        Assert.Equal("D3D11 device context creation returned null.", failure.Message);
        Assert.Equal(["device", "panel"], cleanupOrder);
    }

    [Fact]
    public void Creation_failure_preserves_primary_and_attaches_cleanup_failures()
    {
        var cleanupOrder = new List<string>();
        var panelCleanupFailure =
            new InvalidOperationException("synthetic panel cleanup failure");
        var deviceCleanupFailure =
            new InvalidOperationException("synthetic device cleanup failure");
        var contextCleanupFailure =
            new InvalidOperationException("synthetic context cleanup failure");
        var panel = new TrackingDisposable(
            "panel",
            cleanupOrder,
            panelCleanupFailure);
        var device = new TrackingDisposable(
            "device",
            cleanupOrder,
            deviceCleanupFailure);
        var context = new TrackingDisposable(
            "context",
            cleanupOrder,
            contextCleanupFailure);
        var creationFailure = new InvalidOperationException("synthetic creation failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            D3DFramePresenter.CreateDeviceResources<
                TrackingDisposable,
                TrackingDisposable,
                TrackingDisposable>(
                existingPanel: null,
                () => panel,
                creation =>
                {
                    creation.Device = device;
                    creation.Context = context;
                    ThrowFailure(creationFailure);
                }));

        Assert.Same(creationFailure, thrown);
        Assert.Contains(nameof(ThrowFailure), thrown.StackTrace);
        Assert.Equal(["context", "device", "panel"], cleanupOrder);
        var cleanupFailures = Assert.IsType<Exception[]>(
            thrown.Data[FrameResourceTransaction.CleanupFailuresDataKey]);
        Assert.Equal(
            [contextCleanupFailure, deviceCleanupFailure, panelCleanupFailure],
            cleanupFailures);
    }

    private static D3DPresentationException PresentationFailure(D3DPresentationStage stage) =>
        new(stage, new COMException("synthetic native failure", DxgiErrorInvalidCall));

    private static SharpGenException SharpGenFailure() =>
        new(
            new Result(DxgiErrorInvalidCall),
            "synthetic native failure",
            new COMException("synthetic native failure", DxgiErrorInvalidCall));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFailure(Exception failure) => throw failure;

    private sealed class TrackingDisposable : IDisposable
    {
        private readonly string? _name;
        private readonly List<string>? _cleanupOrder;
        private readonly Exception? _disposeFailure;

        public TrackingDisposable(
            string? name = null,
            List<string>? cleanupOrder = null,
            Exception? disposeFailure = null)
        {
            _name = name;
            _cleanupOrder = cleanupOrder;
            _disposeFailure = disposeFailure;
        }

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (_name is not null)
            {
                _cleanupOrder!.Add(_name);
            }

            if (_disposeFailure is not null)
            {
                throw _disposeFailure;
            }
        }
    }
}
