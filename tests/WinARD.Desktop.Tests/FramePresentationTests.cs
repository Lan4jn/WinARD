using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Services;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Clipboard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.IO;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Infrastructure.Diagnostics;
using Xunit;
using System.Xml.Linq;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Buffers;
using System.IO.Compression;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests;

public sealed class FramePresentationTests
{
    [Fact]
    public async Task Production_client_exposes_only_confirmed_standard_quality_capabilities_after_initialization()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);

        var capabilities = client.QualityCapabilities;

        Assert.Equal(CapabilitySupport.Observed, capabilities.Zlib);
        Assert.Equal(CapabilitySupport.Observed, capabilities.Rgb565);
        Assert.False(capabilities.SafeOnlinePixelFormatSwitch);
        Assert.Equal(CapabilitySupport.Unknown, capabilities.ServerScaling);
        Assert.Equal(CapabilitySupport.Unknown, capabilities.AppleColor1002);
        Assert.Equal(CapabilitySupport.Unknown, capabilities.AppleGrayscale1001);
        Assert.False(capabilities.SafeOnlineScaleSwitch);
    }

    [Fact]
    public async Task Production_coordinator_rejects_q1_without_writing_set_pixel_format()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var transitionOffset = stream.WrittenBytes.Length;
        var runtime = new RfbClientRuntime(client);
        using var coordinator = new QualityTransitionCoordinator(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            client.QualityCapabilities,
            new QualityDecoderGates());
        var decision = new QualityDecision(
            1,
            QualityContentState.Idle,
            QualityLevel.Q1,
            QualityColor.Color16,
            QualityScale.Percent100,
            60,
            QualityDecisionReason.Initial,
            targetSatisfied: true,
            levelChanged: true,
            contentStateChanged: false,
            previousLevel: QualityLevel.Q0,
            previousContentState: QualityContentState.Idle);

        var result = await coordinator.ApplyAtSafeBoundaryAsync(
            decision,
            new QualityTransitionBoundary(
                FrameResponseCompletedAndPresented: true,
                HasOutstandingFramebufferRequest: false,
                HasActiveReceive: false,
                NextFramebufferRequestProduced: false),
            default);

        Assert.Equal(QualityTransitionStatus.ReconnectRequired, result);
        Assert.Equal(transitionOffset, stream.WrittenBytes.Length);
        var transitionWrites = stream.WrittenBytes[transitionOffset..];
        Assert.Empty(transitionWrites);
        Assert.False(ContainsSetPixelFormatMessage(transitionWrites));
    }
    [Fact]
    public void Remote_message_contracts_preserve_pre_statistics_constructor_signatures()
    {
        Assert.NotNull(typeof(RemoteCursorMessage).GetConstructor([typeof(RemoteCursorUpdate)]));
        Assert.NotNull(typeof(RemoteFramebufferMessage).GetConstructor(
        [
            typeof(RemoteFramebufferSize),
            typeof(byte[]),
            typeof(int),
            typeof(IReadOnlyList<RemoteRectangle>),
            typeof(RemoteCursorUpdate),
        ]));
        Assert.NotNull(typeof(RemoteFramebufferMessage).GetConstructor(
        [
            typeof(RemoteFramebufferSize),
            typeof(System.Buffers.IMemoryOwner<byte>),
            typeof(int),
            typeof(int),
            typeof(IReadOnlyList<RemoteRectangle>),
            typeof(RemoteCursorUpdate),
        ]));
    }

    [Fact]
    public void Remote_update_statistics_and_messages_publish_safe_snapshots()
    {
        var counts = new Dictionary<int, int> { [(int)RfbEncodingType.Raw] = 1 };
        var statistics = new RemoteUpdateStatistics(42, counts);
        counts[(int)RfbEncodingType.Raw] = 99;

        using var cursorMessage = new RemoteCursorMessage(
            new RemoteCursorUpdate(0, 0, 0, 0, []),
            statistics);
        using var framebufferMessage = new RemoteFramebufferMessage(
            new RemoteFramebufferSize(1, 1),
            new byte[4],
            4,
            [],
            cursor: null,
            statistics: statistics);

        Assert.Equal(42, cursorMessage.Statistics.ReceivedSessionBytes);
        Assert.Equal(1, cursorMessage.Statistics.EncodingCounts[(int)RfbEncodingType.Raw]);
        Assert.Equal(42, framebufferMessage.Statistics.ReceivedSessionBytes);
        Assert.Equal(1, framebufferMessage.Statistics.EncodingCounts[(int)RfbEncodingType.Raw]);
        using var legacyMessage = new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 0, 0, []));
        Assert.Empty(legacyMessage.Statistics.EncodingCounts);
    }

    [Fact]
    public void Remote_update_statistics_preserves_legacy_constructor_and_deconstruct()
    {
        Assert.NotNull(typeof(RemoteUpdateStatistics).GetConstructor(
            [typeof(long), typeof(IReadOnlyDictionary<int, int>)]));

        var statistics = new RemoteUpdateStatistics(42, new Dictionary<int, int>());
        var (receivedSessionBytes, encodingCounts) = statistics;

        Assert.Equal(42, receivedSessionBytes);
        Assert.Empty(encodingCounts);
        Assert.Same(RemoteFramebufferTransferStatistics.Empty, statistics.TransferStatistics);
    }

    [Fact]
    public void Remote_transfer_statistics_validate_dictionary_arguments_and_values()
    {
        var counts = new Dictionary<int, int> { [0] = 1 };
        var bytes = new Dictionary<int, long> { [0] = 0 };

        Assert.Equal("rectangleCounts", Assert.Throws<ArgumentNullException>(() =>
            new RemoteFramebufferTransferStatistics(0, 0, 0, 0, null, null!, bytes, bytes)).ParamName);
        Assert.Equal("wirePayloadBytesByEncoding", Assert.Throws<ArgumentNullException>(() =>
            new RemoteFramebufferTransferStatistics(0, 0, 0, 0, null, counts, null!, bytes)).ParamName);
        Assert.Equal("pixelWireBytesByEncoding", Assert.Throws<ArgumentNullException>(() =>
            new RemoteFramebufferTransferStatistics(0, 0, 0, 0, null, counts, bytes, null!)).ParamName);
        Assert.Equal("rectangleCounts", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RemoteFramebufferTransferStatistics(
                0, 0, 0, 0, null, new Dictionary<int, int> { [0] = -1 }, bytes, bytes)).ParamName);
        Assert.Equal("wirePayloadBytesByEncoding", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RemoteFramebufferTransferStatistics(
                0, 0, 0, 0, null, counts, new Dictionary<int, long> { [0] = -1 }, bytes)).ParamName);
        Assert.Equal("pixelWireBytesByEncoding", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RemoteFramebufferTransferStatistics(
                0, 0, 0, 0, null, counts, bytes, new Dictionary<int, long> { [0] = -1 })).ParamName);
        Assert.False(typeof(RemoteUpdateStatistics).GetProperty(
            nameof(RemoteUpdateStatistics.TransferStatistics))!.CanWrite);
    }

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
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1), .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(
            stream,
            new FramebufferSnapshotFactory(copyObserver: _ => snapshotCopies++));

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        using var message = Assert.IsType<RemoteCursorMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        using var cursor = message.TakeCursorOwnership();

        Assert.Equal(0, snapshotCopies);
        Assert.Equal([1, 2, 3, 255], cursor.Bgra32.ToArray());
        Assert.Equal(21, message.Statistics.ReceivedSessionBytes);
        Assert.Equal(1, message.Statistics.EncodingCounts[(int)RfbEncodingType.Cursor]);
    }

    [Fact]
    public void Snapshot_factory_marks_a_nonpixel_framebuffer_update_as_nonpixel()
    {
        using var framebuffer = new Framebuffer(1, 1, ProtocolLimits.Default);
        var snapshotFactory = new FramebufferSnapshotFactory();
        var update = new FramebufferUpdateResult([], [], cursor: null, desktopResized: true);

        using var message = Assert.IsType<RemoteFramebufferMessage>(
            snapshotFactory.CreateServerMessage(framebuffer, update));

        Assert.False(message.HasPixelContent);
    }

    [Fact]
    public async Task Rfb_client_allows_a_second_full_request_after_cursor_only_then_delivers_pixels()
    {
        var rawUpdate = new List<byte> { 0, 0, 0, 1 };
        rawUpdate.AddRange(Header(0, 0, 1, 1, (int)RfbEncodingType.Raw));
        rawUpdate.AddRange([1, 2, 3, 0]);
        await using var stream = new ScriptedDuplexStream(
        [
            .. Handshake("RFB 003.008\n"),
            .. ServerInit(1, 1),
            .. CursorOnlyUpdate(),
            .. rawUpdate,
        ]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        await ConfigureDefaultBootstrapAsync(client);
        var requestOffset = stream.WrittenBytes.Length;

        await client.RequestFramebufferUpdateAsync(incremental: false, default);
        using var cursor = Assert.IsType<RemoteCursorMessage>(await client.ReceiveAsync(default));
        await client.RequestFramebufferUpdateAsync(incremental: false, default);
        using var pixels = Assert.IsType<RemoteFramebufferMessage>(await client.ReceiveAsync(default));

        Assert.True(pixels.HasPixelContent);
        Assert.Equal(
        [
            3, 0, 0, 0, 0, 0, 0, 1, 0, 1,
            3, 0, 0, 0, 0, 0, 0, 1, 0, 1,
        ], stream.WrittenBytes[requestOffset..]);
    }

    [Fact]
    public async Task Rfb_client_maps_raw_and_zlib_transfer_statistics_to_application_snapshot()
    {
        var (compressed, _) = CreateSharedZlibChunks([4, 5, 6, 0], [7, 8, 9, 0]);
        var update = new List<byte> { 0, 0, 0, 2 };
        update.AddRange(Header(0, 0, 1, 1, (int)RfbEncodingType.Raw));
        update.AddRange([1, 2, 3, 0]);
        update.AddRange(Header(1, 0, 1, 1, (int)RfbEncodingType.Zlib));
        var length = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)compressed.Length));
        update.AddRange(length);
        update.AddRange(compressed);
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1), .. update]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        await client.RequestFramebufferUpdateAsync(incremental: true, CancellationToken.None);
        using var message = Assert.IsType<RemoteFramebufferMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        var statistics = message.Statistics.TransferStatistics;

        Assert.Equal(2, statistics.RectangleCount);
        Assert.Equal(8 + compressed.Length, statistics.WirePayloadBytes);
        Assert.Equal(8, statistics.PixelWireBytes);
        Assert.Equal(2, statistics.PixelArea);
        Assert.Equal((8L + compressed.Length) * 500, statistics.BytesPerPixelMilli);
        Assert.Equal(1, statistics.RectangleCounts[(int)RfbEncodingType.Raw]);
        Assert.Equal(1, statistics.RectangleCounts[(int)RfbEncodingType.Zlib]);
        Assert.Equal(4, statistics.WirePayloadBytesByEncoding[(int)RfbEncodingType.Raw]);
        Assert.Equal(4 + compressed.Length, statistics.WirePayloadBytesByEncoding[(int)RfbEncodingType.Zlib]);
        Assert.Equal(4, statistics.PixelWireBytesByEncoding[(int)RfbEncodingType.Raw]);
        Assert.Equal(4, statistics.PixelWireBytesByEncoding[(int)RfbEncodingType.Zlib]);
        var mutableCounts = Assert.IsAssignableFrom<IDictionary<int, int>>(statistics.RectangleCounts);
        var mutableWire = Assert.IsAssignableFrom<IDictionary<int, long>>(
            statistics.WirePayloadBytesByEncoding);
        var mutablePixels = Assert.IsAssignableFrom<IDictionary<int, long>>(
            statistics.PixelWireBytesByEncoding);
        Assert.True(mutableCounts.IsReadOnly);
        Assert.True(mutableWire.IsReadOnly);
        Assert.True(mutablePixels.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableCounts.Add(16, 1));
        Assert.Throws<NotSupportedException>(() => mutableWire.Add(16, 1));
        Assert.Throws<NotSupportedException>(() => mutablePixels.Add(16, 1));
    }

    [Fact]
    public async Task Rfb_client_counts_one_encrypted_transport_read_once_across_consecutive_updates()
    {
        var authenticationKey = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var sessionKey = Enumerable.Range(32, 16).Select(value => (byte)value).ToArray();
        var sessionIv = Enumerable.Range(64, 16).Select(value => (byte)value).ToArray();
        var encryptedUpdates = ArdEncryptedPacketCodec.Encrypt(
            sessionKey,
            sessionIv,
            0,
            [.. CursorOnlyUpdate(), .. CursorOnlyUpdate()]);
        await using var stream = new ScriptedDuplexStream(
        [
            .. Handshake("RFB 003.889\n"),
            .. ArdServerInit(2, 1),
            .. ArdSessionEncryptionUpdate(authenticationKey, sessionKey, sessionIv),
            .. encryptedUpdates,
        ]);
        await using var client = new RfbClient(
            stream,
            authenticationResult: new ArdAuthenticationResult(authenticationKey));

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        await ConfigureDefaultBootstrapAsync(client);
        using var activation = Assert.IsType<RemoteFramebufferMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        using var first = Assert.IsType<RemoteCursorMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        using var second = Assert.IsType<RemoteCursorMessage>(
            await client.ReceiveAsync(CancellationToken.None));

        Assert.True(first.Statistics.ReceivedSessionBytes >= 0);
        Assert.True(second.Statistics.ReceivedSessionBytes >= 0);
        Assert.Equal(
            encryptedUpdates.Length,
            first.Statistics.ReceivedSessionBytes + second.Statistics.ReceivedSessionBytes);
        Assert.Equal(encryptedUpdates.Length, first.Statistics.ReceivedSessionBytes);
        Assert.Equal(0, second.Statistics.ReceivedSessionBytes);
        Assert.Equal(1, first.Statistics.EncodingCounts[(int)RfbEncodingType.Cursor]);
        Assert.Equal(1, second.Statistics.EncodingCounts[(int)RfbEncodingType.Cursor]);
    }

    [Fact]
    public async Task Rfb_client_carries_bell_and_clipboard_bytes_into_the_next_frame_once()
    {
        byte[] clipboard = [3, 0, 0, 0, 0, 0, 0, 0];
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1), 2, .. clipboard, .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        Assert.IsType<RemoteBellMessage>(await client.ReceiveAsync(CancellationToken.None));
        Assert.IsType<RemoteClipboardMessage>(await client.ReceiveAsync(CancellationToken.None));
        using var frame = Assert.IsType<RemoteCursorMessage>(
            await client.ReceiveAsync(CancellationToken.None));

        Assert.Equal(1 + clipboard.Length + CursorOnlyUpdate().Length,
            frame.Statistics.ReceivedSessionBytes);
    }

    [Fact]
    public async Task Rfb_client_initialize_before_negotiate_fails_without_io()
    {
        await using var stream = new ScriptedDuplexStream([]);
        await using var client = new RfbClient(stream);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.InitializeAsync(CancellationToken.None));

        Assert.Contains("negotiation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stream.WrittenBytes);
    }

    [Fact]
    public async Task Rfb_client_routes_889_handshake_to_ARD_initializer()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        Assert.Equal(new RemoteFramebufferSize(2, 1), client.FramebufferSize);
        Assert.Equal((byte)ArdClientInitFlags.Ard, stream.WrittenBytes[13]);
    }

    [Fact]
    public async Task Rfb_client_rejects_889_initialization_without_authentication_material()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClientFactory().Create(stream);

        await client.NegotiateAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.InitializeAsync(CancellationToken.None));

        Assert.Equal(RfbProtocolFailureKind.ArdEncryptionNegotiation, exception.Failure?.Kind);
    }

    [Fact]
    public async Task Initialize_does_not_publish_runtime_resources_after_shutdown_starts()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        var client = new RfbClient(stream);
        await client.NegotiateAsync(CancellationToken.None);
        var blockedRead = stream.BlockReadAfter(4);
        var initialize = client.InitializeAsync(CancellationToken.None);
        await blockedRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        client.BeginShutdown();
        Exception? initializeFailure;
        try
        {
            blockedRead.Release.TrySetResult();
            initializeFailure = await Record.ExceptionAsync(
                () => initialize.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            blockedRead.Release.TrySetResult();
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsType<ObjectDisposedException>(initializeFailure);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = client.RequestFramebufferUpdateAsync(
                incremental: true,
                CancellationToken.None).AsTask();
        });
    }

    [Fact]
    public async Task Rfb_client_encrypts_pointer_and_key_after_1103_activation()
    {
        var authenticationKey = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var sessionKey = Enumerable.Range(32, 16).Select(value => (byte)value).ToArray();
        var sessionIv = Enumerable.Range(64, 16).Select(value => (byte)value).ToArray();
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.889\n"),
                .. ArdServerInit(2, 1),
                .. ArdSessionEncryptionUpdate(authenticationKey, sessionKey, sessionIv),
            ]);
        await using var client = new RfbClient(
            stream,
            authenticationResult: new ArdAuthenticationResult(authenticationKey));

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        await ConfigureDefaultBootstrapAsync(client);
        using var frame = Assert.IsType<RemoteFramebufferMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        await client.SendPointerAsync(0, 100, 200, CancellationToken.None);
        await client.SendKeyAsync(0x61, true, CancellationToken.None);

        var wire = stream.WrittenBytes;
        byte[] acknowledgement = [0x12, 0, 0, 2, 0, 1, 0, 0];
        var acknowledgementOffset = FindSequence(wire, acknowledgement);
        Assert.True(acknowledgementOffset >= 0);
        var encrypted = wire[(acknowledgementOffset + acknowledgement.Length)..];
        var firstLength = BinaryPrimitives.ReadUInt16BigEndian(encrypted);
        using var pointer = ArdEncryptedPacketCodec.Decrypt(
            sessionKey,
            sessionIv,
            0,
            encrypted.AsSpan(2, firstLength));
        var secondOffset = 2 + firstLength;
        var secondLength = BinaryPrimitives.ReadUInt16BigEndian(encrypted.AsSpan(secondOffset));
        using var key = ArdEncryptedPacketCodec.Decrypt(
            sessionKey,
            pointer.NextIv,
            1,
            encrypted.AsSpan(secondOffset + 2, secondLength));

        Assert.Equal(new byte[] { 5, 0, 0, 100, 0, 200 }, pointer.Payload);
        Assert.Equal(new byte[] { 4, 1, 0, 0, 0, 0, 0, 0x61 }, key.Payload);
    }

    [Fact]
    public async Task Rfb_client_waits_for_later_1103_before_sending_sensitive_messages()
    {
        var authenticationKey = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var sessionKey = Enumerable.Range(32, 16).Select(value => (byte)value).ToArray();
        var sessionIv = Enumerable.Range(64, 16).Select(value => (byte)value).ToArray();
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.889\n"),
                .. ArdServerInit(2, 1),
                0, 0, 0, 0,
                .. ArdSessionEncryptionUpdate(authenticationKey, sessionKey, sessionIv),
            ]);
        await using var client = new RfbClient(
            stream,
            authenticationResult: new ArdAuthenticationResult(authenticationKey));

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        await ConfigureDefaultBootstrapAsync(client);
        using var firstFrame = Assert.IsType<RemoteFramebufferMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        var plaintextLength = stream.WrittenBytes.Length;

        var pointerWrite = client.SendPointerAsync(0, 100, 200, CancellationToken.None).AsTask();
        var keyWrite = client.SendKeyAsync(0x61, true, CancellationToken.None).AsTask();
        var clipboardWrite = client.SendClipboardTextAsync("local", CancellationToken.None).AsTask();

        Assert.False(pointerWrite.IsCompleted);
        Assert.False(keyWrite.IsCompleted);
        Assert.False(clipboardWrite.IsCompleted);
        Assert.Equal(plaintextLength, stream.WrittenBytes.Length);

        using var encryptionFrame = Assert.IsType<RemoteFramebufferMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        await Task.WhenAll(pointerWrite, keyWrite, clipboardWrite);

        byte[] acknowledgement = [0x12, 0, 0, 2, 0, 1, 0, 0];
        var acknowledgementOffset = FindSequence(stream.WrittenBytes, acknowledgement);
        Assert.True(acknowledgementOffset >= plaintextLength);
        var encrypted = stream.WrittenBytes[(acknowledgementOffset + acknowledgement.Length)..];
        var decodedPayloads = new List<byte[]>();
        var offset = 0;
        var sequence = 0u;
        var iv = sessionIv.ToArray();
        while (offset < encrypted.Length)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(encrypted.AsSpan(offset));
            using var packet = ArdEncryptedPacketCodec.Decrypt(
                sessionKey,
                iv,
                sequence,
                encrypted.AsSpan(offset + 2, length));
            decodedPayloads.Add(packet.Payload.ToArray());
            iv = packet.NextIv.ToArray();
            offset += 2 + length;
            sequence++;
        }

        Assert.Equal(3, decodedPayloads.Count);
        Assert.Contains(decodedPayloads, payload => payload.SequenceEqual(new byte[] { 5, 0, 0, 100, 0, 200 }));
        Assert.Contains(decodedPayloads, payload => payload.SequenceEqual(new byte[] { 4, 1, 0, 0, 0, 0, 0, 0x61 }));
        Assert.Contains(decodedPayloads, payload => payload.SequenceEqual(ClipboardProtocol.EncodeClientCutText("local")));
    }

    [Fact]
    public async Task Pending_encryption_does_not_block_framebuffer_request_that_delivers_1103()
    {
        var authenticationKey = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var sessionKey = Enumerable.Range(32, 16).Select(value => (byte)value).ToArray();
        var sessionIv = Enumerable.Range(64, 16).Select(value => (byte)value).ToArray();
        await using var stream = new InteractiveDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(
            stream,
            authenticationResult: new ArdAuthenticationResult(authenticationKey));

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        await ConfigureDefaultBootstrapAsync(client);
        stream.DeliverAfterFramebufferRequest(
            ArdSessionEncryptionUpdate(authenticationKey, sessionKey, sessionIv));

        var input = client.SendPointerAsync(0, 1, 1, CancellationToken.None).AsTask();
        var receive = client.ReceiveAsync(CancellationToken.None).AsTask();
        var request = client.RequestFramebufferUpdateAsync(
            incremental: true,
            CancellationToken.None).AsTask();
        var requestObserved = false;
        try
        {
            await stream.FramebufferRequestObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
            requestObserved = true;
        }
        finally
        {
            if (!requestObserved)
            {
                stream.DeliverWithoutFramebufferRequest();
            }
        }

        using var activation = Assert.IsType<RemoteFramebufferMessage>(
            await receive.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.WhenAll(input, request).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(requestObserved);
    }

    [Fact]
    public async Task Rfb_client_records_successful_889_control_negotiation_without_remote_identity()
    {
        var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(stream, diagnosticSink: sink);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var diagnostic = Assert.Single(sink.Snapshot());
        Assert.Equal("RFB_SESSION_NEGOTIATED", diagnostic.Code);
        Assert.Contains(diagnostic.Fields, field => field.Name == "ProtocolVersion" && field.Value == "003.889");
        Assert.Contains(diagnostic.Fields, field => field.Name == "ClientInit" && field.Value == "0xC1");
        Assert.Contains(diagnostic.Fields, field => field.Name == "MayControl" && field.Value == "True");
        Assert.Contains(diagnostic.Fields, field => field.Name == "SessionSelectRequired" && field.Value == "False");
        Assert.Contains(diagnostic.Fields, field => field.Name == "SessionSelectCompleted" && field.Value == "False");
        Assert.Contains(diagnostic.Fields, field => field.Name == "RequestedMode" && field.Value == "Shared");
        Assert.Contains(diagnostic.Fields, field => field.Name == "FinalState" && field.Value == "SharedControlNegotiated");
        Assert.DoesNotContain("Mac", string.Join('|', diagnostic.Fields.Select(field => field.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rfb_client_records_standard_initialization_without_ard_claims()
    {
        var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream, diagnosticSink: sink);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var diagnostic = Assert.Single(sink.Snapshot());
        Assert.Contains(diagnostic.Fields, field => field.Name == "ProtocolVersion" && field.Value == "003.008");
        Assert.Contains(diagnostic.Fields, field => field.Name == "ClientInit" && field.Value == "0x01");
        Assert.Contains(diagnostic.Fields, field => field.Name == "MayControl" && field.Value == "NotApplicable");
        Assert.Contains(diagnostic.Fields, field => field.Name == "FinalState" && field.Value == "Initialized");
    }

    [Fact]
    public async Task Failed_889_initialization_does_not_record_success_or_display_name()
    {
        var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
        var serverInit = ArdServerInit(2, 1);
        serverInit[29] = 0;
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. serverInit]);
        await using var client = new RfbClient(stream, diagnosticSink: sink);

        await client.NegotiateAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ArdControlNotAllowedException>(() =>
            client.InitializeAsync(CancellationToken.None));

        Assert.Empty(sink.Snapshot());
    }

    [Theory]
    [InlineData((byte)0x04)]
    [InlineData((byte)0x07)]
    public async Task Rfb_client_skips_zero_payload_ARD_control_message_before_framebuffer_update(
        byte controlMessage)
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1), controlMessage, .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        using var message = Assert.IsType<RemoteCursorMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        using var cursor = message.TakeCursorOwnership();

        Assert.Equal([1, 2, 3, 255], cursor.Bgra32.ToArray());
        Assert.Equal(1 + CursorOnlyUpdate().Length, message.Statistics.ReceivedSessionBytes);
    }

    [Fact]
    public async Task Rfb_client_skips_consecutive_ARD_ack_and_nop_messages()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1), 0x04, 0x07, .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        using var message = Assert.IsType<RemoteCursorMessage>(
            await client.ReceiveAsync(CancellationToken.None));

        Assert.NotNull(message.Cursor);
    }

    [Fact]
    public async Task Rfb_client_replies_to_ARD_tickle_and_continues_to_framebuffer_update()
    {
        var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.889\n"),
                .. ArdServerInit(2, 1),
                .. ArdStateChange(flags: 0x1234, status: 4, extra: [0xAA]),
                .. CursorOnlyUpdate(),
            ]);
        await using var client = new RfbClient(stream, diagnosticSink: sink);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        var outputOffset = stream.WrittenBytes.Length;
        using var message = Assert.IsType<RemoteCursorMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        using var cursor = message.TakeCursorOwnership();

        Assert.Equal([1, 2, 3, 255], cursor.Bgra32.ToArray());
        Assert.Equal(
            ArdStateChange(flags: 0x1234, status: 4, extra: [0xAA]).Length + CursorOnlyUpdate().Length,
            message.Statistics.ReceivedSessionBytes);
        Assert.Equal(
            [0x09, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1],
            stream.WrittenBytes[outputOffset..]);
        var diagnostic = Assert.Single(sink.Snapshot(), item => item.Code == "ARD_STATE_CHANGE");
        Assert.Equal("ARD StateChange message processed.", diagnostic.Message);
        Assert.Equal(
            new[]
            {
                ("Status", "4"),
                ("Flags", "0x1234"),
                ("PayloadSize", "5"),
                ("Action", "AutoFBUpdateSent"),
            },
            diagnostic.Fields.Select(field => (field.Name, field.Value)));
        Assert.All(diagnostic.Fields, field => Assert.Equal(DiagnosticFieldCategory.Public, field.Category));
    }

    [Theory]
    [InlineData((ushort)2, "Consumed")]
    [InlineData((ushort)3, "Consumed")]
    [InlineData((ushort)5, "Consumed")]
    [InlineData((ushort)6, "Consumed")]
    [InlineData((ushort)11, "Consumed")]
    [InlineData((ushort)12, "Consumed")]
    [InlineData((ushort)0x7FFF, "UnknownConsumed")]
    public async Task Rfb_client_consumes_nonterminating_ARD_state_changes(
        ushort status,
        string expectedAction)
    {
        var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.889\n"),
                .. ArdServerInit(2, 1),
                .. ArdStateChange(flags: 0x00F1, status, extra: [0x5A]),
                .. CursorOnlyUpdate(),
            ]);
        await using var client = new RfbClient(stream, diagnosticSink: sink);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        using var message = Assert.IsType<RemoteCursorMessage>(
            await client.ReceiveAsync(CancellationToken.None));

        var diagnostic = Assert.Single(sink.Snapshot(), item => item.Code == "ARD_STATE_CHANGE");
        Assert.Contains(diagnostic.Fields, field => field.Name == "Status" && field.Value == status.ToString(CultureInfo.InvariantCulture));
        Assert.Contains(diagnostic.Fields, field => field.Name == "Flags" && field.Value == "0x00F1");
        Assert.Contains(diagnostic.Fields, field => field.Name == "PayloadSize" && field.Value == "5");
        Assert.Contains(diagnostic.Fields, field => field.Name == "Action" && field.Value == expectedAction);
        Assert.DoesNotContain(diagnostic.Fields, field => field.Name is "Padding" or "Extra" or "ExtraPayload");
    }

    [Fact]
    public async Task Rfb_client_reports_ARD_local_user_closed_as_remote_session_closed()
    {
        var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.889\n"),
                .. ArdServerInit(2, 1),
                .. ArdStateChange(flags: 0xABCD, status: 1, extra: [0x11, 0x22]),
            ]);
        await using var client = new RfbClient(stream, diagnosticSink: sink);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.RemoteSessionClosed, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ArdStateChangePayload, exception.Failure?.ReadStage);
        Assert.Equal(ArdServerMessage.StateChangeType, exception.Failure?.ServerMessageType);
        var diagnostic = Assert.Single(sink.Snapshot(), item => item.Code == "ARD_STATE_CHANGE");
        Assert.Contains(diagnostic.Fields, field => field.Name == "Status" && field.Value == "1");
        Assert.Contains(diagnostic.Fields, field => field.Name == "Flags" && field.Value == "0xABCD");
        Assert.Contains(diagnostic.Fields, field => field.Name == "PayloadSize" && field.Value == "6");
        Assert.Contains(diagnostic.Fields, field => field.Name == "Action" && field.Value == "RemoteSessionClosed");
    }

    [Fact]
    public async Task Rfb_client_propagates_ARD_tickle_write_failure_and_records_safe_diagnostic()
    {
        const string privateMarker = "PRIVATE-STATE-CHANGE-EXTRA";
        var expected = new IOException("Injected AutoFBUpdate failure.");
        var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.889\n"),
                .. ArdServerInit(2, 1),
                .. ArdStateChange(
                    flags: 0x3456,
                    status: 4,
                    extra: System.Text.Encoding.ASCII.GetBytes(privateMarker)),
            ]);
        await using var client = new RfbClient(stream, diagnosticSink: sink);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        stream.FailNextWrite(expected);

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Same(expected, actual);
        var diagnostic = Assert.Single(sink.Snapshot(), item => item.Code == "ARD_STATE_CHANGE");
        Assert.Equal("IOException", diagnostic.Exception?.Type);
        Assert.Contains(diagnostic.Fields, field => field.Name == "Status" && field.Value == "4");
        Assert.Contains(diagnostic.Fields, field => field.Name == "Flags" && field.Value == "0x3456");
        Assert.Contains(diagnostic.Fields, field => field.Name == "PayloadSize" && field.Value == "30");
        Assert.Contains(diagnostic.Fields, field => field.Name == "Action" && field.Value == "AutoFBUpdateFailed");
        Assert.DoesNotContain(privateMarker, JsonSerializer.Serialize(sink.Snapshot()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rfb_client_does_not_record_ARD_tickle_write_cancellation_as_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.889\n"),
                .. ArdServerInit(2, 1),
                .. ArdStateChange(flags: 0, status: 4, extra: [0xAA]),
            ],
            cancellation);
        await using var client = new RfbClient(stream, diagnosticSink: sink);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ReceiveAsync(cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.DoesNotContain(sink.Snapshot(), item => item.Code == "ARD_STATE_CHANGE");
    }

    [Fact]
    public async Task Rfb_client_rejects_ARD_state_change_for_standard_RFB_version()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1), .. ArdStateChange(flags: 0, status: 4)]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.UnexpectedServerMessage, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ServerMessageType, exception.Failure?.ReadStage);
        Assert.Equal(ArdServerMessage.StateChangeType, exception.Failure?.ServerMessageType);
    }

    [Fact]
    public async Task Rfb_client_rejects_ARD_state_change_payload_smaller_than_fixed_fields()
    {
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.889\n"),
                .. ArdServerInit(2, 1),
                ArdServerMessage.StateChangeType, 0, 0, 3,
            ]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.MalformedArdStateChange, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ArdStateChangeHeader, exception.Failure?.ReadStage);
        Assert.Equal(ArdServerMessage.StateChangeType, exception.Failure?.ServerMessageType);
    }

    [Fact]
    public async Task Rfb_client_preserves_ARD_state_change_fixed_payload_truncation()
    {
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.889\n"),
                .. ArdServerInit(2, 1),
                ArdServerMessage.StateChangeType, 0, 0, 4, 0x12, 0x34, 0,
            ]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ArdStateChangePayload, exception.Failure?.ReadStage);
        Assert.Equal(ArdServerMessage.StateChangeType, exception.Failure?.ServerMessageType);
    }

    [Fact]
    public async Task Rfb_client_preserves_ARD_state_change_extra_payload_truncation()
    {
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.889\n"),
                .. ArdServerInit(2, 1),
                ArdServerMessage.StateChangeType, 0, 0, 6, 0x12, 0x34, 0, 4, 0xAA,
            ]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ArdStateChangePayload, exception.Failure?.ReadStage);
        Assert.Equal(ArdServerMessage.StateChangeType, exception.Failure?.ServerMessageType);
    }

    [Fact]
    public async Task Rfb_client_uses_resized_dimensions_and_requested_incremental_flag()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(1, 1), .. DesktopSizeUpdate(2, 1), .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        using var frame = Assert.IsType<RemoteFramebufferMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        await client.RequestFramebufferUpdateAsync(incremental: false, CancellationToken.None);
        using var repair = Assert.IsType<RemoteCursorMessage>(await client.ReceiveAsync(CancellationToken.None));
        await client.RequestFramebufferUpdateAsync(incremental: true, CancellationToken.None);

        Assert.Equal(new RemoteFramebufferSize(2, 1), frame.Size);
        Assert.Equal(
            [3, 0, 0, 0, 0, 0, 0, 2, 0, 1, 3, 1, 0, 0, 0, 0, 0, 2, 0, 1],
            stream.WrittenBytes[^20..]);
    }

    [Fact]
    public async Task Rfb_client_configures_rgb565_bootstrap_before_the_first_full_request()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var initializationLength = stream.WrittenBytes.Length;
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth);

        await client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, default);
        await client.RequestFramebufferUpdateAsync(incremental: false, default);

        Assert.Equal(
            BootstrapWire(PixelFormat.WinArdRgb565, settings.Encodings, 2, 1),
            stream.WrittenBytes[initializationLength..]);
        Assert.Equal(QualityBootstrapAttempt.Preferred, client.BootstrapState.Attempt);
        Assert.Equal(RemotePixelFormatKind.Rgb565, client.BootstrapState.ActualQuality.PixelFormat);
        Assert.Equal(settings.Encodings, client.BootstrapState.ActualQuality.Encodings);
    }

    [Theory]
    [InlineData(0.75d)]
    [InlineData(0.5d)]
    [InlineData(0.25d)]
    public async Task Ard_bootstrap_writes_scaling_before_pixel_format_and_first_request(double scaleFactor)
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(4, 2)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var initializationLength = stream.WrittenBytes.Length;
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth,
            scaleFactor);

        await client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, default);
        await client.RequestFramebufferUpdateAsync(incremental: false, default);

        Assert.Equal(
            BootstrapWire(PixelFormat.WinArdRgb565, settings.Encodings, 4, 2, scaleFactor),
            stream.WrittenBytes[initializationLength..]);
        Assert.False(client.BootstrapState.IsFirstPixelConfirmed);
        Assert.Null(client.BootstrapState.AppliedScaleFactor);
    }

    [Fact]
    public async Task Ard_bootstrap_at_one_hundred_percent_omits_scaling_message()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var initializationLength = stream.WrittenBytes.Length;
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Bgra32,
            [6, 16, 0, 1, -239, -223],
            QualityBootstrapReason.SafeFallback,
            1d);

        await client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Fallback, default);

        Assert.Equal(
            BootstrapWire(PixelFormat.WinArdBgra32, settings.Encodings),
            stream.WrittenBytes[initializationLength..]);
    }

    [Theory]
    [InlineData("RFB 003.008\n", false)]
    [InlineData("RFB 003.889\n", true)]
    public async Task Rfb_client_emits_only_the_selected_bootstrap_declaration_before_the_first_request(
        string banner,
        bool ard)
    {
        await using var stream = new ScriptedDuplexStream(
        [
            .. Handshake(banner),
            .. ard ? ArdServerInit(2, 1) : ServerInit(2, 1),
        ]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        var initializationOffset = stream.WrittenBytes.Length;
        await client.InitializeAsync(default);
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth);

        await client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, default);
        await client.RequestFramebufferUpdateAsync(incremental: false, default);

        var initializationWire = stream.WrittenBytes[initializationOffset..];
        var selectedDeclaration = BootstrapWire(PixelFormat.WinArdRgb565, settings.Encodings);
        var selectedDeclarationAndRequest = BootstrapWire(
            PixelFormat.WinArdRgb565,
            settings.Encodings,
            2,
            1);
        var legacyPixelFormat = BootstrapWire(PixelFormat.WinArdBgra32, [])[..20];
        Assert.Equal(1, CountSequence(initializationWire, selectedDeclaration));
        Assert.Equal(1, CountSequence(initializationWire, selectedDeclarationAndRequest));
        Assert.Equal(0, CountSequence(initializationWire, legacyPixelFormat));
    }

    [Fact]
    public async Task Authenticated_ard_bootstrap_declares_encryption_before_requesting_it()
    {
        var authenticationKey = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(
            stream,
            authenticationResult: new ArdAuthenticationResult(authenticationKey));
        await client.NegotiateAsync(default);
        var initializationOffset = stream.WrittenBytes.Length;
        await client.InitializeAsync(default);
        var initializationLength = stream.WrittenBytes.Length;
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth,
            0.5d);

        await client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, default);

        var initializationWire = stream.WrittenBytes[initializationOffset..initializationLength];
        var bootstrapWire = stream.WrittenBytes[initializationLength..];
        Assert.Equal(0, CountSequence(initializationWire, [0x12, 0, 0, 1]));
        var declaration = BootstrapWire(
            PixelFormat.WinArdRgb565,
            [16, 6, 0, 1, -239, -223, 1101, 1105, 1103],
            scaleFactor: 0.5d);
        Assert.Equal(
            declaration,
            bootstrapWire[..declaration.Length]);
        Assert.Equal(0x12, bootstrapWire[declaration.Length]);
    }

    [Fact]
    public async Task Bootstrap_receive_maps_only_structural_compatibility_failures()
    {
        await using var stream = new ScriptedDuplexStream(
        [
            .. Handshake("RFB 003.008\n"),
            .. ServerInit(1, 1),
            0, 0, 0, 1,
            .. Header(0, 0, 1, 1, 999),
        ]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);

        var compatibility = await Assert.ThrowsAsync<QualityBootstrapCompatibilityException>(() =>
            client.ReceiveBootstrapAsync(default).AsTask());
        Assert.Equal(QualityBootstrapFailureReason.UnsupportedEncoding, compatibility.Reason);
        Assert.Null(compatibility.InnerException);

        await using var cancellationStream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(1, 1)]);
        await using var cancellationClient = new RfbClient(cancellationStream);
        await cancellationClient.NegotiateAsync(default);
        await cancellationClient.InitializeAsync(default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancellationClient.ReceiveBootstrapAsync(cancellation.Token).AsTask());
    }

    [Theory]
    [InlineData(RfbProtocolFailureKind.DecoderFailure, RfbProtocolReadStage.FramebufferRectanglePayload, QualityBootstrapFailureReason.DecoderFailure)]
    [InlineData(RfbProtocolFailureKind.UnsupportedEncoding, RfbProtocolReadStage.FramebufferRectangleHeader, QualityBootstrapFailureReason.UnsupportedEncoding)]
    [InlineData(RfbProtocolFailureKind.MalformedFramebufferUpdate, RfbProtocolReadStage.FramebufferRectangleHeader, QualityBootstrapFailureReason.MalformedFramebufferUpdate)]
    [InlineData(RfbProtocolFailureKind.MalformedFramebufferUpdate, RfbProtocolReadStage.FramebufferRectanglePayload, QualityBootstrapFailureReason.MalformedFramebufferUpdate)]
    [InlineData(RfbProtocolFailureKind.TruncatedRead, RfbProtocolReadStage.FramebufferRectangleHeader, QualityBootstrapFailureReason.MalformedFramebufferUpdate)]
    [InlineData(RfbProtocolFailureKind.TruncatedRead, RfbProtocolReadStage.FramebufferRectanglePayload, QualityBootstrapFailureReason.MalformedFramebufferUpdate)]
    public void Bootstrap_failure_classifier_accepts_only_the_compatibility_closed_set(
        RfbProtocolFailureKind kind,
        RfbProtocolReadStage stage,
        QualityBootstrapFailureReason expected)
    {
        var exception = RfbProtocolException.Create(
            "secret",
            new RfbProtocolFailureInfo(kind, stage));

        Assert.True(RfbClient.TryClassifyBootstrapFailure(exception, out var reason));
        Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData(RfbProtocolFailureKind.MalformedFramebufferUpdate, RfbProtocolReadStage.FramebufferHeader)]
    [InlineData(RfbProtocolFailureKind.TruncatedRead, RfbProtocolReadStage.FramebufferHeader)]
    [InlineData(RfbProtocolFailureKind.ArdEncryptionNegotiation, RfbProtocolReadStage.ServerMessageType)]
    public void Bootstrap_failure_classifier_rejects_non_compatibility_failures(
        RfbProtocolFailureKind kind,
        RfbProtocolReadStage stage)
    {
        var exception = RfbProtocolException.Create(
            "secret",
            new RfbProtocolFailureInfo(kind, stage));

        Assert.False(RfbClient.TryClassifyBootstrapFailure(exception, out var reason));
        Assert.Equal(default, reason);
    }

    [Fact]
    public void Remote_close_is_scale_rejection_only_after_written_scaling_and_before_first_pixel()
    {
        var exception = RfbProtocolException.Create(
            "closed",
            new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.RemoteSessionClosed,
                RfbProtocolReadStage.ArdStateChangePayload));

        Assert.True(RfbClient.TryClassifyBootstrapFailure(
            exception,
            scalingWritten: true,
            firstPixelConfirmed: false,
            out var reason));
        Assert.Equal(QualityBootstrapFailureReason.ScaleRejected, reason);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Remote_close_is_not_bootstrap_compatibility_without_pending_written_scaling(
        bool scalingWritten,
        bool firstPixelConfirmed)
    {
        var exception = RfbProtocolException.Create(
            "closed",
            new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.RemoteSessionClosed,
                RfbProtocolReadStage.ArdStateChangePayload));

        Assert.False(RfbClient.TryClassifyBootstrapFailure(
            exception,
            scalingWritten,
            firstPixelConfirmed,
            out var reason));
        Assert.Equal(default, reason);
    }

    [Theory]
    [InlineData(3, 3, 0.75d, 2, 2)]
    [InlineData(3, 3, 0.75d, 3, 3)]
    [InlineData(100, 80, 0.75d, 75, 60)]
    public async Task Bootstrap_scale_confirmation_accepts_server_integer_rounding(
        ushort originalWidth,
        ushort originalHeight,
        double scaleFactor,
        ushort actualWidth,
        ushort actualHeight)
    {
        await using var stream = new ScriptedDuplexStream(
        [
            .. Handshake("RFB 003.889\n"),
            .. ArdServerInit(originalWidth, originalHeight),
            .. DesktopSizeUpdate(actualWidth, actualHeight),
        ]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        await client.ConfigureBootstrapAsync(
            new QualityBootstrapSettings(
                RemotePixelFormatKind.Rgb565,
                [16, 6, 0, 1, -239, -223],
                QualityBootstrapReason.AutomaticBandwidth,
                scaleFactor),
            QualityBootstrapAttempt.Preferred,
            default);

        using var resize = Assert.IsType<RemoteFramebufferMessage>(await client.ReceiveBootstrapAsync(default));
        client.ConfirmBootstrap(resize.Size);

        Assert.True(client.BootstrapState.IsFirstPixelConfirmed);
        Assert.Equal(scaleFactor, client.BootstrapState.AppliedScaleFactor);
    }

    [Theory]
    [InlineData(100, 80, 0.75d, 1, 1)]
    [InlineData(100, 80, 0.75d, 74, 60)]
    [InlineData(100, 80, 0.75d, 75, 61)]
    public async Task Bootstrap_scale_confirmation_rejects_dimensions_outside_rounding_bounds(
        ushort originalWidth,
        ushort originalHeight,
        double scaleFactor,
        ushort actualWidth,
        ushort actualHeight)
    {
        await using var stream = new ScriptedDuplexStream(
        [
            .. Handshake("RFB 003.889\n"),
            .. ArdServerInit(originalWidth, originalHeight),
            .. DesktopSizeUpdate(actualWidth, actualHeight),
        ]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        await client.ConfigureBootstrapAsync(
            new QualityBootstrapSettings(
                RemotePixelFormatKind.Rgb565,
                [16, 6, 0, 1, -239, -223],
                QualityBootstrapReason.AutomaticBandwidth,
                scaleFactor),
            QualityBootstrapAttempt.Preferred,
            default);

        using var resize = Assert.IsType<RemoteFramebufferMessage>(await client.ReceiveBootstrapAsync(default));
        var compatibility = Assert.Throws<QualityBootstrapCompatibilityException>(() =>
            client.ConfirmBootstrap(resize.Size));

        Assert.Equal(QualityBootstrapFailureReason.FramebufferSizeMismatch, compatibility.Reason);
        Assert.False(client.BootstrapState.IsFirstPixelConfirmed);
    }

    [Fact]
    public async Task Legacy_rfb_client_implementations_explicitly_reject_bootstrap_configuration()
    {
        await using IRfbClient client = new LegacyRfbClient();
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, default).AsTask());

        Assert.Same(QualityBootstrapState.LegacyBgra32, client.BootstrapState);
    }

    [Fact]
    public async Task Active_bootstrap_rejects_receive_transition_and_request_without_interleaved_wire()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var blockedWrite = stream.BlockNextWrite();
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth);
        var bootstrap = client.ConfigureBootstrapAsync(
            settings,
            QualityBootstrapAttempt.Preferred,
            default).AsTask();
        await blockedWrite.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var blockedWireLength = stream.WrittenBytes.Length;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReceiveAsync(default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ApplyQualityTransitionAsync(
                new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
                default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestFramebufferUpdateAsync(incremental: false, default).AsTask());
        Assert.Equal(blockedWireLength, stream.WrittenBytes.Length);

        blockedWrite.Release.TrySetResult();
        await bootstrap.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Bootstrap_publishes_only_complete_states_across_a_blocked_write()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var blockedWrite = stream.BlockNextWrite();
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth);

        var bootstrap = client.ConfigureBootstrapAsync(
            settings,
            QualityBootstrapAttempt.Preferred,
            default).AsTask();
        await blockedWrite.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(QualityBootstrapState.LegacyBgra32, client.BootstrapState);

        blockedWrite.Release.TrySetResult();
        await bootstrap.WaitAsync(TimeSpan.FromSeconds(5));
        var published = client.BootstrapState;
        Assert.Equal(QualityBootstrapAttempt.Preferred, published.Attempt);
        Assert.Same(settings, published.ActualQuality);
    }

    [Fact]
    public async Task Precancelled_bootstrap_creates_no_target_and_preserves_current_session()
    {
        var createCount = 0;
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1), .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(
            stream,
            framebufferSessionFactory: (framebuffer, pixelFormat) =>
            {
                Interlocked.Increment(ref createCount);
                return FramebufferUpdateReader.CreateSession(
                    framebuffer,
                    pixelFormat == RemotePixelFormatKind.Bgra32
                        ? PixelFormat.WinArdBgra32
                        : PixelFormat.WinArdRgb565);
            });
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var wireLength = stream.WrittenBytes.Length;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, createCount);
        Assert.Equal(wireLength, stream.WrittenBytes.Length);
        using var cursor = Assert.IsType<RemoteCursorMessage>(await client.ReceiveAsync(default));
    }

    [Fact]
    public async Task Post_commit_old_session_cleanup_failure_does_not_reverse_bootstrap_success()
    {
        var oldDecoder = new CountingDisposeDecoder(throws: true);
        var targetDecoder = new CountingDisposeDecoder();
        var createCount = 0;
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1), .. CursorOnlyUpdate()]);
        var client = new RfbClient(
            stream,
            framebufferSessionFactory: (framebuffer, pixelFormat) =>
            {
                var decoder = Interlocked.Increment(ref createCount) == 1 ? oldDecoder : targetDecoder;
                return new FramebufferUpdateSession(
                    framebuffer,
                    new Dictionary<int, IRfbEncodingDecoder>
                    {
                        [(int)RfbEncodingType.Cursor] = new CursorEncoding(
                            pixelFormat == RemotePixelFormatKind.Bgra32
                                ? PixelFormat.WinArdBgra32
                                : PixelFormat.WinArdRgb565),
                        [700] = decoder,
                    });
            });
        try
        {
            await client.NegotiateAsync(default);
            await client.InitializeAsync(default);
            var settings = new QualityBootstrapSettings(
                RemotePixelFormatKind.Rgb565,
                [16, 6, 0, 1, -239, -223],
                QualityBootstrapReason.AutomaticBandwidth);

            await client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, default);
            await client.RequestFramebufferUpdateAsync(incremental: false, default);
            using var cursor = Assert.IsType<RemoteCursorMessage>(await client.ReceiveAsync(default));

            Assert.Equal(1, oldDecoder.DisposeCount);
            Assert.Equal(0, targetDecoder.DisposeCount);
            Assert.Same(settings, client.BootstrapState.ActualQuality);
        }
        finally
        {
            await client.DisposeAsync();
        }

        Assert.Equal(1, oldDecoder.DisposeCount);
        Assert.Equal(1, targetDecoder.DisposeCount);
    }

    [Fact]
    public async Task Bootstrap_wire_failure_disposes_current_and_target_once()
    {
        var decoders = new List<CountingDisposeDecoder>();
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        var client = new RfbClient(
            stream,
            framebufferSessionFactory: (framebuffer, _) =>
            {
                var decoder = new CountingDisposeDecoder();
                decoders.Add(decoder);
                return new FramebufferUpdateSession(
                    framebuffer,
                    new Dictionary<int, IRfbEncodingDecoder> { [700] = decoder });
            });
        try
        {
            await client.NegotiateAsync(default);
            await client.InitializeAsync(default);
            stream.FailAfterSuccessfulWrites(1, new IOException("injected bootstrap failure"));
            var settings = new QualityBootstrapSettings(
                RemotePixelFormatKind.Rgb565,
                [16, 6, 0, 1, -239, -223],
                QualityBootstrapReason.AutomaticBandwidth);

            await Assert.ThrowsAsync<IOException>(() =>
                client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, default).AsTask());
        }
        finally
        {
            await client.DisposeAsync();
        }

        Assert.Equal(2, decoders.Count);
        Assert.All(decoders, decoder => Assert.Equal(1, decoder.DisposeCount));
    }

    [Fact]
    public async Task Shutdown_during_active_bootstrap_completes_without_double_disposal()
    {
        var decoders = new List<CountingDisposeDecoder>();
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        var client = new RfbClient(
            stream,
            framebufferSessionFactory: (framebuffer, _) =>
            {
                var decoder = new CountingDisposeDecoder();
                decoders.Add(decoder);
                return new FramebufferUpdateSession(
                    framebuffer,
                    new Dictionary<int, IRfbEncodingDecoder> { [700] = decoder });
            });
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var blockedWrite = stream.BlockNextWrite();
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth);
        var bootstrap = client.ConfigureBootstrapAsync(
            settings,
            QualityBootstrapAttempt.Preferred,
            default).AsTask();
        await blockedWrite.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        client.BeginShutdown();
        await blockedWrite.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bootstrap);
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, decoders.Count);
        Assert.All(decoders, decoder => Assert.Equal(1, decoder.DisposeCount));
    }

    [Fact]
    public async Task Rfb_client_configures_bgra32_fallback_with_zlib_first()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var initializationLength = stream.WrittenBytes.Length;
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Bgra32,
            [6, 16, 0, 1, -239, -223],
            QualityBootstrapReason.SafeFallback);

        await client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Fallback, default);

        Assert.Equal(
            BootstrapWire(PixelFormat.WinArdBgra32, settings.Encodings),
            stream.WrittenBytes[initializationLength..]);
        Assert.Equal(QualityBootstrapAttempt.Fallback, client.BootstrapState.Attempt);
        Assert.Equal(settings, client.BootstrapState.ActualQuality);
    }

    [Fact]
    public async Task Rfb_client_rejects_a_second_bootstrap_without_writing()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth);
        await client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, default);
        var configuredLength = stream.WrittenBytes.Length;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, default).AsTask());

        Assert.Equal(configuredLength, stream.WrittenBytes.Length);
    }

    [Fact]
    public async Task Rfb_client_rejects_bootstrap_after_the_first_request_without_writing()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        await client.RequestFramebufferUpdateAsync(incremental: false, default);
        var requestedLength = stream.WrittenBytes.Length;
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, default).AsTask());

        Assert.Equal(requestedLength, stream.WrittenBytes.Length);
    }

    [Fact]
    public async Task Canceled_bootstrap_after_wire_output_faults_the_client_without_publishing_state()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth);
        stream.CancelAfterNextWrite(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, cancellation.Token).AsTask());

        Assert.Same(QualityBootstrapState.LegacyBgra32, client.BootstrapState);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RequestFramebufferUpdateAsync(incremental: false, default).AsTask());
    }

    [Fact]
    public async Task Failed_bootstrap_after_wire_output_faults_the_client_without_publishing_state()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var settings = new QualityBootstrapSettings(
            RemotePixelFormatKind.Rgb565,
            [16, 6, 0, 1, -239, -223],
            QualityBootstrapReason.AutomaticBandwidth);
        stream.FailAfterSuccessfulWrites(1, new IOException("injected bootstrap failure"));

        await Assert.ThrowsAsync<IOException>(() =>
            client.ConfigureBootstrapAsync(settings, QualityBootstrapAttempt.Preferred, default).AsTask());

        Assert.Same(QualityBootstrapState.LegacyBgra32, client.BootstrapState);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RequestFramebufferUpdateAsync(incremental: false, default).AsTask());
    }

    [Fact]
    public async Task Rfb_client_rejects_duplicate_request_until_a_frame_response_completes()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1), 2, .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);

        await client.RequestFramebufferUpdateAsync(incremental: true, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestFramebufferUpdateAsync(incremental: true, default).AsTask());
        Assert.IsType<RemoteBellMessage>(await client.ReceiveAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestFramebufferUpdateAsync(incremental: true, default).AsTask());
        using var frame = Assert.IsType<RemoteCursorMessage>(await client.ReceiveAsync(default));

        await client.RequestFramebufferUpdateAsync(incremental: true, default);
    }

    [Fact]
    public async Task Rfb_client_rejects_concurrent_receive()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var blocked = stream.BlockReadAfter(1);
        using var cancellation = new CancellationTokenSource();
        var receive = client.ReceiveAsync(cancellation.Token).AsTask();
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReceiveAsync(default).AsTask());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
    }

    [Fact]
    public async Task Rfb_client_quality_transition_preserves_persistent_zlib_stream()
    {
        var firstPixels = Enumerable.Repeat(new byte[] { 1, 2, 3, 0 }, 4096)
            .SelectMany(pixel => pixel)
            .ToArray();
        var secondPixels = Enumerable.Repeat(new byte[] { 0x1F, 0 }, 4096)
            .SelectMany(pixel => pixel)
            .ToArray();
        var (firstChunk, secondChunk) = CreateSharedZlibChunks(firstPixels, secondPixels);
        var sessionCreateCount = 0;
        var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
        await using var stream = new ScriptedDuplexStream(
        [
            .. Handshake("RFB 003.008\n"),
            .. ServerInit(64, 64),
            .. ZlibUpdate(64, 64, firstChunk),
            .. ZlibUpdate(64, 64, secondChunk),
        ]);
        await using var client = new RfbClient(
            stream,
            diagnosticSink: sink,
            framebufferSessionFactory: (framebuffer, pixelFormat) =>
            {
                Interlocked.Increment(ref sessionCreateCount);
                return FramebufferUpdateReader.CreateSession(
                    framebuffer,
                    pixelFormat == RemotePixelFormatKind.Bgra32
                        ? PixelFormat.WinArdBgra32
                        : PixelFormat.WinArdRgb565);
            });
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        using var firstFrame = Assert.IsType<RemoteFramebufferMessage>(await client.ReceiveAsync(default));
        var transitionOffset = stream.WrittenBytes.Length;

        var result = await client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [6, 16, 0, 1, -239, -223], 1),
            default);
        using var repairedFrame = Assert.IsType<RemoteFramebufferMessage>(await client.ReceiveAsync(default));

        Assert.Equal(QualityTransitionStatus.Applied, result);
        Assert.Equal(0xFF030201u, BinaryPrimitives.ReadUInt32LittleEndian(firstFrame.Bgra32.Span[..4]));
        Assert.Equal(0xFF0000FFu, BinaryPrimitives.ReadUInt32LittleEndian(repairedFrame.Bgra32.Span[..4]));
        Assert.Equal(1, sessionCreateCount);
        var transitionWrites = stream.WrittenBytes[transitionOffset..];
        Assert.Equal(30, transitionWrites.Length);
        Assert.Equal(0, transitionWrites[0]);
        Assert.Equal(3, transitionWrites[20]);
        Assert.Equal(0, transitionWrites[21]);
        Assert.DoesNotContain(
            sink.Snapshot(),
            diagnostic => diagnostic.Fields.Any(field => field.Value == "DecoderFailure"));
    }

    [Fact]
    public async Task Rfb_client_quality_transition_preserves_persistent_zlib_stream_from_rgb565_to_bgra32()
    {
        var firstPixels = Enumerable.Repeat(new byte[] { 0, 0xF8 }, 4096)
            .SelectMany(pixel => pixel)
            .ToArray();
        var secondPixels = Enumerable.Repeat(new byte[] { 4, 5, 6, 0 }, 4096)
            .SelectMany(pixel => pixel)
            .ToArray();
        var (firstChunk, secondChunk) = CreateSharedZlibChunks(firstPixels, secondPixels);
        var sessionCreateCount = 0;
        await using var stream = new ScriptedDuplexStream(
        [
            .. Handshake("RFB 003.008\n"),
            .. ServerInit(64, 64),
            .. ZlibUpdate(64, 64, firstChunk),
            .. ZlibUpdate(64, 64, secondChunk),
        ]);
        await using var client = new RfbClient(
            stream,
            framebufferSessionFactory: (framebuffer, pixelFormat) =>
            {
                Interlocked.Increment(ref sessionCreateCount);
                return FramebufferUpdateReader.CreateSession(
                    framebuffer,
                    pixelFormat == RemotePixelFormatKind.Bgra32
                        ? PixelFormat.WinArdBgra32
                        : PixelFormat.WinArdRgb565);
            });
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);

        var firstTransition = await client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [6, 16, 0, 1, -239, -223], 1),
            default);
        using var firstFrame = Assert.IsType<RemoteFramebufferMessage>(await client.ReceiveAsync(default));
        var secondTransitionOffset = stream.WrittenBytes.Length;
        var secondTransition = await client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            default);
        using var repairedFrame = Assert.IsType<RemoteFramebufferMessage>(await client.ReceiveAsync(default));

        Assert.Equal(QualityTransitionStatus.Applied, firstTransition);
        Assert.Equal(QualityTransitionStatus.Applied, secondTransition);
        Assert.Equal(0xFFFF0000u, BinaryPrimitives.ReadUInt32LittleEndian(firstFrame.Bgra32.Span[..4]));
        Assert.Equal(0xFF060504u, BinaryPrimitives.ReadUInt32LittleEndian(repairedFrame.Bgra32.Span[..4]));
        Assert.Equal(1, sessionCreateCount);
        var secondTransitionWrites = stream.WrittenBytes[secondTransitionOffset..];
        Assert.Equal(30, secondTransitionWrites.Length);
        Assert.Equal(0, secondTransitionWrites[0]);
        Assert.Equal(3, secondTransitionWrites[20]);
        Assert.Equal(0, secondTransitionWrites[21]);
    }

    [Fact]
    public async Task Rfb_client_quality_transition_writes_configuration_then_one_full_repair()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var offset = stream.WrittenBytes.Length;

        var result = await client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0, 1, -239, -223], 1),
            default);

        Assert.Equal(QualityTransitionStatus.Applied, result);
        var writes = stream.WrittenBytes[offset..];
        Assert.Equal(0, writes[0]);
        Assert.Equal(2, writes[20]);
        Assert.Equal(3, writes[44]);
        Assert.Equal(54, writes.Length);
        Assert.Equal(0, writes[45]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestFramebufferUpdateAsync(incremental: false, default).AsTask());
    }

    [Fact]
    public async Task Quality_settings_preserve_order_deduplicate_and_append_required_encodings_on_wire()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var offset = stream.WrittenBytes.Length;

        var result = await client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0, 6, 16, 0], 1),
            default);

        Assert.Equal(QualityTransitionStatus.Applied, result);
        var writes = stream.WrittenBytes[offset..];
        Assert.Equal(58, writes.Length);
        Assert.Equal(2, writes[20]);
        Assert.Equal(6, BinaryPrimitives.ReadUInt16BigEndian(writes.AsSpan(22)));
        Assert.Equal(
            [16, 0, 6, 1, -239, -223],
            Enumerable.Range(0, 6)
                .Select(index => BinaryPrimitives.ReadInt32BigEndian(writes.AsSpan(24 + (index * 4)))));
        Assert.Equal(3, writes[48]);
    }

    [Fact]
    public async Task Rfb_client_no_change_and_scale_preflight_write_nothing()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var offset = stream.WrittenBytes.Length;
        var current = new RemoteQualitySettings(
            RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1);

        Assert.Equal(QualityTransitionStatus.NoChange,
            await client.ApplyQualityTransitionAsync(current, default));
        Assert.Equal(QualityTransitionStatus.ReconnectRequired,
            await client.ApplyQualityTransitionAsync(
                new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, current.Encodings, 0.75), default));
        Assert.Equal(offset, stream.WrittenBytes.Length);
    }

    [Fact]
    public async Task Precancelled_quality_transition_preserves_cancellation_before_wire()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1), .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var offset = stream.WrittenBytes.Length;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ApplyQualityTransitionAsync(
                new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0], 1),
                cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(offset, stream.WrittenBytes.Length);
        using var frame = Assert.IsType<RemoteCursorMessage>(await client.ReceiveAsync(default));
    }

    [Fact]
    public async Task Quality_transition_cancelled_while_waiting_for_session_gate_preserves_cancellation()
    {
        var decoder = new BlockingDecoder();
        FramebufferUpdateSession? session = null;
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1), .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(
            stream,
            framebufferSessionFactory: (framebuffer, _) =>
                session = FramebufferUpdateReader.CreateSession(
                    framebuffer,
                    PixelFormat.WinArdBgra32,
                    decoder));
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var offset = stream.WrittenBytes.Length;
        var activeSession = Assert.IsType<FramebufferUpdateSession>(session);
        byte[] blockingUpdate =
        [
            0, 0, 0, 1,
            .. Header(0, 0, 1, 1, decoder.EncodingId),
        ];
        var decode = activeSession.ApplyAsync(new MemoryStream(blockingUpdate), default);
        await decoder.Started.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var transition = client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0], 1),
            cancellation.Token).AsTask();
        cancellation.Cancel();

        try
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transition);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(offset, stream.WrittenBytes.Length);
        }
        finally
        {
            decoder.Release();
        }

        _ = await decode.WaitAsync(TimeSpan.FromSeconds(5));
        using var frame = Assert.IsType<RemoteCursorMessage>(await client.ReceiveAsync(default));
    }

    [Fact]
    public async Task Rfb_client_configuration_write_failure_faults_connection()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        stream.FailNextWrite(new IOException("injected transition failure"));

        var result = await client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0], 1), default);

        Assert.Equal(QualityTransitionStatus.Faulted, result);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RequestFramebufferUpdateAsync(incremental: true, default).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReceiveAsync(default).AsTask());
    }

    [Fact]
    public async Task Quality_transition_is_one_background_item_and_input_runs_before_later_background()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var offset = stream.WrittenBytes.Length;
        var blocked = stream.BlockNextWrite();
        var transition = client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0, 1, -239, -223], 1),
            default).AsTask();
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var background = client.SendClipboardTextAsync("x", default).AsTask();
        var input = client.SendPointerAsync(0, 1, 1, default).AsTask();

        blocked.Release.TrySetResult();
        await Task.WhenAll(transition, input, background).WaitAsync(TimeSpan.FromSeconds(5));

        var writes = stream.WrittenBytes[offset..];
        Assert.Equal(0, writes[0]);
        Assert.Equal(2, writes[20]);
        Assert.Equal(3, writes[44]);
        Assert.Equal(5, writes[54]);
        Assert.Equal(6, writes[60]);
    }

    [Fact]
    public async Task Cancellation_after_first_transition_write_faults_scheduler_before_queued_input()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var offset = stream.WrittenBytes.Length;
        using var cancellation = new CancellationTokenSource();
        var blocked = stream.BlockNextWrite();
        stream.CancelAfterNextWrite(cancellation);
        var transition = client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0], 1),
            cancellation.Token).AsTask();
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var input = client.SendPointerAsync(0, 1, 1, default).AsTask();

        blocked.Release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transition);
        await Assert.ThrowsAnyAsync<Exception>(() => input);
        Assert.Equal(20, stream.WrittenBytes.Length - offset);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReceiveAsync(default).AsTask());
    }

    [Fact]
    public async Task Pixel_format_preflight_failure_is_zero_wire_and_session_remains_usable()
    {
        var createCount = 0;
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1), .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(
            stream,
            framebufferSessionFactory: (framebuffer, pixelFormat) =>
            {
                Interlocked.Increment(ref createCount);
                return new FramebufferUpdateSession(
                    framebuffer,
                    new Dictionary<int, IRfbEncodingDecoder>
                    {
                        [(int)RfbEncodingType.Cursor] = new CursorEncoding(PixelFormat.WinArdBgra32),
                        [700] = new ThrowingValidatePixelFormatDecoder(),
                    });
            });
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var offset = stream.WrittenBytes.Length;

        var result = await client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0], 1), default);

        Assert.Equal(QualityTransitionStatus.CapabilityUnavailable, result);
        Assert.Equal(offset, stream.WrittenBytes.Length);
        Assert.Equal(1, createCount);
        using var frame = Assert.IsType<RemoteCursorMessage>(await client.ReceiveAsync(default));
    }

    [Fact]
    public async Task Quality_transition_does_not_dispose_the_reused_decoder_session()
    {
        var createCount = 0;
        var decoder = new TrackingDisposeDecoder();
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(
            stream,
            framebufferSessionFactory: (framebuffer, _) =>
            {
                Interlocked.Increment(ref createCount);
                return new FramebufferUpdateSession(
                    framebuffer,
                    new Dictionary<int, IRfbEncodingDecoder>
                    {
                        [700] = decoder,
                    });
            });
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var offset = stream.WrittenBytes.Length;

        var result = await client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0, 1, -239, -223], 1), default);

        Assert.Equal(QualityTransitionStatus.Applied, result);
        Assert.Equal(54, stream.WrittenBytes.Length - offset);
        Assert.Equal(1, createCount);
        Assert.False(decoder.IsDisposed);
    }

    [Fact]
    public async Task Repair_write_failure_faults_after_configuration_and_emits_no_request()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var offset = stream.WrittenBytes.Length;
        stream.FailAfterSuccessfulWrites(2, new IOException("injected repair failure"));

        var result = await client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0, 1, -239, -223], 1), default);

        Assert.Equal(QualityTransitionStatus.Faulted, result);
        Assert.Equal(44, stream.WrittenBytes.Length - offset);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReceiveAsync(default).AsTask());
    }

    [Fact]
    public async Task Partial_configuration_message_faults_connection_after_prefix_reaches_wire()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var offset = stream.WrittenBytes.Length;
        stream.FailNextWriteAfterPrefix(5, new IOException("injected partial write failure"));

        var result = await client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0], 1), default);

        Assert.Equal(QualityTransitionStatus.Faulted, result);
        Assert.Equal(5, stream.WrittenBytes.Length - offset);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReceiveAsync(default).AsTask());
    }

    [Fact]
    public async Task Prewire_cancellation_keeps_primary_cancellation_and_reused_session()
    {
        var createCount = 0;
        var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(
            stream,
            diagnosticSink: sink,
            framebufferSessionFactory: (framebuffer, _) =>
            {
                Interlocked.Increment(ref createCount);
                return FramebufferUpdateReader.CreateSession(framebuffer, PixelFormat.WinArdBgra32);
            });
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        var blocked = stream.BlockNextWrite();
        var active = client.SendPointerAsync(0, 1, 1, default).AsTask();
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var transition = client.ApplyQualityTransitionAsync(
            new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0], 1),
            cancellation.Token).AsTask();

        cancellation.Cancel();
        blocked.Release.TrySetResult();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transition);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        await active;
        Assert.Equal(1, createCount);
        Assert.DoesNotContain(sink.Snapshot(), item => item.Code == "RFB_QUALITY_TRANSITION_CLEANUP");
    }

    [Fact]
    public async Task Rfb_client_prioritizes_input_over_queued_framebuffer_requests()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        var outputOffset = stream.WrittenBytes.Length;
        var blockedWrite = stream.BlockNextWrite();

        var activeBackground = client.RequestFramebufferUpdateAsync(
            incremental: false,
            CancellationToken.None).AsTask();
        await blockedWrite.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedBackground = client.SendClipboardTextAsync("x", CancellationToken.None).AsTask();
        var input = client.SendPointerAsync(1, 1, 1, CancellationToken.None).AsTask();

        blockedWrite.Release.TrySetResult();
        await Task.WhenAll(activeBackground, input, queuedBackground)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                3, 0, 0, 0, 0, 0, 0, 2, 0, 1,
                5, 1, 0, 1, 0, 1,
                6, 0, 0, 0, 0, 0, 0, 1, (byte)'x',
            ],
            stream.WrittenBytes[outputOffset..]);
    }

    [Fact]
    public async Task Rfb_client_serves_background_after_thirty_two_input_writes()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        var outputOffset = stream.WrittenBytes.Length;
        var blockedWrite = stream.BlockNextWrite();
        var activeBackground = client.RequestFramebufferUpdateAsync(
            incremental: false,
            CancellationToken.None).AsTask();
        await blockedWrite.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var inputs = Enumerable.Range(0, 40)
            .Select(value => client.SendPointerAsync(
                0,
                value,
                value,
                CancellationToken.None).AsTask())
            .ToArray();
        var queuedBackground = client.SendClipboardTextAsync("x", CancellationToken.None).AsTask();

        blockedWrite.Release.TrySetResult();
        await Task.WhenAll(inputs.Append(activeBackground).Append(queuedBackground))
            .WaitAsync(TimeSpan.FromSeconds(5));

        var types = ParseClientMessageTypes(stream.WrittenBytes[outputOffset..]);
        Assert.Equal(33, types.LastIndexOf(6));
    }

    [Fact]
    public async Task Rfb_client_queues_ARD_tickle_reply_behind_active_runtime_write()
    {
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.889\n"),
                .. ArdServerInit(2, 1),
                .. ArdStateChange(flags: 0, status: 4),
                .. CursorOnlyUpdate(),
            ]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        var blockedWrite = stream.BlockNextWrite();
        var pointer = client.SendPointerAsync(0, 1, 1, CancellationToken.None).AsTask();
        await blockedWrite.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var receive = client.ReceiveAsync(CancellationToken.None).AsTask();
        var earlyCompletion = await Task.WhenAny(receive, Task.Delay(100));

        blockedWrite.Release.TrySetResult();
        using var message = Assert.IsType<RemoteCursorMessage>(
            await receive.WaitAsync(TimeSpan.FromSeconds(5)));
        await pointer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(receive, earlyCompletion);
    }

    [Fact]
    public async Task Rfb_client_dispose_cancels_active_scheduler_write_before_releasing_resources()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(2, 1)]);
        var client = new RfbClient(stream);
        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        var blockedWrite = stream.BlockNextWrite();
        var pointer = client.SendPointerAsync(0, 1, 1, CancellationToken.None).AsTask();
        await blockedWrite.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposal = Task.Run(async () => await client.DisposeAsync());
        try
        {
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            await blockedWrite.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pointer);
        }
        finally
        {
            blockedWrite.Release.TrySetResult();
            _ = await Record.ExceptionAsync(() => pointer);
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("RFB 003.003\n", (byte)0x04)]
    [InlineData("RFB 003.003\n", (byte)0x07)]
    [InlineData("RFB 003.007\n", (byte)0x04)]
    [InlineData("RFB 003.007\n", (byte)0x07)]
    [InlineData("RFB 003.008\n", (byte)0x04)]
    [InlineData("RFB 003.008\n", (byte)0x07)]
    public async Task Rfb_client_rejects_ARD_control_messages_for_standard_RFB_versions(
        string banner,
        byte controlMessage)
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake(banner), .. ServerInit(2, 1), controlMessage]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Contains(
            controlMessage.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rfb_client_rejects_unknown_ARD_message_without_consuming_following_framebuffer()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1), 0x08, .. CursorOnlyUpdate()]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());
        using var message = Assert.IsType<RemoteCursorMessage>(
            await client.ReceiveAsync(CancellationToken.None));

        Assert.Contains("8", exception.Message, StringComparison.Ordinal);
        Assert.Equal(RfbProtocolFailureKind.UnexpectedServerMessage, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ServerMessageType, exception.Failure?.ReadStage);
        Assert.Equal((byte)0x08, exception.Failure?.ServerMessageType);
    }

    [Fact]
    public async Task Rfb_client_adds_framebuffer_message_type_without_overwriting_inner_failure()
    {
        var snapshotFailure = RfbProtocolException.Create(
            "Injected snapshot failure.",
            new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.DecoderFailure,
                RfbProtocolReadStage.FramebufferRectanglePayload,
                EncodingId: 1105,
                RectangleIndex: 2));
        var snapshotAttempts = 0;
        var snapshotFactory = new FramebufferSnapshotFactory(length =>
            Interlocked.Increment(ref snapshotAttempts) == 1
                ? throw snapshotFailure
                : MemoryPool<byte>.Shared.Rent(length));
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.008\n"),
                .. ServerInit(1, 1),
                0, 0, 0, 1,
                .. Header(0, 0, 1, 1, (int)RfbEncodingType.Raw),
                1, 2, 3, 4,
                0, 0, 0, 1,
                .. Header(0, 0, 1, 1, (int)RfbEncodingType.Raw),
                5, 6, 7, 8,
            ]);
        await using var client = new RfbClient(stream, snapshotFactory);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        await client.RequestFramebufferUpdateAsync(incremental: true, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());
        await client.RequestFramebufferUpdateAsync(incremental: true, CancellationToken.None);
        using var recovered = Assert.IsType<RemoteFramebufferMessage>(
            await client.ReceiveAsync(CancellationToken.None));

        Assert.Equal(RfbProtocolFailureKind.DecoderFailure, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.FramebufferRectanglePayload, exception.Failure?.ReadStage);
        Assert.Equal((byte)0, exception.Failure?.ServerMessageType);
        Assert.Equal(1105, exception.Failure?.EncodingId);
        Assert.Equal(2, exception.Failure?.RectangleIndex);
        Assert.Equal([5, 6, 7, 255], recovered.Bgra32.ToArray());
    }

    [Fact]
    public async Task Wire_decode_failure_keeps_request_outstanding_and_decoder_faulted()
    {
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.008\n"),
                .. ServerInit(1, 1),
                0, 0, 0, 1,
                .. Header(0, 0, 1, 1, 999),
                .. CursorOnlyUpdate(),
            ]);
        await using var client = new RfbClient(stream);
        await client.NegotiateAsync(default);
        await client.InitializeAsync(default);
        await client.RequestFramebufferUpdateAsync(incremental: true, default);

        await Assert.ThrowsAsync<RfbProtocolException>(() => client.ReceiveAsync(default).AsTask());
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = client.RequestFramebufferUpdateAsync(incremental: true, default).AsTask();
        });
        var fault = await Assert.ThrowsAsync<RfbProtocolException>(() => client.ReceiveAsync(default).AsTask());

        Assert.Contains("faulted", fault.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rfb_client_adds_clipboard_message_type_without_overwriting_truncation()
    {
        const string secretMarker = "SECRET-CLIPBOARD-MARKER";
        var payload = System.Text.Encoding.ASCII.GetBytes(secretMarker);
        var clipboardBody = new byte[7 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(
            clipboardBody.AsSpan(3),
            checked((uint)payload.Length + 1));
        payload.CopyTo(clipboardBody, 7);
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(1, 1), 3, .. clipboardBody]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ClipboardPayload, exception.Failure?.ReadStage);
        Assert.Equal((byte)3, exception.Failure?.ServerMessageType);

        var chain = new List<Exception>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            chain.Add(current);
        }

        Assert.Equal(2, chain.Count(item => item is RfbProtocolException));
        Assert.IsType<EndOfStreamException>(chain[^1]);
        Assert.All(chain, item => Assert.DoesNotContain(secretMarker, item.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rfb_client_does_not_wrap_framebuffer_io_failure()
    {
        var expected = new IOException("Injected read failure.");
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(1, 1), 0],
            exceptionAfterInput: expected);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Rfb_client_does_not_wrap_clipboard_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(1, 1), 3],
            cancellation);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ReceiveAsync(cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Rfb_client_reports_eof_after_skipped_ARD_ack()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1), 0x04]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ServerMessageType, exception.Failure?.ReadStage);
        Assert.Null(exception.Failure?.ServerMessageType);
        var readFailure = Assert.IsType<RfbProtocolException>(exception.InnerException);
        var endOfStream = Assert.IsType<EndOfStreamException>(readFailure.InnerException);
        Assert.Null(endOfStream.InnerException);
    }

    [Fact]
    public async Task Rfb_client_reports_top_level_type_eof_without_inventing_type()
    {
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.008\n"), .. ServerInit(1, 1)]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ServerMessageType, exception.Failure?.ReadStage);
        Assert.Null(exception.Failure?.ServerMessageType);
        var readFailure = Assert.IsType<RfbProtocolException>(exception.InnerException);
        var endOfStream = Assert.IsType<EndOfStreamException>(readFailure.InnerException);
        Assert.Null(endOfStream.InnerException);
    }

    [Fact]
    public async Task Rfb_client_observes_cancellation_after_skipped_ARD_nop()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new ScriptedDuplexStream(
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1), 0x07],
            cancellation);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ReceiveAsync(cancellation.Token).AsTask());
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

    private static byte[] Handshake(string banner) =>
        banner == "RFB 003.003\n"
            ? [.. System.Text.Encoding.ASCII.GetBytes(banner), 0, 0, 0, 30]
            : [.. System.Text.Encoding.ASCII.GetBytes(banner), 1, 30];

    private static byte[] ArdServerInit(ushort width, ushort height)
    {
        byte[] name = [0, 0, 0, 0, 0, 2, .. new byte[16], 0, (byte)'M', (byte)'a', (byte)'c'];
        var header = ServerInit(width, height);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), checked((uint)name.Length));
        return [.. header, .. name];
    }

    private static byte[] ArdStateChange(ushort flags, ushort status, byte[]? extra = null)
    {
        extra ??= [];
        var bytes = new byte[8 + extra.Length];
        bytes[0] = ArdServerMessage.StateChangeType;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), checked((ushort)(4 + extra.Length)));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), flags);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), status);
        extra.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] ArdSessionEncryptionUpdate(
        byte[] authenticationKey,
        byte[] sessionKey,
        byte[] sessionIv)
    {
        var bytes = new byte[4 + 12 + 36];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteInt32BigEndian(
            bytes.AsSpan(12),
            (int)RfbEncodingType.ArdSessionEncryption);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 1);
