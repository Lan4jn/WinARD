using System.Buffers.Binary;
using System.Text;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.Initialization;
using WinARD.Remote.Protocol.IO;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Initialization;

public sealed class RfbSessionInitializerTests
{
    [Fact]
    public async Task Set_pixel_format_writer_emits_exact_rgb565_wire_message()
    {
        await using var stream = new ScriptedDuplexStream([]);

        await RfbSessionInitializer.WriteSetPixelFormatAsync(
            stream,
            PixelFormat.WinArdRgb565,
            CancellationToken.None);

        Assert.Equal(
            Convert.FromHexString("0000000010100001001F003F001F0B0500000000"),
            stream.WrittenBytes);
        Assert.Equal([20], stream.WriteLengths);
        Assert.Equal(0, stream.FlushCount);
        Assert.False(stream.WasDisposed);
    }

    [Fact]
    public async Task Set_pixel_format_writer_emits_exact_bgra32_wire_message()
    {
        await using var stream = new MemoryStream();

        await RfbSessionInitializer.WriteSetPixelFormatAsync(
            stream,
            PixelFormat.WinArdBgra32,
            CancellationToken.None);

        Assert.Equal(
            Convert.FromHexString("000000002018000100FF00FF00FF100800000000"),
            stream.ToArray());
    }

