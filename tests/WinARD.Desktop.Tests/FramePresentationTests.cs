using WinARD.Application.Ports;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Services;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Authentication;
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
            sessionIv,
            1,
            encrypted.AsSpan(secondOffset + 2, secondLength));

        Assert.Equal(new byte[] { 5, 0, 0, 100, 0, 200 }, pointer.Payload);
        Assert.Equal(new byte[] { 4, 1, 0, 0, 0, 0, 0, 0x61 }, key.Payload);
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
            [.. Handshake("RFB 003.889\n"), .. ArdServerInit(1, 1), .. DesktopSizeUpdate(2, 1)]);
        await using var client = new RfbClient(stream);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);
        using var frame = Assert.IsType<RemoteFramebufferMessage>(
            await client.ReceiveAsync(CancellationToken.None));
        await client.RequestFramebufferUpdateAsync(incremental: false, CancellationToken.None);
        await client.RequestFramebufferUpdateAsync(incremental: true, CancellationToken.None);

        Assert.Equal(new RemoteFramebufferSize(2, 1), frame.Size);
        Assert.Equal(
            [3, 0, 0, 0, 0, 0, 0, 2, 0, 1, 3, 1, 0, 0, 0, 0, 0, 2, 0, 1],
            stream.WrittenBytes[^20..]);
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
        var snapshotFactory = new FramebufferSnapshotFactory(_ => throw snapshotFailure);
        await using var stream = new ScriptedDuplexStream(
            [
                .. Handshake("RFB 003.008\n"),
                .. ServerInit(1, 1),
                0, 0, 0, 1,
                .. Header(0, 0, 1, 1, (int)RfbEncodingType.Raw),
                1, 2, 3, 4,
            ]);
        await using var client = new RfbClient(stream, snapshotFactory);

        await client.NegotiateAsync(CancellationToken.None);
        await client.InitializeAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            client.ReceiveAsync(CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.DecoderFailure, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.FramebufferRectanglePayload, exception.Failure?.ReadStage);
        Assert.Equal((byte)0, exception.Failure?.ServerMessageType);
        Assert.Equal(1105, exception.Failure?.EncodingId);
        Assert.Equal(2, exception.Failure?.RectangleIndex);
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

    private static byte[] DesktopSizeUpdate(ushort width, ushort height)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), height);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), (int)RfbEncodingType.DesktopSize);
        return bytes;
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

    private sealed class ScriptedDuplexStream(
        byte[] input,
        CancellationTokenSource? cancelAfterInput = null,
        IOException? exceptionAfterInput = null) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly MemoryStream _output = new();
        private IOException? _nextWriteFailure;

        public byte[] WrittenBytes => _output.ToArray();

        public void FailNextWrite(IOException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.CompareExchange(ref _nextWriteFailure, exception, null) is not null)
            {
                throw new InvalidOperationException("A write failure is already pending.");
            }
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
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var failure = Interlocked.Exchange(ref _nextWriteFailure, null);
            return failure is null
                ? _output.WriteAsync(buffer, cancellationToken)
                : ValueTask.FromException(failure);
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
    }
}
