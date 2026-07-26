using WinARD.Application.Ports;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Services;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using WinARD.Remote.Protocol.Encodings;
using Xunit;
using System.Xml.Linq;
using System.Buffers.Binary;

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
    public void Remote_session_surface_exposes_an_automation_visible_cursor_overlay()
    {
        var sourcePath = FindRepositoryFile(
            "src",
            "WinARD.Desktop",
            "Views",
            "RemoteSessionWindow.xaml");
        var document = XDocument.Load(sourcePath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var overlay = document
            .Descendants(presentation + "Image")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "RemoteCursorOverlay");

        Assert.Equal(
            "RemoteCursorOverlay",
            (string?)overlay.Attribute("AutomationProperties.AutomationId"));
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
    public async Task Replacing_same_size_frame_unions_dirty_rectangles_without_forcing_full_frame()
    {
        var disposed = new List<int>();
        await using var mailbox = new LatestFrameMailbox();
        mailbox.Publish(Packet(1, 2, 1, [new RemoteRectangle(0, 0, 1, 1)], disposed));
        mailbox.Publish(Packet(2, 2, 1, [new RemoteRectangle(1, 0, 1, 1)], disposed));

        using var received = await mailbox.ReadLatestAsync(CancellationToken.None);

        Assert.Equal(
            [new RemoteRectangle(0, 0, 1, 1), new RemoteRectangle(1, 0, 1, 1)],
            received.DirtyRectangles);
    }

    [Fact]
    public async Task Replacing_same_size_frame_deduplicates_dirty_rectangles()
    {
        var disposed = new List<int>();
        await using var mailbox = new LatestFrameMailbox();
        var dirty = new RemoteRectangle(0, 0, 1, 1);
        mailbox.Publish(Packet(1, 2, 1, [dirty], disposed));
        mailbox.Publish(Packet(2, 2, 1, [dirty], disposed));

        using var received = await mailbox.ReadLatestAsync(CancellationToken.None);

        Assert.Equal([dirty], received.DirtyRectangles);
    }

    [Fact]
    public async Task Replacing_different_size_frame_marks_latest_frame_fully_dirty()
    {
        var disposed = new List<int>();
        await using var mailbox = new LatestFrameMailbox();
        mailbox.Publish(Packet(1, 2, 1, [new RemoteRectangle(0, 0, 1, 1)], disposed));
        mailbox.Publish(Packet(2, 3, 2, [new RemoteRectangle(1, 0, 1, 1)], disposed));

        using var received = await mailbox.ReadLatestAsync(CancellationToken.None);

        Assert.Equal([new RemoteRectangle(0, 0, 3, 2)], received.DirtyRectangles);
    }

    [Fact]
    public async Task Replacing_many_frames_keeps_accumulated_dirty_state_bounded()
    {
        var disposed = new List<int>();
        await using var mailbox = new LatestFrameMailbox();
        for (var sequence = 0; sequence < 400; sequence++)
        {
            var dirty = Enumerable.Range(0, 32)
                .Select(offset => (sequence * 32) + offset)
                .Select(index => new RemoteRectangle(index % 128, (index / 128) % 128, 1, 1))
                .ToArray();
            mailbox.Publish(Packet(
                sequence,
                128,
                128,
                dirty,
                disposed));
        }

        using var received = await mailbox.ReadLatestAsync(CancellationToken.None);

        Assert.Equal([new RemoteRectangle(0, 0, 128, 128)], received.DirtyRectangles);
        Assert.Equal(399, disposed.Count);
    }

    [Fact]
    public void Single_frame_packet_also_bounds_excessive_unique_dirty_rectangles()
    {
        var disposed = new List<int>();
        var dirty = Enumerable.Range(0, 300)
            .Select(index => new RemoteRectangle(index % 32, index / 32, 1, 1))
            .ToArray();

        using var packet = Packet(1, 32, 32, dirty, disposed);

        Assert.Equal([new RemoteRectangle(0, 0, 32, 32)], packet.DirtyRectangles);
    }

    [Fact]
    public async Task Replacing_frames_with_large_combined_coverage_collapses_to_full_frame()
    {
        var disposed = new List<int>();
        await using var mailbox = new LatestFrameMailbox();
        for (var sequence = 0; sequence < 8; sequence++)
        {
            mailbox.Publish(Packet(
                sequence,
                100,
                100,
                [new RemoteRectangle(sequence * 7, 0, 7, 100)],
                disposed));
        }

        using var received = await mailbox.ReadLatestAsync(CancellationToken.None);

        Assert.Equal([new RemoteRectangle(0, 0, 100, 100)], received.DirtyRectangles);
    }

    [Fact]
    public void Four_k_small_dirty_snapshot_rents_and_copies_once_then_transfers_owner_to_frame_packet()
    {
        const int width = 3840;
        const int height = 2160;
        var rentCount = 0;
        var copyCount = 0;
        RecordingOwner? rentedOwner = null;
        var snapshotFactory = new FramebufferSnapshotFactory(
            length =>
            {
                rentCount++;
                return rentedOwner = new RecordingOwner(length);
            },
            _ => copyCount++);
        using var framebuffer = new WinARD.Remote.Protocol.Framebuffer.Framebuffer(
            width,
            height,
            ProtocolLimits.Default);
        var dirty = new FramebufferRect(120, 80, 4, 3);
        var update = new FramebufferUpdateResult([dirty], [dirty], cursor: null, desktopResized: false);
        using var message = snapshotFactory.Create(framebuffer, update);

        using var packet = FramePacket.TakeFrom(sequence: 7, message);
        message.Dispose();

        Assert.Equal(1, rentCount);
        Assert.Equal(1, copyCount);
        Assert.Equal(checked(width * height * 4), rentedOwner!.Memory.Length);
        Assert.False(rentedOwner.IsDisposed);
        Assert.Equal([new RemoteRectangle(120, 80, 4, 3)], packet.DirtyRectangles);
        Assert.Equal(checked(width * height * 4), packet.Pixels.Length);

        packet.Dispose();
        Assert.True(rentedOwner.IsDisposed);
    }

    [Fact]
    public void Cursor_only_update_skips_framebuffer_snapshot_and_preserves_cursor_pixels()
    {
        var rentCount = 0;
        var copyCount = 0;
        var snapshotFactory = new FramebufferSnapshotFactory(
            length =>
            {
                rentCount++;
                return new RecordingOwner(length);
            },
            _ => copyCount++);
        using var framebuffer = new WinARD.Remote.Protocol.Framebuffer.Framebuffer(
            8,
            6,
            ProtocolLimits.Default);
        var cursor = new RemoteCursor(1, 0, 2, 1, [1, 2, 3, 4, 5, 6, 7, 8]);
        var update = new FramebufferUpdateResult([], [], cursor, desktopResized: false);

        using var message = Assert.IsType<RemoteCursorMessage>(
            snapshotFactory.CreateServerMessage(framebuffer, update));
        using var cursorUpdate = message.TakeCursorOwnership();

        Assert.Equal(0, rentCount);
        Assert.Equal(0, copyCount);
        Assert.True(cursorUpdate.IsVisible);
        Assert.Equal((1, 0, 2, 1),
            (cursorUpdate.HotspotX, cursorUpdate.HotspotY, cursorUpdate.Width, cursorUpdate.Height));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], cursorUpdate.Bgra32.ToArray());
    }

    [Fact]
    public void Mixed_framebuffer_and_cursor_update_delivers_both_payloads()
    {
        var snapshotFactory = new FramebufferSnapshotFactory();
        using var framebuffer = new WinARD.Remote.Protocol.Framebuffer.Framebuffer(
            2,
            1,
            ProtocolLimits.Default);
        var dirty = new FramebufferRect(0, 0, 1, 1);
        var cursor = new RemoteCursor(0, 0, 1, 1, [9, 8, 7, 6]);
        var update = new FramebufferUpdateResult([dirty], [dirty], cursor, desktopResized: false);

        using var message = Assert.IsType<RemoteFramebufferMessage>(
            snapshotFactory.CreateServerMessage(framebuffer, update));
        using var cursorUpdate = Assert.IsType<RemoteCursorUpdate>(message.TakeCursorOwnership());

        Assert.Equal([new RemoteRectangle(0, 0, 1, 1)], message.DirtyRectangles);
        Assert.Equal([9, 8, 7, 6], cursorUpdate.Bgra32.ToArray());
    }

    [Fact]
    public void Empty_cursor_update_is_delivered_as_hidden_without_framebuffer_copy()
    {
        var copyCount = 0;
        var snapshotFactory = new FramebufferSnapshotFactory(copyObserver: _ => copyCount++);
        using var framebuffer = new WinARD.Remote.Protocol.Framebuffer.Framebuffer(
            2,
            1,
            ProtocolLimits.Default);
        var update = new FramebufferUpdateResult(
            [],
            [],
            new RemoteCursor(0, 0, 0, 0, []),
            desktopResized: false);

        using var message = Assert.IsType<RemoteCursorMessage>(
            snapshotFactory.CreateServerMessage(framebuffer, update));
        using var cursorUpdate = message.TakeCursorOwnership();

        Assert.False(cursorUpdate.IsVisible);
        Assert.Empty(cursorUpdate.Bgra32.ToArray());
        Assert.Equal(0, copyCount);
    }

    [Fact]
    public void Cursor_contract_rejects_payload_above_the_protocol_cursor_limit()
    {
        const int width = 2048;
        const int height = 2049;
        var length = checked(width * height * 4);
        using var owner = new RecordingOwner(length);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RemoteCursorUpdate(0, 0, width, height, owner, length));
    }

    [Fact]
    public async Task Rfb_client_dispatches_cursor_only_update_without_snapshotting_the_desktop()
    {
        var snapshotCopies = 0;
        await using var stream = new ScriptedDuplexStream(
            [.. ServerInit(2, 1), .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(
            stream,
            new FramebufferSnapshotFactory(copyObserver: _ => snapshotCopies++));

        await client.InitializeAsync(CancellationToken.None);
        using var message = Assert.IsType<RemoteCursorMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        using var cursor = message.TakeCursorOwnership();

        Assert.Equal(0, snapshotCopies);
        Assert.Equal([1, 2, 3, 255], cursor.Bgra32.ToArray());
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

    private static byte[] ServerInit(ushort width, ushort height)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), height);
        PixelFormat.WinArdBgra32.ToWireBytes().CopyTo(bytes, 4);
        return bytes;
    }

    private static byte[] CursorOnlyUpdate()
    {
        var bytes = new byte[22];
        bytes[0] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), (int)RfbEncodingType.Cursor);
        bytes[16] = 1;
        bytes[17] = 2;
        bytes[18] = 3;
        bytes[19] = 0;
        bytes[20] = 0x80;
        return bytes;
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

    private sealed class RecordingOwner(int length) : System.Buffers.IMemoryOwner<byte>
    {
        private byte[]? _buffer = new byte[length];

        public bool IsDisposed => _buffer is null;

        public Memory<byte> Memory => _buffer ?? throw new ObjectDisposedException(nameof(RecordingOwner));

        public void Dispose() => _buffer = null;
    }

    private sealed class ScriptedDuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly MemoryStream _output = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _input.Dispose();
                _output.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
