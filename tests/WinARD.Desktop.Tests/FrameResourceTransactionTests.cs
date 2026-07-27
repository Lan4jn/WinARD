using WinARD.Desktop.Rendering;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests;

public sealed class FrameResourceTransactionTests
{
    [Fact]
    public void Successful_creation_transfers_resource_ownership_to_caller()
    {
        var createTextureCount = 0;
        var createSwapChainCount = 0;
        var bindCount = 0;
        var texture = new TrackingDisposable();
        var swapChain = new TrackingDisposable();

        var resources = FrameResourceTransaction.Create(
            () =>
            {
                createTextureCount++;
                return texture;
            },
            () =>
            {
                createSwapChainCount++;
                return swapChain;
            },
            boundSwapChain =>
            {
                bindCount++;
                Assert.Same(swapChain, boundSwapChain);
            });

        Assert.Equal(1, createTextureCount);
        Assert.Equal(1, createSwapChainCount);
        Assert.Equal(1, bindCount);
        Assert.Same(texture, resources.Texture);
        Assert.Same(swapChain, resources.SwapChain);
        Assert.Equal(0, texture.DisposeCount);
        Assert.Equal(0, swapChain.DisposeCount);

        resources.Texture.Dispose();
        resources.SwapChain.Dispose();

        Assert.Equal(1, texture.DisposeCount);
        Assert.Equal(1, swapChain.DisposeCount);
    }

    [Fact]
    public void Swap_chain_creation_failure_disposes_texture_and_propagates_original_exception()
    {
        var bindCount = 0;
        var texture = new TrackingDisposable();
        var failure = new InvalidOperationException("synthetic swap-chain creation failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            FrameResourceTransaction.Create<TrackingDisposable, TrackingDisposable>(
                () => texture,
                () => throw failure,
                _ => bindCount++));

        Assert.Same(failure, thrown);
        Assert.Equal(1, texture.DisposeCount);
        Assert.Equal(0, bindCount);
    }

    [Fact]
    public void Bind_failure_disposes_both_resources_and_propagates_original_exception()
    {
        var texture = new TrackingDisposable();
        var swapChain = new TrackingDisposable();
        var failure = new InvalidOperationException("synthetic bind failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            FrameResourceTransaction.Create(
                () => texture,
                () => swapChain,
                _ => throw failure));

        Assert.Same(failure, thrown);
        Assert.Equal(1, texture.DisposeCount);
        Assert.Equal(1, swapChain.DisposeCount);
    }

    [Fact]
    public void Cleanup_failures_are_attached_without_replacing_primary_failure()
    {
        var textureCleanupFailure = new InvalidOperationException("synthetic texture cleanup failure");
        var swapChainCleanupFailure = new InvalidOperationException("synthetic swap-chain cleanup failure");
        var texture = new TrackingDisposable(textureCleanupFailure);
        var swapChain = new TrackingDisposable(swapChainCleanupFailure);
        var primaryFailure = new InvalidOperationException("synthetic bind failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            FrameResourceTransaction.Create(
                () => texture,
                () => swapChain,
                _ => throw primaryFailure));

        Assert.Same(primaryFailure, thrown);
        var cleanupFailures = Assert.IsType<Exception[]>(
            thrown.Data[FrameResourceTransaction.CleanupFailuresDataKey]);
        Assert.Equal([swapChainCleanupFailure, textureCleanupFailure], cleanupFailures);
        Assert.Equal(1, texture.DisposeCount);
        Assert.Equal(1, swapChain.DisposeCount);
    }

    [Fact]
    public void Texture_creation_failure_does_not_run_later_steps()
    {
        var createSwapChainCount = 0;
        var bindCount = 0;
        var failure = new InvalidOperationException("synthetic texture creation failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            FrameResourceTransaction.Create<TrackingDisposable, TrackingDisposable>(
                () => throw failure,
                () =>
                {
                    createSwapChainCount++;
                    return new TrackingDisposable();
                },
                _ => bindCount++));

        Assert.Same(failure, thrown);
        Assert.Equal(0, createSwapChainCount);
        Assert.Equal(0, bindCount);
    }

    [Fact]
    public void Null_delegates_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FrameResourceTransaction.Create<TrackingDisposable, TrackingDisposable>(
                null!,
                () => new TrackingDisposable(),
                _ => { }));
        Assert.Throws<ArgumentNullException>(() =>
            FrameResourceTransaction.Create<TrackingDisposable, TrackingDisposable>(
                () => new TrackingDisposable(),
                null!,
                _ => { }));
        Assert.Throws<ArgumentNullException>(() =>
            FrameResourceTransaction.Create<TrackingDisposable, TrackingDisposable>(
                () => new TrackingDisposable(),
                () => new TrackingDisposable(),
                null!));
    }

    private sealed class TrackingDisposable : IDisposable
    {
        private readonly Exception? _disposeFailure;

        public TrackingDisposable(Exception? disposeFailure = null)
        {
            _disposeFailure = disposeFailure;
        }

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (_disposeFailure is not null)
            {
                throw _disposeFailure;
            }
        }
    }
}
