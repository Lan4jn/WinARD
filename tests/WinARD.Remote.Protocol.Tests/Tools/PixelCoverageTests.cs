using WinARD.ProtocolProbe;
using WinARD.Remote.Protocol.Framebuffer;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Tools;

public sealed class PixelCoverageTests
{
    [Fact]
    public void Coverage_tracks_partial_overlapping_and_complete_rectangles()
    {
        var coverage = new PixelCoverage(4, 2);

        coverage.Add(new FramebufferRect(0, 0, 2, 2), CancellationToken.None);
        coverage.Add(new FramebufferRect(1, 0, 2, 2), CancellationToken.None);

        Assert.False(coverage.IsComplete);
        Assert.Equal(6, coverage.CoveredPixels);

        coverage.Add(new FramebufferRect(3, 0, 1, 2), CancellationToken.None);

        Assert.True(coverage.IsComplete);
        Assert.Equal(8, coverage.CoveredPixels);
    }

    [Fact]
    public void Coverage_uses_fixed_bitset_for_large_sparse_updates()
    {
        const int size = 8192;
        var coverage = new PixelCoverage(size, size);

        for (var x = 0; x < size; x += 2)
        {
            coverage.Add(new FramebufferRect(x, 0, 1, size), CancellationToken.None);
        }

        Assert.False(coverage.IsComplete);
        Assert.Equal((long)size * size / 2, coverage.CoveredPixels);
        Assert.Equal(8 * 1024 * 1024, coverage.StorageByteLength);
    }

    [Fact]
    public void Coverage_add_observes_cancellation_before_large_scan()
    {
        var coverage = new PixelCoverage(8192, 8192);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() =>
            coverage.Add(new FramebufferRect(0, 0, 8192, 8192), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, coverage.CoveredPixels);
    }

    [Fact]
    public void Coverage_reset_rebuilds_storage_and_discards_previous_pixels()
    {
        var coverage = new PixelCoverage(4, 4);
        coverage.Add(new FramebufferRect(0, 0, 4, 4), CancellationToken.None);

        coverage.Reset(2, 1);

        Assert.False(coverage.IsComplete);
        Assert.Equal(0, coverage.CoveredPixels);
        Assert.Equal(sizeof(ulong), coverage.StorageByteLength);
    }
}
