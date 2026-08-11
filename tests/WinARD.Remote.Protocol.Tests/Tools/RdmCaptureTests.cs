using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinARD.ProtocolProbe.RdmCapture;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.Initialization;
using WinARD.Remote.Protocol.IO;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Tools;

public sealed class RdmCaptureTests
{
    [Fact]
    public async Task Declaration_reader_captures_known_messages_through_first_framebuffer_request_without_retaining_viewer_info()
    {
        const string privateViewerInfo = "private-workstation";
        var viewerInfo = new byte[66];
        viewerInfo[0] = 0x21;
        Encoding.UTF8.GetBytes(privateViewerInfo).CopyTo(viewerInfo, 1);
        byte[] setMode = [0x0A, 0x00, 0x00, 0x01];
        byte[] setDisplay = [0x0D, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] setPixelFormat = [0x00, 0x00, 0x00, 0x00, .. PixelFormat.WinArdBgra32.ToWireBytes()];
        byte[] setEncodings =
        [
            0x02, 0x00, 0x00, 0x04,
            0x00, 0x00, 0x30, 0x39,
            0x00, 0x00, 0x00, 0x10,
            0x00, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0x21,
        ];
        byte[] framebufferRequest = [0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x80, 0x04, 0x38];
        await using var stream = new MemoryStream(
            [.. viewerInfo, .. setMode, .. setDisplay, .. setPixelFormat, .. setEncodings, .. framebufferRequest]);

        var result = await RfbClientDeclarationReader.ReadAsync(stream, CancellationToken.None);

        var expectedPixelFormat = PixelFormat.WinArdBgra32;
        Assert.Equal(
            new CapturedPixelFormat(
                expectedPixelFormat.BitsPerPixel,
                expectedPixelFormat.Depth,
                expectedPixelFormat.BigEndian,
                expectedPixelFormat.TrueColor,
                expectedPixelFormat.RedMax,
                expectedPixelFormat.GreenMax,
                expectedPixelFormat.BlueMax,
                expectedPixelFormat.RedShift,
                expectedPixelFormat.GreenShift,
                expectedPixelFormat.BlueShift),
            result.PixelFormat);
        Assert.Equal([12345, 16, 0, -223], result.Encodings);
        Assert.True(result.ReachedFramebufferRequest);
        Assert.Null(result.StoppedAtUnknownMessageType);
        Assert.Equal(6, result.Messages.Count);
        Assert.Equal(
            new CapturedClientMessage(
                0x21,
                "ViewerInfo",
                66,
                null,
                Convert.ToHexString(SHA256.HashData(viewerInfo))),
            result.Messages[0]);
        Assert.Null(result.Messages[^1].NumericPayloadHex);

        var report = new RdmCaptureReport(
            1,
            "adaptive-default",
            "RFB 003.889",
            0xC1,
            result.PixelFormat,
            result.Encodings,
            result.Messages,
            result.ReachedFramebufferRequest,
            result.StoppedAtUnknownMessageType);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(privateViewerInfo, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declaration_reader_captures_auto_update_and_both_known_set_encryption_opcodes()
    {
        byte[] autoFramebufferUpdate =
            [0x09, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x80, 0x04, 0x38];
        byte[] encryptionRequest =
            [0x12, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01];
        byte[] encryptionAcknowledgement = [0x12, 0x00, 0x00, 0x02, 0x00, 0x01, 0x00, 0x00];
        byte[] framebufferRequest = [0x03, 0x01, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04];
        await using var stream = new MemoryStream(
            [.. autoFramebufferUpdate, .. encryptionRequest, .. encryptionAcknowledgement, .. framebufferRequest]);

        var result = await RfbClientDeclarationReader.ReadAsync(stream, CancellationToken.None);

        Assert.Collection(
            result.Messages,
            message => Assert.Equal(
                new CapturedClientMessage(
                    0x09,
                    "AutoFramebufferUpdate",
                    16,
                    "000001000000000000000007800438",
                    null),
                message),
            message => Assert.Equal(
                new CapturedClientMessage(
                    0x12,
                    "SetEncryption",
                    12,
                    "0000010001000100000001",
                    null),
                message),
            message => Assert.Equal(
                new CapturedClientMessage(0x12, "SetEncryption", 8, "00000200010000", null),
                message),
            message => Assert.Equal(
                new CapturedClientMessage(
                    0x03,
                    "FramebufferUpdateRequest",
                    10,
                    null,
                    null),
                message));
    }

    [Theory]
    [InlineData(0x04)]
    [InlineData(0x05)]
    [InlineData(0x06)]
    [InlineData(0x7F)]
    public async Task Declaration_reader_stops_at_unknown_message_type_without_consuming_payload(byte type)
    {
        await using var stream = new MemoryStream([type, 0xA5, 0xB6]);

        var result = await RfbClientDeclarationReader.ReadAsync(stream, CancellationToken.None);

        Assert.False(result.ReachedFramebufferRequest);
        Assert.Equal(type, result.StoppedAtUnknownMessageType);
        Assert.Empty(result.Messages);
        Assert.Equal(1, stream.Position);
    }

    [Fact]
    public async Task Declaration_reader_rejects_set_encodings_count_above_4096_before_reading_ids()
    {
        await using var stream = new MemoryStream([0x02, 0xA5, 0x10, 0x01, 0xB6]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RfbClientDeclarationReader.ReadAsync(stream, CancellationToken.None));

        Assert.Equal("The RFB SetEncodings count exceeds 4096.", exception.Message);
        Assert.Equal(4, stream.Position);
        Assert.DoesNotContain("A5", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("B6", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Declaration_reader_rejects_more_than_64_messages()
    {
        byte[] setMode = [0x0A, 0x00, 0x00, 0x01];
        await using var stream = new MemoryStream(
            Enumerable.Range(0, 65).SelectMany(_ => setMode).ToArray());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RfbClientDeclarationReader.ReadAsync(stream, CancellationToken.None));

        Assert.Equal("The RFB client declaration exceeds 64 messages.", exception.Message);
    }

    [Fact]
    public async Task Declaration_reader_accepts_framebuffer_request_as_exactly_64th_message()
    {
        byte[] setMode = [0x0A, 0x00, 0x00, 0x01];
        byte[] framebufferRequest = [0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x80, 0x04, 0x38];
        await using var stream = new MemoryStream(
            [.. Enumerable.Range(0, 63).SelectMany(_ => setMode), .. framebufferRequest]);

        var result = await RfbClientDeclarationReader.ReadAsync(stream, CancellationToken.None);

        Assert.True(result.ReachedFramebufferRequest);
        Assert.Null(result.StoppedAtUnknownMessageType);
        Assert.Equal(64, result.Messages.Count);
        Assert.Equal("FramebufferUpdateRequest", result.Messages[^1].Name);
        Assert.Null(result.Messages[^1].NumericPayloadHex);
    }

    [Fact]
    public async Task Declaration_reader_rejects_more_than_64_kibibytes()
    {
        var maximumSetEncodings = new byte[4 + (4096 * sizeof(int))];
        maximumSetEncodings[0] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(maximumSetEncodings.AsSpan(2), 4096);
        await using var stream = new MemoryStream(
            Enumerable.Range(0, 4).SelectMany(_ => maximumSetEncodings).ToArray());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RfbClientDeclarationReader.ReadAsync(stream, CancellationToken.None));

        Assert.Equal("The RFB client declaration exceeds 65536 bytes.", exception.Message);
    }

    [Fact]
    public async Task Declaration_reader_rejects_unknown_set_encryption_opcode_without_guessing_its_length()
    {
        await using var stream = new MemoryStream([0x12, 0x00, 0xA5, 0xB6, 0xC7]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RfbClientDeclarationReader.ReadAsync(stream, CancellationToken.None));

        Assert.Equal("The RFB SetEncryption opcode is unsupported.", exception.Message);
        Assert.Equal(4, stream.Position);
        Assert.DoesNotContain("A5", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("B6", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C7", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Declaration_reader_reports_truncation_without_echoing_payload()
    {
        var privatePayload = Encoding.UTF8.GetBytes("private-truncated-viewer-info");
        await using var stream = new MemoryStream([0x21, .. privatePayload]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RfbClientDeclarationReader.ReadAsync(stream, CancellationToken.None));

        Assert.Equal("The RFB client declaration was truncated.", exception.Message);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Declaration_reader_honors_pre_cancelled_token()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream([0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RfbClientDeclarationReader.ReadAsync(stream, cancellation.Token));

        Assert.Equal("The RFB client declaration was cancelled.", exception.Message);
    }

    [Fact]
    public async Task Capture_server_sends_synthetic_server_init_and_round_trips_captured_declarations()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var port = ReserveLoopbackPort();
        var serverTask = new RdmCaptureServer().CaptureOnceAsync(
            port,
            "adaptive-default",
            cancellation.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellation.Token);
        await using var stream = client.GetStream();
        using var username = SecretMaterial.FromUtf8("synthetic-user");
        using var password = SecretMaterial.FromUtf8("synthetic-password");
        var handshake = await RfbHandshake.NegotiateAsync(stream, cancellation.Token);
        using var authentication = await new ArdAuthenticator().AuthenticateAsync(
            stream,
            handshake.Version,
            username,
            password,
            cancellation.Token);

        var serverInit = await RfbSessionInitializer.InitializeAsync(
            stream,
            handshake,
            ProtocolLimits.Default,
            cancellation.Token);
        Assert.Equal(1920, serverInit.Width);
        Assert.Equal(1080, serverInit.Height);
        Assert.Equal(PixelFormat.WinArdBgra32, serverInit.PixelFormat);
        Assert.Equal("WinARD Synthetic Probe", serverInit.Name);
        Assert.NotNull(serverInit.ArdCapabilities);
        Assert.True(serverInit.ArdCapabilities.MayControl);
        Assert.False(serverInit.ArdCapabilities.RequiresSessionSelection);

        await RfbSessionInitializer.WriteFramebufferUpdateRequestAsync(
            stream,
            incremental: false,
            0,
            0,
            1920,
            1080,
            cancellation.Token);
        var report = await serverTask.WaitAsync(cancellation.Token);
        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal("adaptive-default", report.Profile);
        Assert.Equal("RFB 003.889", report.ClientVersion);
        Assert.Equal(0xC1, report.ClientInit);
        Assert.True(report.ReachedFramebufferRequest);
        Assert.Null(report.StoppedAtUnknownMessageType);
        Assert.Equal(
            new CapturedPixelFormat(32, 24, false, true, 255, 255, 255, 16, 8, 0),
            report.PixelFormat);
        Assert.NotEmpty(report.Encodings);

        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "adaptive-default.json");
        try
        {
            await RdmCaptureFile.WriteAsync(path, report, cancellation.Token);
            var restored = await RdmCaptureFile.ReadAsync(path, cancellation.Token);
            Assert.Equal(report.SchemaVersion, restored.SchemaVersion);
            Assert.Equal(report.Profile, restored.Profile);
            Assert.Equal(report.ClientVersion, restored.ClientVersion);
            Assert.Equal(report.ClientInit, restored.ClientInit);
            Assert.Equal(report.PixelFormat, restored.PixelFormat);
            Assert.Equal(report.Encodings, restored.Encodings);
            Assert.Equal(report.Messages, restored.Messages);
            Assert.Equal(report.ReachedFramebufferRequest, restored.ReachedFramebufferRequest);
            Assert.Equal(report.StoppedAtUnknownMessageType, restored.StoppedAtUnknownMessageType);
            Assert.Equal([Path.GetFullPath(path)], Directory.EnumerateFiles(directory).Select(Path.GetFullPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Capture_server_binds_only_127_0_0_1_and_stops_accepting_after_first_connection()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var port = ReserveLoopbackPort();
        var serverTask = new RdmCaptureServer().CaptureOnceAsync(port, "adaptive-default", cancellation.Token);
        using var alternateLoopbackClient = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
            await alternateLoopbackClient.ConnectAsync(
                IPAddress.Parse("127.0.0.2"),
                port,
                cancellation.Token));

        using var firstClient = new TcpClient();
        await firstClient.ConnectAsync(IPAddress.Loopback, port, cancellation.Token);
        await using var firstStream = firstClient.GetStream();
        Assert.Equal(
            "RFB 003.889\n",
            Encoding.ASCII.GetString(await ReadExactlyAsync(firstStream, 12, cancellation.Token)));
        using var secondClient = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
            await secondClient.ConnectAsync(IPAddress.Loopback, port, cancellation.Token));

        await firstStream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.007\n"), cancellation.Token);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await serverTask.WaitAsync(cancellation.Token));
    }

    [Fact]
    public async Task Capture_server_reports_configured_no_progress_timeout_without_echoing_input()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), RdmCaptureServer.DefaultInactivityTimeout);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var port = ReserveLoopbackPort();
        var serverTask = new RdmCaptureServer(TimeSpan.FromMilliseconds(100)).CaptureOnceAsync(
            port,
            "adaptive-default",
            cancellation.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellation.Token);
        await using var stream = client.GetStream();
        var serverBanner = await ReadExactlyAsync(stream, 12, cancellation.Token);
        Assert.Equal("RFB 003.889\n", Encoding.ASCII.GetString(serverBanner));

        var exception = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await serverTask.WaitAsync(cancellation.Token));

        Assert.Equal("RDM capture made no progress for 0.1 seconds.", exception.Message);
        Assert.DoesNotContain("RFB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_server_resets_inactivity_timeout_while_declaration_bytes_keep_progressing()
    {
        var inactivityTimeout = TimeSpan.FromSeconds(1);
        var progressInterval = TimeSpan.FromMilliseconds(400);
        using var hardTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cancellationToken = hardTimeout.Token;
        var port = ReserveLoopbackPort();
        var serverTask = new RdmCaptureServer(inactivityTimeout).CaptureOnceAsync(
            port,
            "adaptive-default",
            cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        await using var stream = client.GetStream();
        using var username = SecretMaterial.FromUtf8("synthetic-user");
        using var password = SecretMaterial.FromUtf8("synthetic-password");
        var handshake = await RfbHandshake.NegotiateAsync(stream, cancellationToken);
        using var authentication = await new ArdAuthenticator().AuthenticateAsync(
            stream,
            handshake.Version,
            username,
            password,
            cancellationToken);
        await stream.WriteAsync(new byte[] { 0xC1 }, cancellationToken);
        var serverInitHeader = await ReadExactlyAsync(stream, 24, cancellationToken);
        var extendedNameLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(serverInitHeader.AsSpan(20)));
        _ = await ReadExactlyAsync(stream, extendedNameLength, cancellationToken);

        byte[] framebufferRequest = [0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x80, 0x04, 0x38];
        var progressStopwatch = Stopwatch.StartNew();
        for (var offset = 0; offset < framebufferRequest.Length; offset += 2)
        {
            await stream.WriteAsync(framebufferRequest.AsMemory(offset, 2), cancellationToken);
            if (offset + 2 < framebufferRequest.Length)
            {
                await Task.Delay(progressInterval, cancellationToken);
            }
        }

        var report = await serverTask.WaitAsync(cancellationToken);

        Assert.True(progressStopwatch.Elapsed > inactivityTimeout);
        Assert.True(report.ReachedFramebufferRequest);
        Assert.Null(report.StoppedAtUnknownMessageType);
        Assert.Null(report.Messages[^1].NumericPayloadHex);
    }

    [Fact]
    public async Task Ard_server_handshake_accepts_real_889_client_without_retaining_authentication_response()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cancellationToken = cancellation.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = AcceptHandshakeAsync(listener, cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, GetPort(listener), cancellationToken);
        await using var stream = client.GetStream();
        using var username = SecretMaterial.FromUtf8("synthetic-user");
        using var password = SecretMaterial.FromUtf8("synthetic-password");

        var negotiated = await RfbHandshake.NegotiateAsync(stream, cancellationToken);
        using var authentication = await new ArdAuthenticator().AuthenticateAsync(
            stream,
            negotiated.Version,
            username,
            password,
            cancellationToken);
        await stream.WriteAsync(new byte[] { 0xC1 }, cancellationToken);

        var result = await serverTask.WaitAsync(cancellationToken);
        Assert.Equal(RfbVersion.V3_889, result.Version);
        Assert.Equal(0xC1, result.ClientInit);
        Assert.Equal(192, result.DiscardedAuthenticationResponseBytes);
        var propertyNames = string.Join(',', result.GetType().GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("credential", propertyNames, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("host", propertyNames, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawresponse", propertyNames, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.GetType().GetProperties(), property => property.PropertyType == typeof(byte[]));
    }

    [Fact]
    public async Task Ard_server_handshake_rejects_unsupported_client_banner()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cancellationToken = cancellation.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = AcceptHandshakeAsync(listener, cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, GetPort(listener), cancellationToken);
        await using var stream = client.GetStream();

        Assert.Equal(
            "RFB 003.889\n",
            Encoding.ASCII.GetString(await ReadExactlyAsync(stream, 12, cancellationToken)));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.007\n"), cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await serverTask.WaitAsync(cancellationToken));
        Assert.Equal("The RFB client version banner is not supported.", exception.Message);
    }

    [Fact]
    public async Task Ard_server_handshake_rejects_security_selection_other_than_ard()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cancellationToken = cancellation.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = AcceptHandshakeAsync(listener, cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, GetPort(listener), cancellationToken);
        await using var stream = client.GetStream();

        _ = await ReadExactlyAsync(stream, 12, cancellationToken);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), cancellationToken);
        Assert.Equal(new byte[] { 1, 30 }, await ReadExactlyAsync(stream, 2, cancellationToken));
        await stream.WriteAsync(new byte[] { 1 }, cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await serverTask.WaitAsync(cancellationToken));
        Assert.Equal("The RFB client did not select Apple Remote Desktop security.", exception.Message);
    }

    [Fact]
    public async Task Ard_server_handshake_rejects_truncated_authentication_response_without_echoing_it()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cancellationToken = cancellation.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = AcceptHandshakeAsync(listener, cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, GetPort(listener), cancellationToken);
        await using var stream = client.GetStream();

        _ = await ReadExactlyAsync(stream, 12, cancellationToken);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.889\n"), cancellationToken);
        _ = await ReadExactlyAsync(stream, 2, cancellationToken);
        await stream.WriteAsync(new byte[] { 30 }, cancellationToken);
        _ = await ReadExactlyAsync(stream, 132, cancellationToken);
        var partialResponse = Enumerable.Repeat((byte)0xA7, 191).ToArray();
        await stream.WriteAsync(partialResponse, cancellationToken);
        client.Client.Shutdown(SocketShutdown.Send);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await serverTask.WaitAsync(cancellationToken));
        Assert.Equal("The ARD authentication response was truncated.", exception.Message);
        Assert.DoesNotContain("A7", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ard_server_handshake_rejects_invalid_client_init()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cancellationToken = cancellation.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = AcceptHandshakeAsync(listener, cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, GetPort(listener), cancellationToken);
        await using var stream = client.GetStream();
        using var username = SecretMaterial.FromUtf8("synthetic-user");
        using var password = SecretMaterial.FromUtf8("synthetic-password");

        var negotiated = await RfbHandshake.NegotiateAsync(stream, cancellationToken);
        using var authentication = await new ArdAuthenticator().AuthenticateAsync(
            stream,
            negotiated.Version,
            username,
            password,
            cancellationToken);
        await stream.WriteAsync(new byte[] { 0x00 }, cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await serverTask.WaitAsync(cancellationToken));
        Assert.Equal("The RFB ClientInit value is not supported.", exception.Message);
    }

    [Fact]
    public async Task Ard_server_handshake_honors_cancellation_while_reading_client_banner()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cancellationToken = cancellation.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = AcceptHandshakeAsync(listener, cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, GetPort(listener), cancellationToken);
        await using var stream = client.GetStream();
        _ = await ReadExactlyAsync(stream, 12, cancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Ard_server_handshake_zeroes_discarded_authentication_response_after_success()
    {
        var clientBytes = CreateScriptedHandshakeClientBytes(192, 0xC1);
        await using var stream = new AuthenticationReadCaptureStream(clientBytes);

        var result = await ArdProbeServerHandshake.AcceptAsync(stream, CancellationToken.None);

        Assert.Equal(192, result.DiscardedAuthenticationResponseBytes);
        Assert.True(stream.CapturedAuthenticationResponse);
        AssertZeroed(stream.AuthenticationResponseTarget);
    }

    [Fact]
    public async Task Ard_server_handshake_zeroes_partial_authentication_response_after_truncation()
    {
        var clientBytes = CreateScriptedHandshakeClientBytes(191, clientInit: null);
        await using var stream = new AuthenticationReadCaptureStream(clientBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArdProbeServerHandshake.AcceptAsync(stream, CancellationToken.None));

        Assert.True(stream.CapturedAuthenticationResponse);
        AssertZeroed(stream.AuthenticationResponseTarget);
    }

    [Fact]
    public async Task Capture_file_round_trips_camel_case_json_without_sensitive_fields()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "capture.json");
        var report = CreateReport(
            encodings: [0, -223, -309],
            messages:
            [
                new CapturedClientMessage(0, "SetPixelFormat", 20, "0020", null),
                new CapturedClientMessage(3, "FramebufferUpdateRequest", 10, "0000000004000300", null),
            ]);

        try
        {
            await RdmCaptureFile.WriteAsync(path, report, CancellationToken.None);

            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
            Assert.Contains("\"bitsPerPixel\": 32", json, StringComparison.Ordinal);
            Assert.DoesNotContain("SchemaVersion", json, StringComparison.Ordinal);
            foreach (var sensitiveName in new[] { "host", "username", "password", "credential", "rawAuthResponse" })
            {
                Assert.DoesNotContain(sensitiveName, json, StringComparison.OrdinalIgnoreCase);
            }

            var restored = await RdmCaptureFile.ReadAsync(path, CancellationToken.None);
            Assert.Equal(report.SchemaVersion, restored.SchemaVersion);
            Assert.Equal(report.Profile, restored.Profile);
            Assert.Equal(report.ClientVersion, restored.ClientVersion);
            Assert.Equal(report.ClientInit, restored.ClientInit);
            Assert.Equal(report.PixelFormat, restored.PixelFormat);
            Assert.Equal(report.Encodings, restored.Encodings);
            Assert.Equal(report.Messages, restored.Messages);
            Assert.Equal(report.ReachedFramebufferRequest, restored.ReachedFramebufferRequest);
            Assert.Equal(report.StoppedAtUnknownMessageType, restored.StoppedAtUnknownMessageType);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Write_creates_parent_directory_atomically_overwrites_and_removes_temporary_files()
    {
        var root = CreateTemporaryDirectory();
        var directory = Path.Combine(root, "nested", "captures");
        var path = Path.Combine(directory, "capture.json");
        try
        {
            await RdmCaptureFile.WriteAsync(path, CreateReport(profile: "first"), CancellationToken.None);
            await RdmCaptureFile.WriteAsync(path, CreateReport(profile: "second"), CancellationToken.None);

            var restored = await RdmCaptureFile.ReadAsync(path, CancellationToken.None);
            Assert.Equal("second", restored.Profile);
            Assert.Equal([Path.GetFullPath(path)], Directory.EnumerateFiles(directory).Select(Path.GetFullPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":2,\"profile\":\"valid\",\"clientVersion\":\"3.8\",\"clientInit\":1,\"encodings\":[],\"messages\":[],\"reachedFramebufferRequest\":false}")]
    [InlineData("{\"schemaVersion\":1,\"profile\":\"valid\",\"clientVersion\":null,\"clientInit\":1,\"encodings\":[],\"messages\":[],\"reachedFramebufferRequest\":false}")]
    [InlineData("{\"schemaVersion\":1,\"profile\":\"valid\",\"clientVersion\":\"3.8\",\"clientInit\":1,\"encodings\":null,\"messages\":[],\"reachedFramebufferRequest\":false}")]
    [InlineData("{\"schemaVersion\":1,\"profile\":\"valid\",\"clientVersion\":\"3.8\",\"clientInit\":1,\"encodings\":[],\"messages\":null,\"reachedFramebufferRequest\":false}")]
    [InlineData("{\"schemaVersion\":1,\"profile\":\"valid\",\"clientVersion\":\"3.8\",\"clientInit\":1,\"encodings\":[],\"messages\":[{\"type\":0,\"name\":null,\"wireLength\":1}],\"reachedFramebufferRequest\":false}")]
    public async Task Read_rejects_empty_unsupported_or_incomplete_models(string json)
    {
        await AssertInvalidCaptureAsync(json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("contains space")]
    [InlineData("contains_underscore")]
    [InlineData("123456789012345678901234567890123")]
    public async Task Read_rejects_invalid_profiles(string profile)
    {
        await AssertInvalidCaptureAsync(CreateJson(profile: profile));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("aa")]
    [InlineData("0G")]
    [InlineData("00-")]
    public async Task Read_rejects_invalid_numeric_payload_hex(string numericPayloadHex)
    {
        var messages = $"[{{\"type\":3,\"name\":\"Request\",\"wireLength\":10,\"numericPayloadHex\":{JsonSerializer.Serialize(numericPayloadHex)}}}]";

        await AssertInvalidCaptureAsync(CreateJson(messages: messages));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Read_rejects_oversized_lists(bool oversizeEncodings)
    {
        var entries = string.Join(',', Enumerable.Repeat("0", 4097));
        var encodings = oversizeEncodings ? $"[{entries}]" : "[]";
        var messages = oversizeEncodings
            ? "[]"
            : $"[{string.Join(',', Enumerable.Repeat("{\"type\":0,\"name\":\"Message\",\"wireLength\":1}", 4097))}]";

        await AssertInvalidCaptureAsync(CreateJson(encodings: encodings, messages: messages));
    }

    [Fact]
    public async Task Read_rejects_files_larger_than_four_mibibytes_before_deserialization()
    {
        const int maximumFileLength = 4 * 1024 * 1024;
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "oversized.json");
        try
        {
            await File.WriteAllTextAsync(path, new string(' ', maximumFileLength + 1));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                RdmCaptureFile.ReadAsync(path, CancellationToken.None));

            Assert.Contains(
                maximumFileLength.ToString(CultureInfo.InvariantCulture),
                exception.Message,
                StringComparison.Ordinal);
            Assert.Null(exception.InnerException);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Read_rejects_oversized_client_version()
    {
        await AssertInvalidCaptureAsync(CreateJson(clientVersion: new string('v', 33)));
    }

    [Fact]
    public async Task Read_rejects_oversized_message_name()
    {
        var messages = $"[{{\"type\":0,\"name\":{JsonSerializer.Serialize(new string('n', 65))},\"wireLength\":1}}]";

        await AssertInvalidCaptureAsync(CreateJson(messages: messages));
    }

    [Fact]
    public async Task Read_rejects_numeric_payload_hex_larger_than_4096_characters()
    {
        var messages = $"[{{\"type\":0,\"name\":\"Message\",\"wireLength\":1,\"numericPayloadHex\":{JsonSerializer.Serialize(new string('A', 4098))}}}]";

        await AssertInvalidCaptureAsync(CreateJson(messages: messages));
    }

    [Fact]
    public async Task Read_rejects_non_null_sha256_that_is_not_64_uppercase_hex_characters()
    {
        var invalidHashes = new[]
        {
            string.Empty,
            new string('A', 63),
            new string('a', 64),
            new string('G', 64),
        };

        foreach (var hash in invalidHashes)
        {
            var messages = $"[{{\"type\":0,\"name\":\"Message\",\"wireLength\":1,\"payloadSha256\":{JsonSerializer.Serialize(hash)}}}]";
            await AssertInvalidCaptureAsync(CreateJson(messages: messages));
        }
    }

    [Fact]
    public void Capture_report_snapshots_source_collections()
    {
        var encodings = new List<int> { 0 };
        var messages = new List<CapturedClientMessage>
        {
            new(3, "FramebufferUpdateRequest", 10, "0000000004000300", null),
        };
        var report = CreateReport(encodings: encodings, messages: messages);

        encodings.Add(-309);
        messages.Add(new CapturedClientMessage(0, "SetPixelFormat", 20, "0020", null));

        Assert.Equal([0], report.Encodings);
        Assert.Single(report.Messages);
        Assert.IsNotType<List<int>>(report.Encodings);
        Assert.IsNotType<List<CapturedClientMessage>>(report.Messages);
    }

    [Fact]
    public async Task Write_serializes_the_capture_snapshot_not_mutated_source_collections()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "snapshot.json");
        var encodings = new List<int> { 0 };
        var messages = new List<CapturedClientMessage>
        {
            new(3, "FramebufferUpdateRequest", 10, "0000000004000300", null),
        };
        var report = CreateReport(encodings: encodings, messages: messages);
        encodings.Add(-309);
        messages.Clear();

        try
        {
            await RdmCaptureFile.WriteAsync(path, report, CancellationToken.None);

            var restored = await RdmCaptureFile.ReadAsync(path, CancellationToken.None);
            Assert.Equal([0], restored.Encodings);
            Assert.Single(restored.Messages);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Write_uses_short_random_temporary_name_and_removes_it()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "capture-with-a-descriptive-target-name.json");
        var temporaryName = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, "*.tmp")
        {
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, args) => temporaryName.TrySetResult(args.Name ?? string.Empty);

        try
        {
            await RdmCaptureFile.WriteAsync(path, CreateReport(), CancellationToken.None);

            var observedName = await temporaryName.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Matches("^\\.winard-rdm-[0-9a-f]{32}\\.tmp$", observedName);
            Assert.DoesNotContain(Path.GetFileName(path), observedName, StringComparison.Ordinal);
            Assert.DoesNotContain(Directory.EnumerateFiles(directory), candidate =>
                string.Equals(Path.GetExtension(candidate), ".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Comparer_returns_the_only_distinct_signed_adaptive_encoding_in_adaptive_order()
    {
        var baseline = CreateReport(encodings: [0, -223, 16]);
        var adaptive = CreateReport(encodings: [-223, -309, -309, 16]);

        var result = RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(baseline, adaptive);

        Assert.Equal(-309, result);
    }

    [Fact]
    public void Comparer_rejects_zero_adaptive_only_encodings()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(
                CreateReport(encodings: [0, -223]),
                CreateReport(encodings: [-223, 0, 0])));

        Assert.DoesNotContain("MVS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comparer_rejects_multiple_adaptive_only_encodings()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(
                CreateReport(encodings: [0]),
                CreateReport(encodings: [-309, -310])));

        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MVS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comparer_rejects_mismatched_schemas()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(
                CreateReport(schemaVersion: 1),
                CreateReport(schemaVersion: 2)));

        Assert.Contains("1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MVS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comparer_rejects_null_reports()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(null!, CreateReport()));
        Assert.Throws<ArgumentNullException>(() =>
            RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(CreateReport(), null!));
    }

    [Fact]
    public async Task Read_and_write_honor_pre_cancelled_tokens()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "capture.json");
        await File.WriteAllTextAsync(path, CreateJson());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RdmCaptureFile.ReadAsync(path, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RdmCaptureFile.WriteAsync(path, CreateReport(), cancellation.Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RdmCaptureReport CreateReport(
        int schemaVersion = 1,
        string profile = "adaptive-default",
        string clientVersion = "RFB 003.008",
        IReadOnlyList<int>? encodings = null,
        IReadOnlyList<CapturedClientMessage>? messages = null) =>
        new(
            schemaVersion,
            profile,
            clientVersion,
            1,
            new CapturedPixelFormat(32, 24, false, true, 255, 255, 255, 16, 8, 0),
            encodings ?? [0],
            messages ?? [new CapturedClientMessage(3, "FramebufferUpdateRequest", 10, "0000000004000300", null)],
            true,
            null);

    private static async Task AssertInvalidCaptureAsync(string json)
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "invalid.json");
        try
        {
            await File.WriteAllTextAsync(path, json);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RdmCaptureFile.ReadAsync(path, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateJson(
        string profile = "valid-profile",
        string clientVersion = "RFB 003.008",
        string encodings = "[]",
        string messages = "[]") =>
        $$"""
        {
          "schemaVersion": 1,
          "profile": {{JsonSerializer.Serialize(profile)}},
          "clientVersion": {{JsonSerializer.Serialize(clientVersion)}},
          "clientInit": 1,
          "pixelFormat": null,
          "encodings": {{encodings}},
          "messages": {{messages}},
          "reachedFramebufferRequest": false,
          "stoppedAtUnknownMessageType": null
        }
        """;

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-rdm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<ArdProbeServerHandshakeResult> AcceptHandshakeAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        return await ArdProbeServerHandshake.AcceptAsync(client.GetStream(), cancellationToken);
    }

    private static async Task<byte[]> ReadExactlyAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    private static int GetPort(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return GetPort(listener);
    }

    private static void AssertZeroed(ReadOnlyMemory<byte> memory)
    {
        Assert.Equal(192, memory.Length);
        Assert.DoesNotContain(memory.ToArray(), value => value != 0);
    }

    private static byte[] CreateScriptedHandshakeClientBytes(int responseLength, byte? clientInit)
    {
        var bytes = new List<byte>(13 + responseLength + (clientInit.HasValue ? 1 : 0));
        bytes.AddRange(Encoding.ASCII.GetBytes("RFB 003.889\n"));
        bytes.Add(30);
        bytes.AddRange(Enumerable.Repeat((byte)0xA7, responseLength));
        if (clientInit.HasValue)
        {
            bytes.Add(clientInit.Value);
        }

        return bytes.ToArray();
    }

    private sealed class AuthenticationReadCaptureStream(byte[] clientBytes) : Stream
    {
        private const int AuthenticationResponseOffset = 13;

        private int _position;

        public bool CapturedAuthenticationResponse { get; private set; }

        public Memory<byte> AuthenticationResponseTarget { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new InvalidOperationException("The handshake must not flush the stream.");

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position == AuthenticationResponseOffset && !CapturedAuthenticationResponse)
            {
                CapturedAuthenticationResponse = true;
                AuthenticationResponseTarget = buffer;
            }

            var count = Math.Min(buffer.Length, clientBytes.Length - _position);
            if (count == 0)
            {
                return ValueTask.FromResult(0);
            }

            clientBytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
