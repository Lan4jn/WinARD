using WinARD.Application.Ports;
using WinARD.Desktop.Rendering;
using Xunit;
using System.Xml.Linq;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests;

public sealed class FramePresentationTests
{
    [Fact]
    public void Fit_maps_viewport_center_through_letterbox()
    {
        var transform = ViewportTransform.Create(
            1920, 1080, 1280, 800, 1, ViewportScaleMode.Fit);

        Assert.True(transform.TryMapToRemote(640, 400, out var point));
        Assert.Equal(new RemotePoint(960, 540), point);
    }

    [Fact]
    public void Fit_clamps_letterbox_and_remote_edges()
    {
        var transform = ViewportTransform.Create(
            1920, 1080, 1280, 800, 1, ViewportScaleMode.Fit);

        Assert.True(transform.TryMapToRemote(640, 0, out var top));
        Assert.True(transform.TryMapToRemote(2000, 900, out var bottomRight));
        Assert.Equal(new RemotePoint(960, 0), top);
        Assert.Equal(new RemotePoint(1919, 1079), bottomRight);
    }

    [Fact]
    public void Actual_size_accounts_for_dpi_and_zero_dimensions_are_invalid()
    {
        var transform = ViewportTransform.Create(
            1920, 1080, 1280, 800, 1.5, ViewportScaleMode.ActualSize);
        var invalid = ViewportTransform.Create(
            1920, 1080, 0, 800, 1, ViewportScaleMode.Fit);

        Assert.True(transform.TryMapToRemote(100, 40, out var point));
        Assert.Equal(new RemotePoint(150, 60), point);
        Assert.False(invalid.TryMapToRemote(0, 0, out _));
    }

    [Fact]
    public void Actual_size_maps_scrolled_viewport_to_remote_bottom_right()
    {
        var transform = ViewportTransform.Create(
            3840,
            2160,
            1280,
            720,
            1.5,
            ViewportScaleMode.ActualSize,
            scrollOffsetX: 1280,
            scrollOffsetY: 720);

        Assert.True(transform.TryMapToRemote(1279.9, 719.9, out var point));
        Assert.Equal(new RemotePoint(3839, 2159), point);
    }

    [Fact]
    public void Actual_size_clamps_scrolled_coordinates_to_remote_bounds()
    {
        var transform = ViewportTransform.Create(
            3840,
            2160,
            1280,
            720,
            1.5,
            ViewportScaleMode.ActualSize,
            scrollOffsetX: 1280,
            scrollOffsetY: 720);

        Assert.True(transform.TryMapToRemote(-5000, -5000, out var topLeft));
        Assert.True(transform.TryMapToRemote(5000, 5000, out var bottomRight));
        Assert.Equal(new RemotePoint(0, 0), topLeft);
        Assert.Equal(new RemotePoint(3839, 2159), bottomRight);
    }

    [Fact]
    public void Actual_size_layout_uses_remote_pixel_size_and_clamps_scroll_offsets()
    {
        var layout = ViewportLayout.Create(
            3840,
            2160,
            1280,
            720,
            1.5,
            ViewportScaleMode.ActualSize,
            scrollOffsetX: 5000,
            scrollOffsetY: 5000);

        Assert.True(layout.IsValid);
        Assert.True(layout.IsScrollingEnabled);
        Assert.Equal(2560, layout.SurfaceWidth);
        Assert.Equal(1440, layout.SurfaceHeight);
        Assert.Equal(1280, layout.ScrollOffsetX);
        Assert.Equal(720, layout.ScrollOffsetY);
    }

    [Fact]
    public void Fit_layout_fills_viewport_and_resets_scroll_offsets()
    {
        var layout = ViewportLayout.Create(
            3840,
            2160,
            1280,
            720,
            1.5,
            ViewportScaleMode.Fit,
            scrollOffsetX: 900,
            scrollOffsetY: 600);

        Assert.True(layout.IsValid);
        Assert.False(layout.IsScrollingEnabled);
        Assert.Equal(1280, layout.SurfaceWidth);
        Assert.Equal(720, layout.SurfaceHeight);
        Assert.Equal(0, layout.ScrollOffsetX);
        Assert.Equal(0, layout.ScrollOffsetY);
    }