    [Fact]
    public async Task Set_pixel_format_writer_validates_before_writing()
    {
        await using var stream = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            RfbSessionInitializer.WriteSetPixelFormatAsync(
                null!, PixelFormat.WinArdBgra32, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            RfbSessionInitializer.WriteSetPixelFormatAsync(
                stream, null!, CancellationToken.None));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RfbSessionInitializer.WriteSetPixelFormatAsync(
                stream, PixelFormat.WinArdBgra32, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public void Session_declaration_defensively_copies_encodings_and_preserves_signed_order()
    {
        int[] encodings = [6, 16, 0, 1, -239, -223];
        var declaration = new RfbSessionDeclaration(PixelFormat.WinArdRgb565, encodings);

        encodings[0] = 999;

        Assert.Same(PixelFormat.WinArdRgb565, declaration.PixelFormat);
        Assert.Equal([6, 16, 0, 1, -239, -223], declaration.Encodings);
        var mutableView = Assert.IsAssignableFrom<IList<int>>(declaration.Encodings);
        Assert.True(mutableView.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = 999);
    }

    [Fact]
    public void Session_declaration_validates_inputs_and_default_prefers_zlib()
    {
        Assert.Throws<ArgumentNullException>(() => new RfbSessionDeclaration(null!, []));
        Assert.Throws<ArgumentNullException>(() => new RfbSessionDeclaration(PixelFormat.WinArdBgra32, null!));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RfbSessionDeclaration(PixelFormat.WinArdBgra32, new int[ushort.MaxValue + 1]));

        Assert.Equal("encodings", exception.ParamName);
        Assert.Same(PixelFormat.WinArdBgra32, RfbSessionDeclaration.Default.PixelFormat);
        Assert.Equal([6, 16, 0, 1, -239, -223], RfbSessionDeclaration.Default.Encodings);
    }

    [Fact]
    public async Task Session_declaration_validates_the_snapshot_before_initializer_io()
    {
        var encodings = new MisreportedEncodingList(
            reportedCount: 0,
            actualCount: ushort.MaxValue + 1);
        await using var stream = new ScriptedDuplexStream([]);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_8),
                new RfbSessionDeclaration(PixelFormat.WinArdBgra32, encodings),
                ProtocolLimits.Default,
                CancellationToken.None));

        Assert.Equal("encodings", exception.ParamName);
        Assert.Empty(stream.WrittenBytes);
        Assert.Equal(0, stream.ReadPosition);
    }

    [Fact]
    public async Task Session_declaration_controls_standard_pixel_format_and_encoding_wire_order()
    {
        var declaration = new RfbSessionDeclaration(
            PixelFormat.WinArdRgb565,
            [6, 16, 0, 1, -239, -223]);
        await using var stream = new ScriptedDuplexStream(
            ServerInit(640, 480, PixelFormat.WinArdBgra32, "Studio Mac"));

        _ = await RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_8),
            declaration,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(
            [
                new byte[] { 1 },
                Convert.FromHexString("0000000010100001001F003F001F0B0500000000"),
                SetEncodingsMessage(6, 16, 0, 1, -239, -223),
            ],
            stream.Writes);
    }

    [Fact]
    public void Explicit_handshake_initializer_overload_is_available()
    {
        var overload = typeof(RfbSessionInitializer).GetMethod(
            nameof(RfbSessionInitializer.InitializeAsync),
            [typeof(Stream), typeof(RfbHandshakeResult), typeof(ProtocolLimits), typeof(CancellationToken)]);

        Assert.NotNull(overload);
    }

    [Fact]
    public void Initializer_public_overload_shapes_remain_available()
    {
        Assert.NotNull(typeof(RfbSessionInitializer).GetMethod(
            nameof(RfbSessionInitializer.InitializeAsync),
            [typeof(Stream), typeof(RfbHandshakeResult), typeof(ProtocolLimits), typeof(CancellationToken)]));
        Assert.NotNull(typeof(RfbSessionInitializer).GetMethod(
            nameof(RfbSessionInitializer.InitializeAsync),
            [
                typeof(Stream),
                typeof(RfbHandshakeResult),
                typeof(ProtocolLimits),
                typeof(ArdSessionEncryption),
                typeof(CancellationToken),
            ]));
        Assert.NotNull(typeof(RfbSessionInitializer).GetMethod(
            nameof(RfbSessionInitializer.InitializeAsync),
            [
                typeof(Stream),
                typeof(RfbHandshakeResult),
                typeof(RfbSessionDeclaration),
                typeof(ProtocolLimits),
                typeof(CancellationToken),
            ]));
        Assert.NotNull(typeof(RfbSessionInitializer).GetMethod(
            nameof(RfbSessionInitializer.InitializeAsync),
            [
                typeof(Stream),
                typeof(RfbHandshakeResult),
                typeof(ProtocolLimits),
                typeof(RfbSessionDeclaration),
                typeof(ArdSessionEncryption),
                typeof(CancellationToken),
            ]));
    }

    [Fact]
    public async Task Initialize_writes_shared_init_pixel_format_and_encodings_in_order()
    {
        var input = ServerInit(640, 480, PixelFormat.WinArdBgra32, "Studio Mac");
        await using var stream = new ScriptedDuplexStream(input);

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_8),
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(640, server.Width);
        Assert.Equal(480, server.Height);
        Assert.Equal("Studio Mac", server.Name);
        Assert.Equal(PixelFormat.WinArdBgra32, server.PixelFormat);
        Assert.Equal(ExpectedClientInitialization(), stream.WrittenBytes);
        Assert.Equal([1, 20, 28], stream.WriteLengths);
        Assert.Equal(0, stream.FlushCount);
        Assert.False(stream.WasDisposed);
    }

    [Theory]
    [MemberData(nameof(StandardVersions))]
    public async Task Explicit_standard_versions_preserve_the_exact_standard_initialization(RfbVersion version)
    {
        var input = ServerInit(640, 480, PixelFormat.WinArdBgra32, "Studio Mac");
        await using var stream = new ScriptedDuplexStream(input);

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(version),
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(640, server.Width);
        Assert.Equal(480, server.Height);
        Assert.Equal("Studio Mac", server.Name);
        Assert.Null(server.ArdCapabilities);
        Assert.Equal(ExpectedClientInitialization(), stream.WrittenBytes);
        Assert.Equal([1, 20, 28], stream.WriteLengths);
        Assert.Equal(0, stream.FlushCount);
        Assert.False(stream.WasDisposed);
    }

    [Fact]
    public async Task Non_ARD_security_handshake_is_rejected_before_io()
    {
        await using var stream = new ScriptedDuplexStream([]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                new RfbHandshakeResult(RfbVersion.V3_8, RfbSecurityType.None),
                ProtocolLimits.Default,
                CancellationToken.None));

        Assert.Equal(0, stream.ReadPosition);
        Assert.Empty(stream.WrittenBytes);
    }

    [Fact]
    public async Task Explicit_initializer_validates_nulls_and_pre_cancellation_before_io()
    {
        await using var stream = new ScriptedDuplexStream([]);
        await using var compatibilityStream = new ScriptedDuplexStream([]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            RfbSessionInitializer.InitializeAsync(null!, Handshake(RfbVersion.V3_8), ProtocolLimits.Default, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            RfbSessionInitializer.InitializeAsync(stream, null!, ProtocolLimits.Default, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            RfbSessionInitializer.InitializeAsync(stream, Handshake(RfbVersion.V3_8), null!, CancellationToken.None));
        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(
                compatibilityStream,
                Handshake(RfbVersion.V3_8),
                ProtocolLimits.Default,
                null,
                CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RfbSessionInitializer.InitializeAsync(stream, Handshake(RfbVersion.V3_889), ProtocolLimits.Default, cancellation.Token));

        Assert.Equal(0, stream.ReadPosition);
        Assert.Empty(stream.WrittenBytes);
        Assert.Equal([1], compatibilityStream.WrittenBytes);
    }

    [Fact]
    public async Task Ard_non_session_initialization_parses_capabilities_and_writes_exact_control_sequence()
    {
        var bitmap = Enumerable.Range(0, 16).Select(index => (byte)(index + 1)).ToArray();
        var nameField = ExtendedNameField((uint)ArdServerFlags.MayControl, bitmap, "Studio Mac");
        await using var stream = new ScriptedDuplexStream(
            ServerInit(1440, 900, PixelFormat.WinArdBgra32, nameField));

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_889),
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal((1440, 900, "Studio Mac"), (server.Width, server.Height, server.Name));
        Assert.NotNull(server.ArdCapabilities);
        Assert.Equal((uint)ArdServerFlags.MayControl, server.ArdCapabilities.RawFlags);
        Assert.Equal(bitmap, server.ArdCapabilities.CommandBitmap.ToArray());
        Assert.Equal(
            [ClientInitArd(), ViewerInfoMessage(), SetModeSharedMessage(), SetDisplayAllMessage(), SetPixelFormatMessage(), ArdSetEncodingsMessage()],
            stream.Writes);
        Assert.Equal([1, 66, 4, 8, 20, 36], stream.WriteLengths);
        Assert.Equal(0, stream.FlushCount);
        Assert.False(stream.WasDisposed);
    }

    [Fact]
    public async Task Ard_rejects_metadata_expansion_above_encoding_limit_before_io()
    {
        var declaration = new RfbSessionDeclaration(
            PixelFormat.WinArdBgra32,
            new int[ushort.MaxValue]);
        await using var stream = new ScriptedDuplexStream([]);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_889),
                declaration,
                ProtocolLimits.Default,
                CancellationToken.None));

        Assert.Equal("encodings", exception.ParamName);
        Assert.Empty(stream.WrittenBytes);
        Assert.Equal(0, stream.ReadPosition);
    }

    [Fact]
    public async Task Ard_rejects_encryption_expansion_above_encoding_limit_before_io()
    {
        var declaration = new RfbSessionDeclaration(
            PixelFormat.WinArdBgra32,
            new int[ushort.MaxValue - 3]);
        await using var inner = new ScriptedDuplexStream([]);
        await using var transport = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        await using var encryption = new ArdSessionEncryption(
            transport,
            new ArdAuthenticationResult(new byte[16]));

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            RfbSessionInitializer.InitializeAsync(
                transport,
                Handshake(RfbVersion.V3_889),
                ProtocolLimits.Default,
                declaration,
                encryption,
                CancellationToken.None));

        Assert.Equal("encodings", exception.ParamName);
        Assert.Empty(inner.WrittenBytes);
        Assert.Equal(0, inner.ReadPosition);
    }

    [Fact]
    public async Task Session_declaration_is_preserved_before_ard_metadata_encodings()
    {
        var declaration = new RfbSessionDeclaration(
            PixelFormat.WinArdRgb565,
            [6, 16, 0, 1, -239, -223]);
        var nameField = ExtendedNameField(
            (uint)ArdServerFlags.MayControl,
            new byte[16],
            "Studio Mac");
        await using var stream = new ScriptedDuplexStream(
            ServerInit(1440, 900, PixelFormat.WinArdBgra32, nameField));

        _ = await RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_889),
            declaration,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(Convert.FromHexString("0000000010100001001F003F001F0B0500000000"), stream.Writes[^2]);
        Assert.Equal(
            SetEncodingsMessage(
                6,
                16,
                0,
                1,
                -239,
                -223,
                (int)RfbEncodingType.ArdDisplayInfo,
                (int)RfbEncodingType.ArdDisplayInfo2),
            stream.Writes[^1]);
    }

    [Fact]
    public async Task Ard_encryption_aware_initialization_advertises_1103_then_requests_encryption()
    {
        var nameField = ExtendedNameField(
            (uint)ArdServerFlags.MayControl,
            new byte[16],
            "Studio Mac");
        await using var inner = new ScriptedDuplexStream(
            ServerInit(1440, 900, PixelFormat.WinArdBgra32, nameField));
        await using var transport = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        await using var encryption = new ArdSessionEncryption(
            transport,
            new ArdAuthenticationResult(new byte[16]));

        _ = await RfbSessionInitializer.InitializeAsync(
            transport,
            Handshake(RfbVersion.V3_889),
            ProtocolLimits.Default,
            encryption,
            CancellationToken.None);

        Assert.Equal(
            [
                ClientInitArd(),
                ViewerInfoMessage(),
                SetModeSharedMessage(),
                SetDisplayAllMessage(),
                SetPixelFormatMessage(),
                EncryptedArdSetEncodingsMessage(),
                new byte[] { 0x12, 0, 0, 1, 0, 1, 0, 1, 0, 0, 0, 1 },
            ],
            inner.Writes);
        Assert.Equal(ArdSessionEncryptionState.Requested, encryption.State);
    }

    [Fact]
    public async Task Ard_plain_name_field_requires_extended_initialization_and_writes_only_client_init()
    {
        await using var stream = new ScriptedDuplexStream(
            ServerInit(640, 480, PixelFormat.WinArdBgra32, "ordinary name"));

        await Assert.ThrowsAsync<ArdExtendedInitializationRequiredException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_889),
                ProtocolLimits.Default,
                CancellationToken.None));

        Assert.Equal([ClientInitArd()], stream.Writes);
    }

    [Fact]
    public async Task Ard_server_without_control_permission_stops_after_client_init()
    {
        var nameField = ExtendedNameField((uint)ArdServerFlags.Observe, new byte[16], "private");
        await using var stream = new ScriptedDuplexStream(
            ServerInit(640, 480, PixelFormat.WinArdBgra32, nameField));

        var exception = await Assert.ThrowsAsync<ArdControlNotAllowedException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_889),
                ProtocolLimits.Default,
                CancellationToken.None));

        Assert.Equal((uint)ArdServerFlags.Observe, exception.RawServerFlags);
        Assert.Equal([ClientInitArd()], stream.Writes);
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(640, 0)]
    public async Task Ard_zero_dimensions_without_session_selection_fail_before_control_or_bootstrap(
        ushort width,
        ushort height)
    {
        var serverInit = ServerInit(
            width,
            height,
            PixelFormat.WinArdBgra32,
            ExtendedNameField((uint)ArdServerFlags.MayControl, new byte[16], "Studio"));
        var input = serverInit.Concat(FramebufferUpdate(DisplayInfoRectangle(1280, 720))).ToArray();
        await using var stream = new ScriptedDuplexStream(input);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_889),
                ProtocolLimits.Default,
                CancellationToken.None));

        Assert.Equal(serverInit.Length, stream.ReadPosition);
        Assert.Equal([ClientInitArd()], stream.Writes);
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(640, 0)]
    public async Task Ard_session_selection_rejects_half_zero_dimensions_before_selection_or_control(
        ushort width,
        ushort height)
    {
        var serverInit = ServerInit(
            width,
            height,
            PixelFormat.WinArdBgra32,
            ExtendedNameField(
                (uint)(ArdServerFlags.MayControl | ArdServerFlags.SessionSelect),
                new byte[16],
                "Studio"));
        var input = serverInit
            .Concat(SessionInfo(1u << 1, "alice"u8.ToArray()))
            .Concat(SessionResult(0))
            .Concat(FramebufferUpdate(DisplayInfoRectangle(1280, 720)))
            .ToArray();
        await using var stream = new ScriptedDuplexStream(input);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_889),
                ProtocolLimits.Default,
                CancellationToken.None));

        Assert.Equal(serverInit.Length, stream.ReadPosition);
        Assert.Equal([ClientInitArd()], stream.Writes);
    }

    [Fact]
    public async Task Ard_session_selection_finishes_before_control_messages_are_written()
    {
        var prefix = ServerInit(
            640,
            480,
            PixelFormat.WinArdBgra32,
            ExtendedNameField(
                (uint)(ArdServerFlags.MayControl | ArdServerFlags.SessionSelect),
                new byte[16],
                "Studio"))
            .Concat(SessionInfo(1u << 1, "alice"u8.ToArray()))
            .ToArray();
        await using var stream = new GatedSessionDuplexStream(prefix, SessionResult(0));

        var initialization = RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_889),
            ProtocolLimits.Default,
            CancellationToken.None);
        await stream.SessionCommandWritten.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([ClientInitArd(), SessionCommand("alice", command: 1)], stream.Writes);

        stream.ReleaseSessionResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [ClientInitArd(), SessionCommand("alice", command: 1), ViewerInfoMessage(), SetModeSharedMessage(), SetDisplayAllMessage(), SetPixelFormatMessage(), ArdSetEncodingsMessage()],
            stream.Writes);
    }

    [Fact]
    public async Task Ard_zero_size_session_bootstraps_with_minimal_encodings_then_restores_ard_encodings()
    {
        var serverInit = ServerInit(
            0,
            0,
            PixelFormat.WinArdBgra32,
            ExtendedNameField(
                (uint)(ArdServerFlags.MayControl | ArdServerFlags.SessionSelect),
                new byte[16],
                "Studio"));
        var sessionInfo = SessionInfo(1u << 1, "alice"u8.ToArray());
        var sessionResult = SessionResult(0);
        var bootstrap = FramebufferUpdate(DisplayInfoRectangle(1920, 1080));
        await using var stream = new ScriptedDuplexStream(
            [.. serverInit, .. sessionInfo, .. sessionResult, .. bootstrap]);

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_889),
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal((1920, 1080), (server.Width, server.Height));
        Assert.Equal(
            [ClientInitArd(), SessionCommand("alice", command: 1), ViewerInfoMessage(), SetModeSharedMessage(), SetDisplayAllMessage(), SetPixelFormatMessage(), BootstrapSetEncodingsMessage(), ArdSetEncodingsMessage()],
            stream.Writes);
        Assert.Equal([1, 74, 66, 4, 8, 20, 16, 36], stream.WriteLengths);
        Assert.All(stream.Writes, message => Assert.NotEqual(3, message[0]));
        Assert.Equal(serverInit.Length + sessionInfo.Length + sessionResult.Length + bootstrap.Length, stream.ReadPosition);
        Assert.Equal(0, stream.FlushCount);
        Assert.False(stream.WasDisposed);
    }

    [Fact]
    public async Task Ard_zero_size_session_declares_bootstrap_encodings_before_reading_bootstrap_data()
    {
        var prefix = ServerInit(
                0,
                0,
                PixelFormat.WinArdBgra32,
                ExtendedNameField(
                    (uint)(ArdServerFlags.MayControl | ArdServerFlags.SessionSelect),
                    new byte[16],
                    "Studio"))
            .Concat(SessionInfo(1u << 1, "alice"u8.ToArray()))
            .Concat(SessionResult(0))
            .ToArray();
        await using var stream = new GatedBootstrapDuplexStream(
            prefix,
            FramebufferUpdate(DisplayInfoRectangle(1920, 1080)));

        var initialization = RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_889),
            ProtocolLimits.Default,
            CancellationToken.None);
        await stream.BootstrapEncodingsWritten.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(initialization.IsCompleted);
        Assert.Equal(BootstrapSetEncodingsMessage(), stream.Writes[^1]);

        stream.ReleaseBootstrapData();
        var server = await initialization.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((1920, 1080), (server.Width, server.Height));
        Assert.Equal(ArdSetEncodingsMessage(), stream.Writes[^1]);
    }

    [Fact]
    public async Task Session_declaration_is_applied_only_after_ard_bootstrap_metadata()
    {
        var declaration = new RfbSessionDeclaration(PixelFormat.WinArdRgb565, [6, 16, 0]);
        var prefix = ServerInit(
                0,
                0,
                PixelFormat.WinArdBgra32,
                ExtendedNameField(
                    (uint)(ArdServerFlags.MayControl | ArdServerFlags.SessionSelect),
                    new byte[16],
                    "Studio"))
            .Concat(SessionInfo(1u << 1, "alice"u8.ToArray()))
            .Concat(SessionResult(0))
            .ToArray();
        await using var stream = new GatedBootstrapDuplexStream(
            prefix,
            FramebufferUpdate(DisplayInfoRectangle(1920, 1080)));

        var initialization = RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_889),
            declaration,
            ProtocolLimits.Default,
            CancellationToken.None);
        await stream.BootstrapEncodingsWritten.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BootstrapSetEncodingsMessage(), stream.Writes[^1]);

        stream.ReleaseBootstrapData();
        _ = await initialization.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            SetEncodingsMessage(
                6,
                16,
                0,
                (int)RfbEncodingType.ArdDisplayInfo,
                (int)RfbEncodingType.ArdDisplayInfo2,
                (int)RfbEncodingType.DesktopSize),
            stream.Writes[^1]);
    }

    [Fact]
    public async Task Ard_nonzero_session_does_not_read_bootstrap_or_declare_bootstrap_encodings()
    {
        var serverInit = ServerInit(
            800,
            600,
            PixelFormat.WinArdBgra32,
            ExtendedNameField(
                (uint)(ArdServerFlags.MayControl | ArdServerFlags.SessionSelect),
                new byte[16],
                "Studio"));
        var sessionInfo = SessionInfo(1u << 1, "alice"u8.ToArray());
        var sessionResult = SessionResult(0);
        byte[] unreadBootstrap = [0xDE, 0xAD, 0xBE, 0xEF];
        await using var stream = new ScriptedDuplexStream(
            [.. serverInit, .. sessionInfo, .. sessionResult, .. unreadBootstrap]);

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_889),
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal((800, 600), (server.Width, server.Height));
        Assert.Equal(serverInit.Length + sessionInfo.Length + sessionResult.Length, stream.ReadPosition);
        Assert.Equal(ArdSetEncodingsMessage(), stream.Writes[^1]);
        Assert.DoesNotContain(stream.Writes, message => message.SequenceEqual(BootstrapSetEncodingsMessage()));
    }

    [Fact]
    public async Task Ard_display_name_uses_only_the_parser_tail_and_cleans_controls()
    {
        var bitmap = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        var nameField = ExtendedNameField(
            (uint)ArdServerFlags.MayControl,
            bitmap,
            "Studio\r\nMac");
        await using var stream = new ScriptedDuplexStream(
            ServerInit(640, 480, PixelFormat.WinArdBgra32, nameField));

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_889),
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal("Studio  Mac", server.Name);
        Assert.DoesNotContain('�', server.Name);
        Assert.DoesNotContain('\0', server.Name);
    }

    [Fact]
    public async Task Ard_oversized_name_is_rejected_before_payload_read()
    {
        var header = ServerInitHeader(1, 1, PixelFormat.WinArdBgra32, 1025);
        await using var stream = new ScriptedDuplexStream(header);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_889),
                new ProtocolLimits(1024, 1024),
                CancellationToken.None));

        Assert.Equal(header.Length, stream.ReadPosition);
        Assert.Equal([ClientInitArd()], stream.Writes);
    }

    [Fact]
    public async Task Ard_truncated_server_init_is_protocol_failure_and_preserves_stream_ownership()
    {
        await using var stream = new ScriptedDuplexStream([0, 1, 0]);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_889),
                ProtocolLimits.Default,
                CancellationToken.None));

        Assert.Equal([ClientInitArd()], stream.Writes);
        Assert.Equal(0, stream.FlushCount);
        Assert.False(stream.WasDisposed);
    }

    [Fact]
    public async Task Ard_initializer_propagates_asynchronous_cancellation()
    {
        await using var stream = new BlockingDuplexStream();
        using var cancellation = new CancellationTokenSource();

        var task = RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_889),
            ProtocolLimits.Default,
            cancellation.Token);
        await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, stream.ReceivedCancellationToken);
    }

    [Fact]
    public async Task Set_encodings_writer_preserves_signed_ids_and_order()
    {
        await using var stream = new MemoryStream();

        await RfbSessionInitializer.WriteSetEncodingsAsync(
            stream,
            [16, -223, 1103, int.MinValue],
            CancellationToken.None);

        Assert.Equal(
            Convert.FromHexString("0200000400000010FFFFFF210000044F80000000"),
            stream.ToArray());
    }

    [Fact]
    public async Task Set_encodings_writer_allows_empty_declaration()
    {
        await using var stream = new MemoryStream();

        await RfbSessionInitializer.WriteSetEncodingsAsync(
            stream,
            Array.Empty<int>(),
            CancellationToken.None);

        Assert.Equal(new byte[] { 2, 0, 0, 0 }, stream.ToArray());
    }

    [Fact]
    public async Task Set_encodings_writer_rejects_too_many_entries_before_writing()
    {
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            RfbSessionInitializer.WriteSetEncodingsAsync(
                stream,
                new int[ushort.MaxValue + 1],
                CancellationToken.None).AsTask());

        Assert.Equal("encodings", exception.ParamName);
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task Set_encodings_writer_rejects_null_entries_before_writing()
    {
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            RfbSessionInitializer.WriteSetEncodingsAsync(
                stream,
                null!,
                CancellationToken.None).AsTask());

        Assert.Equal("encodings", exception.ParamName);
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task Set_encodings_writer_honors_pre_cancellation_before_writing()
    {
        await using var stream = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RfbSessionInitializer.WriteSetEncodingsAsync(
                stream,
                [16],
                cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task Update_request_uses_exact_big_endian_wire_format()
    {
        await using var stream = new ScriptedDuplexStream([]);

        await RfbSessionInitializer.WriteFramebufferUpdateRequestAsync(
            stream,
            incremental: false,
            x: 1,
            y: 2,
            width: 0x1234,
            height: 0x5678,
            CancellationToken.None);

        Assert.Equal([3, 0, 0, 1, 0, 2, 0x12, 0x34, 0x56, 0x78], stream.WrittenBytes);
        Assert.Equal([10], stream.WriteLengths);
        Assert.Equal(0, stream.FlushCount);
        Assert.False(stream.WasDisposed);
    }

    [Fact]
    public async Task Name_uses_replacement_cleans_controls_and_truncates_display()
    {
        var name = new byte[5000];
        Array.Fill(name, (byte)'a');
        name[0] = 0xFF;
        name[1] = (byte)'\r';
        name[2] = (byte)'\n';
        name[3] = 0;
        await using var stream = new ScriptedDuplexStream(ServerInit(1, 1, PixelFormat.WinArdBgra32, name));

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_8),
            new ProtocolLimits(6000, 1024),
            CancellationToken.None);

        Assert.Equal(4096, server.Name.Length);
        Assert.StartsWith("�   ", server.Name, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', server.Name);
        Assert.DoesNotContain('\n', server.Name);
        Assert.True(server.IsNameTruncated);
    }

    [Fact]
    public async Task Name_cleans_unicode_line_separators()
    {
        await using var stream = new ScriptedDuplexStream(
            ServerInit(1, 1, PixelFormat.WinArdBgra32, "one\u2028two\u2029three"));

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_8),
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal("one two three", server.Name);
    }

    [Fact]
    public async Task Name_truncation_does_not_split_surrogate_pair()
    {
        var name = new string('a', 4095) + "😀tail";
        await using var stream = new ScriptedDuplexStream(
            ServerInit(1, 1, PixelFormat.WinArdBgra32, name));

        var server = await RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_8),
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(4095, server.Name.Length);
        Assert.False(char.IsSurrogate(server.Name[^1]));
        Assert.True(server.IsNameTruncated);
    }

    [Fact]
    public async Task Oversized_name_is_rejected_before_payload_read()
    {
        var header = ServerInitHeader(1, 1, PixelFormat.WinArdBgra32, 1025);
        await using var stream = new ScriptedDuplexStream(header);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_8),
                new ProtocolLimits(1024, 1024),
                CancellationToken.None));

        Assert.Contains("1025", exception.Message, StringComparison.Ordinal);
        Assert.Equal(header.Length, stream.ReadPosition);
    }

    [Fact]
    public async Task Truncated_server_init_is_wrapped_as_protocol_failure()
    {
        await using var stream = new ScriptedDuplexStream([0, 1, 0]);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_8),
                ProtocolLimits.Default,
                CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task Zero_server_dimensions_are_rejected(ushort width, ushort height)
    {
        await using var stream = new ScriptedDuplexStream(ServerInit(width, height, PixelFormat.WinArdBgra32, "x"));

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_8),
                ProtocolLimits.Default,
                CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(InvalidPixelFormats))]
    public async Task Invalid_server_pixel_format_is_rejected(byte[] pixelFormat)
    {
        await using var stream = new ScriptedDuplexStream(ServerInit(1, 1, pixelFormat, "x"));

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbSessionInitializer.InitializeAsync(
                stream,
                Handshake(RfbVersion.V3_8),
                ProtocolLimits.Default,
                CancellationToken.None));
    }

    [Fact]
    public async Task Initialize_propagates_asynchronous_cancellation()
    {
        await using var stream = new BlockingDuplexStream();
        using var cancellation = new CancellationTokenSource();

        var task = RfbSessionInitializer.InitializeAsync(
            stream,
            Handshake(RfbVersion.V3_8),
            ProtocolLimits.Default,
            cancellation.Token);
        await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, stream.ReceivedCancellationToken);
    }

    public static TheoryData<RfbVersion> StandardVersions => new()
    {
        RfbVersion.V3_3,
        RfbVersion.V3_7,
        RfbVersion.V3_8,
    };

    public static TheoryData<byte[]> InvalidPixelFormats => new()
    {
        PixelFormatBytes(24, 24, 0, 1, 255, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 0, 0, 1, 255, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 24, 2, 1, 255, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 24, 0, 0, 255, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 24, 0, 1, 0, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 24, 0, 1, 250, 255, 255, 16, 8, 0),
        PixelFormatBytes(32, 24, 0, 1, 255, 255, 255, 28, 8, 0),
        PixelFormatBytes(32, 24, 0, 1, 255, 255, 255, 8, 8, 0),
    };

    private static RfbHandshakeResult Handshake(RfbVersion version) =>
        new(version, RfbSecurityType.AppleRemoteDesktop);

    private static byte[] ClientInitArd() => [(byte)ArdClientInitFlags.Ard];

    private static byte[] ViewerInfoMessage()
    {
        var message = new byte[66];
        message[0] = 0x21;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 62);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(10), 6);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(18), 0);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(22), 15);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(26), 0);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(30), 0);
        message[34] = 0xB0;
        message[36] = 0x0C;
        message[37] = 0x03;
        message[38] = 0x90;
        message[44] = 0x40;
        return message;
    }

    private static byte[] SetModeSharedMessage() => [0x0A, 0, 0, 1];

    private static byte[] SetDisplayAllMessage() => [0x0D, 1, 0, 0, 0, 0, 0, 0];

    private static byte[] SetPixelFormatMessage()
    {
        var message = new byte[20];
        PixelFormat.WinArdBgra32.ToWireBytes().CopyTo(message, 4);
        return message;
    }

    private static byte[] StandardSetEncodingsMessage() =>
        SetEncodingsMessage(
            (int)RfbEncodingType.Zlib,
            (int)RfbEncodingType.Zrle,
            (int)RfbEncodingType.Raw,
            (int)RfbEncodingType.CopyRect,
            (int)RfbEncodingType.Cursor,
            (int)RfbEncodingType.DesktopSize);

    private static byte[] ArdSetEncodingsMessage() =>
        SetEncodingsMessage(
            (int)RfbEncodingType.Zlib,
            (int)RfbEncodingType.Zrle,
            (int)RfbEncodingType.Raw,
            (int)RfbEncodingType.CopyRect,
            (int)RfbEncodingType.Cursor,
            (int)RfbEncodingType.DesktopSize,
            (int)RfbEncodingType.ArdDisplayInfo,
            (int)RfbEncodingType.ArdDisplayInfo2);

    private static byte[] EncryptedArdSetEncodingsMessage() =>
        SetEncodingsMessage(
            (int)RfbEncodingType.Zlib,
            (int)RfbEncodingType.Zrle,
            (int)RfbEncodingType.Raw,
            (int)RfbEncodingType.CopyRect,
            (int)RfbEncodingType.Cursor,
            (int)RfbEncodingType.DesktopSize,
            (int)RfbEncodingType.ArdDisplayInfo,
            (int)RfbEncodingType.ArdDisplayInfo2,
            (int)RfbEncodingType.ArdSessionEncryption);

    private static byte[] BootstrapSetEncodingsMessage() =>
        SetEncodingsMessage(
            (int)RfbEncodingType.ArdDisplayInfo,
            (int)RfbEncodingType.ArdDisplayInfo2,
            (int)RfbEncodingType.DesktopSize);

    private static byte[] SetEncodingsMessage(params int[] encodings)
    {
        var message = new byte[4 + (encodings.Length * sizeof(int))];
        message[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), checked((ushort)encodings.Length));
        for (var index = 0; index < encodings.Length; index++)
        {
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4 + (index * sizeof(int))), encodings[index]);
        }

        return message;
    }

    private sealed class MisreportedEncodingList(int reportedCount, int actualCount) : IReadOnlyList<int>
    {
        public int Count => reportedCount;

        public int this[int index] => 0;

        public IEnumerator<int> GetEnumerator() => Enumerable.Repeat(0, actualCount).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static byte[] ExtendedNameField(uint flags, byte[] bitmap, string displayName)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(bitmap.Length, 16);
        var field = new byte[23 + Encoding.UTF8.GetByteCount(displayName)];
        BinaryPrimitives.WriteUInt32BigEndian(field.AsSpan(2), flags);
        bitmap.CopyTo(field, 6);
        Encoding.UTF8.GetBytes(displayName).CopyTo(field, 23);
        return field;
    }

    private static byte[] SessionInfo(uint allowedCommands, byte[] username)
    {
        var body = new byte[11 + username.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, 1);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(2), allowedCommands);
        username.CopyTo(body, 10);
        return SizedBody(body);
    }

    private static byte[] SessionResult(uint status)
    {
        var body = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(body, 1);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(2), status);
        return SizedBody(body);
    }

    private static byte[] SizedBody(byte[] body)
    {
        var message = new byte[2 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(message, checked((ushort)body.Length));
        body.CopyTo(message, 2);
        return message;
    }

    private static byte[] SessionCommand(string username, byte command)
    {
        var message = new byte[74];
        BinaryPrimitives.WriteUInt16BigEndian(message, 72);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 1);
        message[8] = command;
        Encoding.UTF8.GetBytes(username).CopyTo(message, 10);
        return message;
    }

    private static byte[] FramebufferUpdate(params byte[][] rectangles)
    {
        var message = new List<byte> { 0, 0 };
        message.Add((byte)(rectangles.Length >> 8));
        message.Add((byte)rectangles.Length);
        foreach (var rectangle in rectangles)
        {
            message.AddRange(rectangle);
        }

        return message.ToArray();
    }

    private static byte[] DisplayInfoRectangle(ushort width, ushort height)
    {
        var rectangle = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(rectangle.AsSpan(8), (int)RfbEncodingType.ArdDisplayInfo);
        BinaryPrimitives.WriteUInt16BigEndian(rectangle.AsSpan(12), width);
        BinaryPrimitives.WriteUInt16BigEndian(rectangle.AsSpan(14), height);
        return rectangle;
    }

    private static byte[] ExpectedClientInitialization() =>
    [
        1,
        0, 0, 0, 0,
        32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0,
        2, 0, 0, 6,
        0, 0, 0, 6,
        0, 0, 0, 16,
        0, 0, 0, 0,
        0, 0, 0, 1,
        0xFF, 0xFF, 0xFF, 0x11,
        0xFF, 0xFF, 0xFF, 0x21,
    ];

    private static byte[] ServerInit(ushort width, ushort height, PixelFormat format, string name) =>
        ServerInit(width, height, format.ToWireBytes(), Encoding.UTF8.GetBytes(name));

    private static byte[] ServerInit(ushort width, ushort height, PixelFormat format, byte[] name) =>
        ServerInit(width, height, format.ToWireBytes(), name);

    private static byte[] ServerInit(ushort width, ushort height, byte[] format, string name) =>
        ServerInit(width, height, format, Encoding.UTF8.GetBytes(name));

    private static byte[] ServerInit(ushort width, ushort height, byte[] format, byte[] name)
    {
        var header = ServerInitHeader(width, height, format, checked((uint)name.Length));
        return [.. header, .. name];
    }

    private static byte[] ServerInitHeader(ushort width, ushort height, PixelFormat format, uint nameLength) =>
        ServerInitHeader(width, height, format.ToWireBytes(), nameLength);

    private static byte[] ServerInitHeader(ushort width, ushort height, byte[] format, uint nameLength)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), height);
        format.CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), nameLength);
        return bytes;
    }

    private static byte[] PixelFormatBytes(
        byte bitsPerPixel,
        byte depth,
        byte bigEndian,
        byte trueColor,
        ushort redMax,
        ushort greenMax,
        ushort blueMax,
        byte redShift,
        byte greenShift,
        byte blueShift)
    {
        var bytes = new byte[16];
        bytes[0] = bitsPerPixel;
        bytes[1] = depth;
        bytes[2] = bigEndian;
        bytes[3] = trueColor;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), redMax);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), greenMax);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), blueMax);
        bytes[10] = redShift;
        bytes[11] = greenShift;
        bytes[12] = blueShift;
        return bytes;
    }

    private sealed class ScriptedDuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly MemoryStream _output = new();

        public byte[] WrittenBytes => _output.ToArray();
        public List<int> WriteLengths { get; } = [];
        public List<byte[]> Writes { get; } = [];
        public long ReadPosition => _input.Position;
        public int FlushCount { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => FlushCount++;
        public override Task FlushAsync(CancellationToken cancellationToken) { FlushCount++; return Task.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteLengths.Add(buffer.Length);
            Writes.Add(buffer.ToArray());
            return _output.WriteAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            _input.Dispose();
            _output.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class GatedSessionDuplexStream(byte[] prefix, byte[] suffix) : Stream
    {
        private readonly MemoryStream _prefix = new(prefix, writable: false);
        private readonly MemoryStream _suffix = new(suffix, writable: false);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sessionCommandWritten =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> Writes { get; } = [];
        public Task SessionCommandWritten => _sessionCommandWritten.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_prefix.Position < _prefix.Length)
            {
                return await _prefix.ReadAsync(buffer, cancellationToken);
            }

            await _release.Task.WaitAsync(cancellationToken);
            return await _suffix.ReadAsync(buffer, cancellationToken);
        }

        public void ReleaseSessionResult() => _release.TrySetResult();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(buffer.ToArray());
            if (Writes.Count == 2)
            {
                _sessionCommandWritten.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _prefix.Dispose();
                _suffix.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class GatedBootstrapDuplexStream(byte[] prefix, byte[] bootstrapData) : Stream
    {
        private readonly MemoryStream _prefix = new(prefix, writable: false);
        private readonly MemoryStream _bootstrapData = new(bootstrapData, writable: false);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _bootstrapEncodingsWritten =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> Writes { get; } = [];
        public Task BootstrapEncodingsWritten => _bootstrapEncodingsWritten.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_prefix.Position < _prefix.Length)
            {
                return await _prefix.ReadAsync(buffer, cancellationToken);
            }

            await _release.Task.WaitAsync(cancellationToken);
            return await _bootstrapData.ReadAsync(buffer, cancellationToken);
        }

        public void ReleaseBootstrapData() => _release.TrySetResult();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(buffer.ToArray());
            if (Writes.Count == 7)
            {
                _bootstrapEncodingsWritten.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _prefix.Dispose();
                _bootstrapData.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class BlockingDuplexStream : Stream
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;
        public CancellationToken ReceivedCancellationToken { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReceivedCancellationToken = cancellationToken;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