#pragma warning disable CA5358 // AES-ECB is required to construct the ARD session-encryption fixture.
        using var aes = Aes.Create();
        aes.Key = authenticationKey;
        aes.EncryptEcb(sessionKey, PaddingMode.None).CopyTo(bytes, 20);
        aes.EncryptEcb(sessionIv, PaddingMode.None).CopyTo(bytes, 36);
#pragma warning restore CA5358
        return bytes;
    }

    private static int FindSequence(byte[] source, byte[] sequence)
    {
        for (var offset = 0; offset <= source.Length - sequence.Length; offset++)
        {
            if (source.AsSpan(offset, sequence.Length).SequenceEqual(sequence))
            {
                return offset;
            }
        }

        return -1;
    }

    private static int CountSequence(byte[] source, byte[] sequence)
    {
        var count = 0;
        for (var offset = 0; offset <= source.Length - sequence.Length; offset++)
        {
            if (source.AsSpan(offset, sequence.Length).SequenceEqual(sequence))
            {
                count++;
            }
        }

        return count;
    }

    private static byte[] BootstrapWire(
        PixelFormat pixelFormat,
        IReadOnlyList<int> encodings,
        ushort? requestWidth = null,
        ushort? requestHeight = null,
        double scaleFactor = 1d)
    {
        var bytes = new List<byte>(24 + (encodings.Count * sizeof(int)) + 10);
        if (scaleFactor < 1d)
        {
            bytes.AddRange([8, 0]);
            var scale = new byte[sizeof(double)];
            BinaryPrimitives.WriteInt64BigEndian(scale, BitConverter.DoubleToInt64Bits(scaleFactor));
            bytes.AddRange(scale);
        }

        bytes.AddRange([0, 0, 0, 0]);
        bytes.AddRange(pixelFormat.ToWireBytes());
        bytes.AddRange([2, 0]);
        var count = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(count, checked((ushort)encodings.Count));
        bytes.AddRange(count);
        foreach (var encoding in encodings)
        {
            var encoded = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(encoded, encoding);
            bytes.AddRange(encoded);
        }

        if (requestWidth is { } width && requestHeight is { } height)
        {
            bytes.AddRange([3, 0, 0, 0, 0, 0]);
            var dimensions = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(dimensions, width);
            BinaryPrimitives.WriteUInt16BigEndian(dimensions.AsSpan(2), height);
            bytes.AddRange(dimensions);
        }

        return bytes.ToArray();
    }

    private static ValueTask ConfigureDefaultBootstrapAsync(RfbClient client) =>
        client.ConfigureBootstrapAsync(
            new QualityBootstrapSettings(
                RemotePixelFormatKind.Bgra32,
                [6, 16, 0, 1, -239, -223],
                QualityBootstrapReason.SafeFallback),
            QualityBootstrapAttempt.Fallback,
            default);

    private static List<byte> ParseClientMessageTypes(byte[] messages)
    {
        var types = new List<byte>();
        for (var offset = 0; offset < messages.Length;)
        {
            var type = messages[offset];
            types.Add(type);
            offset += type switch
            {
                3 => 10,
                5 => 6,
                6 => checked(8 + BinaryPrimitives.ReadInt32BigEndian(messages.AsSpan(offset + 4))),
                _ => throw new InvalidDataException($"Unexpected client message type {type}."),
            };
        }

        return types;
    }

    private static byte[] CursorOnlyUpdate()
    {
        var bytes = new byte[21];
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

    private static byte[] DesktopSizeUpdate(ushort width, ushort height)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), height);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), (int)RfbEncodingType.DesktopSize);
        return bytes;
    }

    private static (byte[] First, byte[] Second) CreateSharedZlibChunks(byte[] first, byte[] second)
    {
        using var output = new MemoryStream();
        using var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
        zlib.Write(first);
        zlib.Flush();
        var firstLength = checked((int)output.Length);
        zlib.Write(second);
        zlib.Flush();
        var all = output.ToArray();
        return (all[..firstLength], all[firstLength..]);
    }

    private static byte[] ZlibUpdate(ushort width, ushort height, byte[] compressed)
    {
        var message = new byte[20 + compressed.Length];
        message[3] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(10), height);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(12), (int)RfbEncodingType.Zlib);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(16), checked((uint)compressed.Length));
        compressed.CopyTo(message, 20);
        return message;
    }

    private static byte[] Header(ushort x, ushort y, ushort width, ushort height, int encodingId)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, x);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), y);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), height);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), encodingId);
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

    private sealed class ThrowingValidatePixelFormatDecoder :
        IRfbEncodingDecoder,
        IReconfigurablePixelFormatDecoder
    {
        public int EncodingId => 700;

        public ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            WinARD.Remote.Protocol.Framebuffer.Framebuffer framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(EncodingDecodeResult.Empty);

        public void ValidatePixelFormat(PixelFormat pixelFormat) =>
            throw new InvalidOperationException("injected pixel format validation failure");

        public void CommitPixelFormat(PixelFormat pixelFormat) =>
            throw new InvalidOperationException("commit must not run after validation failure");
    }

    private sealed class TrackingDisposeDecoder : IRfbEncodingDecoder, IDisposable
    {
        public int EncodingId => 700;

        public bool IsDisposed { get; private set; }

        public ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            WinARD.Remote.Protocol.Framebuffer.Framebuffer framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(EncodingDecodeResult.Empty);

        public void Dispose() => IsDisposed = true;
    }

    private sealed class CountingDisposeDecoder(bool throws = false) : IRfbEncodingDecoder, IDisposable
    {
        public int EncodingId => 700;

        public int DisposeCount { get; private set; }

        public ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            WinARD.Remote.Protocol.Framebuffer.Framebuffer framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(EncodingDecodeResult.Empty);

        public void Dispose()
        {
            DisposeCount++;
            if (throws)
            {
                throw new IOException("injected decoder cleanup failure");
            }
        }
    }

    private sealed class BlockingDecoder : IRfbEncodingDecoder
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int EncodingId => 701;

        public Task Started => _started.Task;

        public async ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            WinARD.Remote.Protocol.Framebuffer.Framebuffer framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return EncodingDecodeResult.Empty;
        }

        public void Release() => _release.TrySetResult();
    }

    private static bool ContainsSetPixelFormatMessage(ReadOnlySpan<byte> bytes)
    {
        for (var offset = 0; offset <= bytes.Length - 20; offset++)
        {
            if (bytes[offset] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class RfbClientRuntime(RfbClient client) : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => client.FramebufferSize;

        public ArdDisplayCapabilities QualityCapabilities => client.QualityCapabilities;

        public ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(
            RemoteQualitySettings settings,
            CancellationToken cancellationToken) =>
            client.ApplyQualityTransitionAsync(settings, cancellationToken);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            client.RequestFramebufferUpdateAsync(incremental, cancellationToken);

        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            client.ReceiveAsync(cancellationToken);

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) =>
            client.SendPointerAsync(buttons, x, y, cancellationToken);

        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) =>
            client.SendKeyAsync(keysym, down, cancellationToken);

        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) =>
            client.SendClipboardTextAsync(text, cancellationToken);

        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class LegacyRfbClient : IRfbClient
    {
        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AuthenticateAsync(
            string username,
            ISecret secret,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedDuplexStream(
        byte[] input,
        CancellationTokenSource? cancelAfterInput = null,
        IOException? exceptionAfterInput = null) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly MemoryStream _output = new();
        private IOException? _nextWriteFailure;
        private IOException? _deferredWriteFailure;
        private IOException? _partialWriteFailure;
        private int _partialWriteBytes;
        private int _successfulWritesBeforeFailure = -1;
        private CancellationTokenSource? _cancelAfterNextWrite;
        private BlockedWrite? _nextBlockedWrite;
        private BlockedRead? _blockedRead;
        private int _readsUntilBlock;

        public byte[] WrittenBytes => _output.ToArray();

        public void FailNextWrite(IOException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.CompareExchange(ref _nextWriteFailure, exception, null) is not null)
            {
                throw new InvalidOperationException("A write failure is already pending.");
            }
        }

        public void FailAfterSuccessfulWrites(int successfulWrites, IOException exception)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(successfulWrites);
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.CompareExchange(ref _deferredWriteFailure, exception, null) is not null)
            {
                throw new InvalidOperationException("A deferred write failure is already pending.");
            }

            Volatile.Write(ref _successfulWritesBeforeFailure, successfulWrites);
        }

        public void FailNextWriteAfterPrefix(int prefixBytes, IOException exception)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prefixBytes);
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.CompareExchange(ref _partialWriteFailure, exception, null) is not null)
            {
                throw new InvalidOperationException("A partial write failure is already pending.");
            }

            Volatile.Write(ref _partialWriteBytes, prefixBytes);
        }

        public void CancelAfterNextWrite(CancellationTokenSource cancellation)
        {
            ArgumentNullException.ThrowIfNull(cancellation);
            if (Interlocked.CompareExchange(ref _cancelAfterNextWrite, cancellation, null) is not null)
            {
                throw new InvalidOperationException("A post-write cancellation is already pending.");
            }
        }

        public BlockedWrite BlockNextWrite()
        {
            var blockedWrite = new BlockedWrite();
            if (Interlocked.CompareExchange(ref _nextBlockedWrite, blockedWrite, null) is not null)
            {
                throw new InvalidOperationException("A blocked write is already pending.");
            }

            return blockedWrite;
        }

        public BlockedRead BlockReadAfter(int readCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readCount);
            var blockedRead = new BlockedRead();
            if (Interlocked.CompareExchange(ref _blockedRead, blockedRead, null) is not null)
            {
                throw new InvalidOperationException("A blocked read is already pending.");
            }

            Volatile.Write(ref _readsUntilBlock, readCount);
            return blockedRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var readsUntilBlock = Volatile.Read(ref _readsUntilBlock);
            if (readsUntilBlock > 0 && Interlocked.Decrement(ref _readsUntilBlock) == 0)
            {
                var blockedRead = Interlocked.Exchange(ref _blockedRead, null);
                if (blockedRead is not null)
                {
                    blockedRead.Started.TrySetResult();
                    await blockedRead.Release.Task.WaitAsync(cancellationToken);
                }
            }

            if (_input.Position == _input.Length && exceptionAfterInput is not null)
            {
                throw exceptionAfterInput;
            }

            var read = await _input.ReadAsync(buffer, cancellationToken);
            if (read > 0 && _input.Position == _input.Length)
            {
                cancelAfterInput?.Cancel();
            }

            return read;
        }
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var writesBeforeFailure = Volatile.Read(ref _successfulWritesBeforeFailure);
            if (writesBeforeFailure == 0)
            {
                Volatile.Write(ref _successfulWritesBeforeFailure, -1);
                throw Interlocked.Exchange(ref _deferredWriteFailure, null)!;
            }

            if (writesBeforeFailure > 0)
            {
                _ = Interlocked.Decrement(ref _successfulWritesBeforeFailure);
            }

            var failure = Interlocked.Exchange(ref _nextWriteFailure, null);
            if (failure is not null)
            {
                throw failure;
            }

            var partialFailure = Interlocked.Exchange(ref _partialWriteFailure, null);
            if (partialFailure is not null)
            {
                var prefixBytes = Math.Min(Volatile.Read(ref _partialWriteBytes), buffer.Length);
                await _output.WriteAsync(buffer[..prefixBytes], CancellationToken.None);
                throw partialFailure;
            }

            var blockedWrite = Interlocked.Exchange(ref _nextBlockedWrite, null);
            if (blockedWrite is not null)
            {
                blockedWrite.Started.TrySetResult();
                try
                {
                    await blockedWrite.Release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    blockedWrite.CancellationObserved.TrySetResult();
                    throw;
                }
            }

            await _output.WriteAsync(buffer, cancellationToken);
            Interlocked.Exchange(ref _cancelAfterNextWrite, null)?.Cancel();
        }
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

        public sealed class BlockedWrite
        {
            public TaskCompletionSource Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource CancellationObserved { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public sealed class BlockedRead
        {
            public TaskCompletionSource Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class InteractiveDuplexStream : Stream
    {
        private readonly object _sync = new();
        private readonly Queue<byte> _input = new();
        private readonly MemoryStream _output = new();
        private TaskCompletionSource _inputAvailable = NewSignal();
        private byte[]? _delivery;
        private bool _deliveryCompleted;

        public InteractiveDuplexStream(byte[] input) => AppendInput(input);

        public TaskCompletionSource FramebufferRequestObserved { get; } =
            NewSignal();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void DeliverAfterFramebufferRequest(byte[] delivery)
        {
            ArgumentNullException.ThrowIfNull(delivery);
            lock (_sync)
            {
                _delivery = delivery.ToArray();
            }
        }

        public void DeliverWithoutFramebufferRequest()
        {
            byte[]? delivery;
            lock (_sync)
            {
                if (_deliveryCompleted)
                {
                    return;
                }

                _deliveryCompleted = true;
                delivery = _delivery;
            }

            if (delivery is not null)
            {
                AppendInput(delivery);
            }
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_input.Count > 0)
                    {
                        var count = Math.Min(buffer.Length, _input.Count);
                        for (var index = 0; index < count; index++)
                        {
                            buffer.Span[index] = _input.Dequeue();
                        }

                        return count;
                    }

                    wait = _inputAvailable.Task;
                }

                await wait.WaitAsync(cancellationToken);
            }
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            byte[]? delivery = null;
            lock (_sync)
            {
                _output.Write(buffer.Span);
                if (!_deliveryCompleted &&
                    _delivery is not null &&
                    buffer.Length == 10 &&
                    buffer.Span[0] == 3)
                {
                    _deliveryCompleted = true;
                    delivery = _delivery;
                    FramebufferRequestObserved.TrySetResult();
                }
            }

            if (delivery is not null)
            {
                AppendInput(delivery);
            }

            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        private void AppendInput(byte[] input)
        {
            TaskCompletionSource signal;
            lock (_sync)
            {
                foreach (var value in input)
                {
                    _input.Enqueue(value);
                }

                signal = _inputAvailable;
                _inputAvailable = NewSignal();
            }

            signal.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