    [Fact]
    public void Remote_session_surface_is_wrapped_in_an_automation_visible_scroll_viewer()
    {
        var sourcePath = FindRepositoryFile(
            "src",
            "WinARD.Desktop",
            "Views",
            "RemoteSessionWindow.xaml");
        var document = XDocument.Load(sourcePath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var scrollViewer = document
            .Descendants(presentation + "ScrollViewer")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "FrameScrollViewer");

        Assert.Equal(
            "RemoteFrameScrollViewer",
            (string?)scrollViewer.Attribute("AutomationProperties.AutomationId"));
        Assert.Contains(
            scrollViewer.Descendants(presentation + "SwapChainPanel"),
            element => (string?)element.Attribute(xaml + "Name") == "FramePanel");
    }

    [Fact]
    public void Dirty_rectangles_are_clipped_and_empty_rectangles_are_removed()
    {
        var clipped = FrameValidation.ClipDirtyRectangles(
            [
                new RemoteRectangle(-2, -1, 5, 4),
                new RemoteRectangle(9, 9, 5, 5),
                new RemoteRectangle(20, 20, 1, 1),
            ],
            10,
            10);

        Assert.Equal(
            [new RemoteRectangle(0, 0, 3, 3), new RemoteRectangle(9, 9, 1, 1)],
            clipped);
    }

    [Theory]
    [InlineData(2, 2, 8, 16)]
    [InlineData(2, 2, 12, 24)]
    public void Frame_validation_accepts_padded_stride(int width, int height, int stride, int length)
    {
        FrameValidation.ValidateBgra32(width, height, stride, length);
    }

    [Theory]
    [InlineData(2, 2, 7, 16)]
    [InlineData(2, 2, 8, 15)]
    [InlineData(int.MaxValue, 2, int.MaxValue, int.MaxValue)]
    public void Frame_validation_rejects_bad_stride_or_length(int width, int height, int stride, int length)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            FrameValidation.ValidateBgra32(width, height, stride, length));
    }

    [Fact]
    public async Task Latest_frame_mailbox_drops_and_disposes_old_frame_without_backpressure()
    {
        var disposed = new List<int>();
        await using var mailbox = new LatestFrameMailbox();
        mailbox.Publish(Packet(1, disposed));
        mailbox.Publish(Packet(2, disposed));

        using var received = await mailbox.ReadLatestAsync(CancellationToken.None);

        Assert.Equal(2, received.Sequence);
        Assert.Equal([1], disposed);
    }

    [Fact]
    public async Task Replacing_pending_frame_preserves_dirty_pixels_from_dropped_frame()
    {
        var disposed = new List<int>();
        await using var mailbox = new LatestFrameMailbox();
        mailbox.Publish(Packet(1, 2, 1, [new RemoteRectangle(0, 0, 1, 1)], disposed));
        mailbox.Publish(Packet(2, 2, 1, [new RemoteRectangle(1, 0, 1, 1)], disposed));

        using var received = await mailbox.ReadLatestAsync(CancellationToken.None);

        Assert.Equal([new RemoteRectangle(0, 0, 2, 1)], received.DirtyRectangles);
    }

    [Fact]
    public async Task Mailbox_dispose_releases_pending_frame_and_cancels_reader()
    {
        var disposed = new List<int>();
        var mailbox = new LatestFrameMailbox();
        mailbox.Publish(Packet(7, disposed));

        await mailbox.DisposeAsync();

        Assert.Equal([7], disposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await mailbox.ReadLatestAsync(CancellationToken.None));
    }

    private static FramePacket Packet(int sequence, List<int> disposed) =>
        new(sequence, 1, 1, 4, 4, [new RemoteRectangle(0, 0, 1, 1)], new TestOwner(sequence, disposed));

    private static FramePacket Packet(
        int sequence,
        int width,
        int height,
        IReadOnlyList<RemoteRectangle> dirtyRectangles,
        List<int> disposed) =>
        new(
            sequence,
            width,
            height,
            checked(width * 4),
            checked(width * height * 4),
            dirtyRectangles,
            new TestOwner(sequence, disposed, checked(width * height * 4)));

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file: {Path.Combine(segments)}");
    }

    private sealed class TestOwner(int sequence, List<int> disposed, int length = 4) : System.Buffers.IMemoryOwner<byte>
    {
        private byte[]? _buffer = new byte[length];

        public Memory<byte> Memory => _buffer ?? throw new ObjectDisposedException(nameof(TestOwner));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _buffer, null) is not null)
            {
                disposed.Add(sequence);
            }
        }
    }
}
