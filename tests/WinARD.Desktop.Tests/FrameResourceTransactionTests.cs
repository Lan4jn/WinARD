using System.Collections;
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
        var cleanupOrder = new List<string>();
        var texture = new TrackingDisposable("texture", cleanupOrder);
        var swapChain = new TrackingDisposable("swapChain", cleanupOrder);
        var failure = new InvalidOperationException("synthetic bind failure");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            FrameResourceTransaction.Create(
                () => texture,
                () => swapChain,
                _ => throw failure));

        Assert.Same(failure, thrown);
        Assert.Equal(1, texture.DisposeCount);
        Assert.Equal(1, swapChain.DisposeCount);
        Assert.Equal(["swapChain", "texture"], cleanupOrder);
    }

    [Fact]
    public void Cleanup_failures_are_attached_without_replacing_primary_failure()
    {
        var cleanupOrder = new List<string>();
        var textureCleanupFailure = new InvalidOperationException("synthetic texture cleanup failure");
        var swapChainCleanupFailure = new InvalidOperationException("synthetic swap-chain cleanup failure");
        var texture = new TrackingDisposable("texture", cleanupOrder, textureCleanupFailure);
        var swapChain = new TrackingDisposable("swapChain", cleanupOrder, swapChainCleanupFailure);
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
        Assert.Equal(["swapChain", "texture"], cleanupOrder);
    }

    [Fact]
    public void Cleanup_failures_are_appended_to_existing_cleanup_failures()
    {
        var existingCleanupFailure = new InvalidOperationException("existing cleanup failure");
        var swapChainCleanupFailure = new InvalidOperationException("synthetic swap-chain cleanup failure");
        var textureCleanupFailure = new InvalidOperationException("synthetic texture cleanup failure");
        var primaryFailure = new InvalidOperationException("synthetic bind failure");
        primaryFailure.Data[FrameResourceTransaction.CleanupFailuresDataKey] =
            new Exception[] { existingCleanupFailure };

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            FrameResourceTransaction.Create(
                () => new TrackingDisposable(disposeFailure: textureCleanupFailure),
                () => new TrackingDisposable(disposeFailure: swapChainCleanupFailure),
                _ => throw primaryFailure));

        Assert.Same(primaryFailure, thrown);
        var cleanupFailures = Assert.IsType<Exception[]>(
            thrown.Data[FrameResourceTransaction.CleanupFailuresDataKey]);
        Assert.Equal(
            [existingCleanupFailure, swapChainCleanupFailure, textureCleanupFailure],
            cleanupFailures);
    }

    [Fact]
    public void Cleanup_failures_do_not_replace_existing_non_exception_data()
    {
        const string existingValue = "existing diagnostic value";
        var primaryFailure = new InvalidOperationException("synthetic bind failure");
        primaryFailure.Data[FrameResourceTransaction.CleanupFailuresDataKey] = existingValue;

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            FrameResourceTransaction.Create(
                () => new TrackingDisposable(
                    disposeFailure: new InvalidOperationException("texture cleanup failure")),
                () => new TrackingDisposable(
                    disposeFailure: new InvalidOperationException("swap-chain cleanup failure")),
                _ => throw primaryFailure));

        Assert.Same(primaryFailure, thrown);
        Assert.Equal(existingValue, thrown.Data[FrameResourceTransaction.CleanupFailuresDataKey]);
    }

    [Fact]
    public void Data_getter_failure_does_not_replace_primary_failure()
    {
        var primaryFailure = new ThrowingDataException(
            () => throw new InvalidOperationException("synthetic Data getter failure"));

        var thrown = Assert.Throws<ThrowingDataException>(() =>
            FrameResourceTransaction.Create(
                () => new TrackingDisposable(
                    disposeFailure: new InvalidOperationException("texture cleanup failure")),
                () => new TrackingDisposable(),
                _ => throw primaryFailure));

        Assert.Same(primaryFailure, thrown);
    }

    [Fact]
    public void Data_contains_failure_does_not_replace_primary_failure()
    {
        var data = new ThrowingContainsDictionary();
        var primaryFailure = new ThrowingDataException(() => data);

        var thrown = Assert.Throws<ThrowingDataException>(() =>
            FrameResourceTransaction.Create(
                () => new TrackingDisposable(
                    disposeFailure: new InvalidOperationException("texture cleanup failure")),
                () => new TrackingDisposable(),
                _ => throw primaryFailure));

        Assert.Same(primaryFailure, thrown);
        Assert.Equal(1, data.ContainsCount);
    }

    [Fact]
    public void Data_indexer_getter_failure_does_not_replace_primary_failure()
    {
        var data = new ThrowingGetterDictionary();
        var primaryFailure = new ThrowingDataException(() => data);

        var thrown = Assert.Throws<ThrowingDataException>(() =>
            FrameResourceTransaction.Create(
                () => new TrackingDisposable(
                    disposeFailure: new InvalidOperationException("texture cleanup failure")),
                () => new TrackingDisposable(),
                _ => throw primaryFailure));

        Assert.Same(primaryFailure, thrown);
        Assert.Equal(1, data.GetterCount);
    }

    [Fact]
    public void Data_setter_failure_does_not_replace_primary_failure()
    {
        var primaryFailure = new ThrowingDataException(() => new ThrowingSetterDictionary());

        var thrown = Assert.Throws<ThrowingDataException>(() =>
            FrameResourceTransaction.Create(
                () => new TrackingDisposable(
                    disposeFailure: new InvalidOperationException("texture cleanup failure")),
                () => new TrackingDisposable(),
                _ => throw primaryFailure));

        Assert.Same(primaryFailure, thrown);
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
    public void Null_texture_result_does_not_run_later_steps()
    {
        var createSwapChainCount = 0;
        var bindCount = 0;

        var failure = Assert.Throws<InvalidOperationException>(() =>
            FrameResourceTransaction.Create<TrackingDisposable, TrackingDisposable>(
                () => null!,
                () =>
                {
                    createSwapChainCount++;
                    return new TrackingDisposable();
                },
                _ => bindCount++));

        Assert.Equal("Frame texture creation returned null.", failure.Message);
        Assert.Equal(0, createSwapChainCount);
        Assert.Equal(0, bindCount);
    }

    [Fact]
    public void Null_swap_chain_result_disposes_texture_without_binding()
    {
        var bindCount = 0;
        var texture = new TrackingDisposable();

        var failure = Assert.Throws<InvalidOperationException>(() =>
            FrameResourceTransaction.Create<TrackingDisposable, TrackingDisposable>(
                () => texture,
                () => null!,
                _ => bindCount++));

        Assert.Equal("DXGI swap-chain creation returned null.", failure.Message);
        Assert.Equal(1, texture.DisposeCount);
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
        public int ContainsCount { get; private set; }

        public override bool Contains(object key)
        {
            ContainsCount++;
            throw new InvalidOperationException("synthetic Data Contains failure");
        }
    }

    private sealed class ThrowingGetterDictionary : Hashtable
    {
        public int GetterCount { get; private set; }

        public override bool Contains(object key) => true;

        public override object? this[object key]
        {
            get
            {
                GetterCount++;
                throw new InvalidOperationException("synthetic Data indexer getter failure");
            }
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
