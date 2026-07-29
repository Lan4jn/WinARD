using System.Buffers;
using System.Globalization;
using System.Runtime.ExceptionServices;
using WinARD.Application.Ports;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Clipboard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.Initialization;
using WinARD.Remote.Protocol.Input;
using WinARD.Remote.Protocol.IO;
using WinARD.Infrastructure.Diagnostics;

namespace WinARD.Desktop.Services;

public sealed class RfbClientFactory(ISafeDiagnosticSink? diagnosticSink = null) : IRfbClientFactory
{
    public IRfbClient Create(Stream stream) => new RfbClient(
        stream,
        diagnosticSink: diagnosticSink,
        requireArdAuthentication: true);
}

internal sealed class RfbClient : IRfbClient
{
    private readonly ArdEncryptedStream _transport;
    private readonly FramebufferSnapshotFactory _snapshotFactory;
    private readonly ISafeDiagnosticSink? _diagnosticSink;
    private readonly bool _requireArdAuthentication;
    private RfbHandshakeResult? _handshake;
    private RfbServerInit? _serverInit;
    private Framebuffer? _framebuffer;
    private FramebufferUpdateSession? _framebufferUpdates;
    private ArdAuthenticationResult? _authenticationResult;
    private ArdSessionEncryption? _sessionEncryption;
    private bool _disposed;

    public RfbClient(
        Stream stream,
        FramebufferSnapshotFactory? snapshotFactory = null,
        ISafeDiagnosticSink? diagnosticSink = null,
        ArdAuthenticationResult? authenticationResult = null,
        bool requireArdAuthentication = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _transport = new ArdEncryptedStream(stream, ProtocolLimits.Default);
        _snapshotFactory = snapshotFactory ?? new FramebufferSnapshotFactory();
        _diagnosticSink = diagnosticSink;
        _authenticationResult = authenticationResult;
        _requireArdAuthentication = requireArdAuthentication;
    }

    public RemoteFramebufferSize FramebufferSize
    {
        get
        {
            ThrowIfDisposed();
            var framebuffer = _framebuffer ?? throw new InvalidOperationException("RFB initialization has not completed.");
            return new RemoteFramebufferSize(framebuffer.Width, framebuffer.Height);
        }
    }

    public async Task NegotiateAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _handshake = await RfbHandshake.NegotiateAsync(_transport, cancellationToken).ConfigureAwait(false);
    }

    public async Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(secret);
        var handshake = _handshake ?? throw new InvalidOperationException("RFB negotiation has not completed.");
        if (_authenticationResult is not null || _sessionEncryption is not null)
        {
            throw new InvalidOperationException("RFB authentication has already completed.");
        }

        using var usernameMaterial = SecretMaterial.FromUtf8(username);
        using var passwordMaterial = new ApplicationSecretMaterial(secret);
        _authenticationResult = await new ArdAuthenticator().AuthenticateAsync(
            _transport,
            handshake.Version,
            usernameMaterial,
            passwordMaterial,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var handshake = _handshake ?? throw new InvalidOperationException("RFB negotiation has not completed.");
        if (handshake.Version == RfbVersion.V3_889 &&
            _authenticationResult is null &&
            _requireArdAuthentication)
        {
            throw RfbProtocolException.Create(
                "ARD 003.889 initialization requires authenticated session encryption material.",
                new RfbProtocolFailureInfo(RfbProtocolFailureKind.ArdEncryptionNegotiation));
        }

        if (handshake.Version == RfbVersion.V3_889 && _authenticationResult is not null)
        {
            _sessionEncryption = new ArdSessionEncryption(_transport, _authenticationResult);
            _authenticationResult = null;
            _serverInit = await RfbSessionInitializer.InitializeAsync(
                _transport,
                handshake,
                ProtocolLimits.Default,
                _sessionEncryption,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _serverInit = await RfbSessionInitializer.InitializeAsync(
                _transport,
                handshake,
                ProtocolLimits.Default,
                cancellationToken).ConfigureAwait(false);
        }

        _framebuffer = new Framebuffer(
            _serverInit.Width,
            _serverInit.Height,
            ProtocolLimits.Default);
        _framebufferUpdates = _sessionEncryption is null
            ? FramebufferUpdateReader.CreateSession(_framebuffer, PixelFormat.WinArdBgra32)
            : FramebufferUpdateReader.CreateSession(
                _framebuffer,
                PixelFormat.WinArdBgra32,
                _sessionEncryption.CreateDecoder());
        WriteNegotiationDiagnostic(handshake, _serverInit);
    }

    public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var framebuffer = _framebuffer ?? throw new InvalidOperationException("RFB initialization has not completed.");
        return new ValueTask(RfbSessionInitializer.WriteFramebufferUpdateRequestAsync(
            _transport,
            incremental,
            0,
            0,
            checked((ushort)framebuffer.Width),
            checked((ushort)framebuffer.Height),
            cancellationToken));
    }

    public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var framebuffer = _framebuffer ?? throw new InvalidOperationException("RFB initialization has not completed.");
        var updates = _framebufferUpdates ?? throw new InvalidOperationException("RFB initialization has not completed.");
        var handshake = _handshake ?? throw new InvalidOperationException("RFB negotiation has not completed.");
        var reader = new RfbReader(_transport, ProtocolLimits.Default);
        while (true)
        {
            byte type;
            try
            {
                type = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RfbProtocolException exception)
            {
                throw exception.WithContext(new RfbProtocolFailureInfo(
                    RfbProtocolFailureKind.UnexpectedServerMessage,
                    RfbProtocolReadStage.ServerMessageType));
            }

            switch (type)
            {
                case 0:
                    try
                    {
                        var update = await updates.ApplyBodyAsync(
                                _transport,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (_sessionEncryption is not null)
                        {
                            await _sessionEncryption.CompleteFramebufferUpdateAsync(cancellationToken)
                                .ConfigureAwait(false);
                        }

                        return _snapshotFactory.CreateServerMessage(framebuffer, update);
                    }
                    catch (RfbProtocolException exception)
                    {
                        throw exception.WithContext(new RfbProtocolFailureInfo(
                            RfbProtocolFailureKind.MalformedFramebufferUpdate,
                            ServerMessageType: 0));
                    }
                case 2:
                    return new RemoteBellMessage();
                case 3:
                    try
                    {
                        return new RemoteClipboardMessage(
                            await ClipboardProtocol.ReadServerCutTextBodyAsync(
                                reader,
                                cancellationToken).ConfigureAwait(false));
                    }
                    catch (RfbProtocolException exception)
                    {
                        throw exception.WithContext(new RfbProtocolFailureInfo(
                            RfbProtocolFailureKind.MalformedClipboard,
                            ServerMessageType: 3));
                    }
                case ArdServerMessage.StateChangeType when handshake.Version == RfbVersion.V3_889:
                    var stateChange = await ArdStateChangeReader.ReadBodyAsync(
                            reader,
                            ProtocolLimits.Default,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (stateChange.Status == (ushort)ArdStateChangeStatus.LocalUserClosed)
                    {
                        WriteArdStateChangeDiagnostic(stateChange, "RemoteSessionClosed");
                        throw RfbProtocolException.Create(
                            "The remote ARD session was closed.",
                            new RfbProtocolFailureInfo(
                                RfbProtocolFailureKind.RemoteSessionClosed,
                                RfbProtocolReadStage.ArdStateChangePayload,
                                ArdServerMessage.StateChangeType));
                    }

                    if (stateChange.Status == (ushort)ArdStateChangeStatus.Tickle)
                    {
                        try
                        {
                            await new ArdClientMessageWriter(new RfbWriter(_transport))
                                .WriteAutoFramebufferUpdateAsync(
                                    checked((ushort)framebuffer.Width),
                                    checked((ushort)framebuffer.Height),
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            WriteArdStateChangeDiagnostic(stateChange, "AutoFBUpdateFailed", exception);
                            throw;
                        }

                        WriteArdStateChangeDiagnostic(stateChange, "AutoFBUpdateSent");
                    }
                    else
                    {
                        var action = stateChange.Status is
                            (ushort)ArdStateChangeStatus.PasteboardChanged or
                            (ushort)ArdStateChangeStatus.PasteboardDataNeeded or
                            (ushort)ArdStateChangeStatus.Sleep or
                            (ushort)ArdStateChangeStatus.Wake or
                            (ushort)ArdStateChangeStatus.CursorHidden or
                            (ushort)ArdStateChangeStatus.CursorVisible
                                ? "Consumed"
                                : "UnknownConsumed";
                        WriteArdStateChangeDiagnostic(stateChange, action);
                    }

                    continue;
                case var ardControlMessage
                    when handshake.Version == RfbVersion.V3_889 &&
                         ArdServerMessage.IsZeroPayloadControl(ardControlMessage):
                    continue;
                default:
                    throw RfbProtocolException.Create(
                        $"Unsupported RFB server message type {type}.",
                        new RfbProtocolFailureInfo(
                            RfbProtocolFailureKind.UnexpectedServerMessage,
                            RfbProtocolReadStage.ServerMessageType,
                            type));
            }
        }
    }

    public ValueTask SendPointerAsync(
        byte buttons,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return new PointerEventWriter(new RfbWriter(_transport)).WriteAsync(
            buttons,
            x,
            y,
            cancellationToken);
    }

    public ValueTask SendKeyAsync(
        uint keysym,
        bool down,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return new KeyEventWriter(new RfbWriter(_transport)).WriteAsync(
            down,
            keysym,
            cancellationToken);
    }

    public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return new ClipboardProtocol(new RfbWriter(_transport)).WriteClientCutTextAsync(
            text,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Exception? failure = null;
        try
        {
            if (_framebufferUpdates is not null)
            {
                await _framebufferUpdates.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            if (_sessionEncryption is not null)
            {
                await _sessionEncryption.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (failure is not null)
        {
            _ = exception;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        _authenticationResult?.Dispose();
        _authenticationResult = null;
        _framebuffer?.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void WriteNegotiationDiagnostic(RfbHandshakeResult handshake, RfbServerInit serverInit)
    {
        var ard = serverInit.ArdCapabilities;
        var isArd = handshake.Version == RfbVersion.V3_889;
        _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
            "RFB_SESSION_NEGOTIATED",
            Guid.NewGuid().ToString("N"),
            "RFB session initialization completed.",
            [
                new("ProtocolVersion", $"{handshake.Version.Major:D3}.{handshake.Version.Minor:D3}"),
                new("ClientInit", isArd ? "0xC1" : "0x01"),
                new("ServerFlags", ard is null
                    ? "NotApplicable"
                    : $"0x{ard.RawFlags.ToString("X8", CultureInfo.InvariantCulture)}"),
                new("MayControl", ard is null ? "NotApplicable" : ard.MayControl.ToString()),
                new("SessionSelectRequired", (ard?.RequiresSessionSelection ?? false).ToString()),
                new("SessionSelectCompleted", (ard?.RequiresSessionSelection ?? false).ToString()),
                new("RequestedMode", isArd ? "Shared" : "StandardShared"),
                new("FinalState", isArd ? "SharedControlNegotiated" : "Initialized"),
            ]));
    }

    private void WriteArdStateChangeDiagnostic(
        ArdStateChange stateChange,
        string action,
        Exception? exception = null)
    {
        _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
            "ARD_STATE_CHANGE",
            Guid.NewGuid().ToString("N"),
            "ARD StateChange message processed.",
            [
                new("Status", stateChange.Status.ToString(CultureInfo.InvariantCulture)),
                new("Flags", $"0x{stateChange.Flags.ToString("X4", CultureInfo.InvariantCulture)}"),
                new("PayloadSize", stateChange.PayloadSize.ToString(CultureInfo.InvariantCulture)),
                new("Action", action),
            ],
            exception));
    }

    private sealed class ApplicationSecretMaterial(ISecret secret) : ISecretMaterial
    {
        private ISecret? _secret = secret.Clone();

        public int Length => Secret.Length;

        public void CopyTo(Span<byte> destination) => Secret.CopyTo(destination);

        public void Dispose() => Interlocked.Exchange(ref _secret, null)?.Dispose();

        private ISecret Secret => _secret ?? throw new ObjectDisposedException(nameof(ApplicationSecretMaterial));
    }
}

internal sealed class FramebufferSnapshotFactory
{
    private readonly Func<int, IMemoryOwner<byte>> _rent;
    private readonly Action<int>? _copyObserver;

    public FramebufferSnapshotFactory(
        Func<int, IMemoryOwner<byte>>? rent = null,
        Action<int>? copyObserver = null)
    {
        _rent = rent ?? (length => MemoryPool<byte>.Shared.Rent(length));
        _copyObserver = copyObserver;
    }

    public RemoteFramebufferMessage Create(
        Framebuffer framebuffer,
        FramebufferUpdateResult update)
    {
        ArgumentNullException.ThrowIfNull(framebuffer);
        ArgumentNullException.ThrowIfNull(update);
        var cursor = CreateCursorUpdate(update.Cursor);
        var length = framebuffer.PixelByteLength;
        var owner = _rent(length);
        try
        {
            if (owner.Memory.Length < length)
            {
                throw new InvalidOperationException("The framebuffer owner is smaller than requested.");
            }

            framebuffer.CopyPixelsTo(owner.Memory.Span[..length]);
            _copyObserver?.Invoke(length);
            return new RemoteFramebufferMessage(
                new RemoteFramebufferSize(framebuffer.Width, framebuffer.Height),
                owner,
                length,
                framebuffer.Stride,
                update.DirtyRects
                    .Select(rectangle => new RemoteRectangle(
                        rectangle.X,
                        rectangle.Y,
                        rectangle.Width,
                        rectangle.Height))
                    .ToArray(),
                cursor);
        }
        catch
        {
            owner.Dispose();
            cursor?.Dispose();
            throw;
        }
    }

    public RemoteServerMessage CreateServerMessage(
        Framebuffer framebuffer,
        FramebufferUpdateResult update)
    {
        ArgumentNullException.ThrowIfNull(framebuffer);
        ArgumentNullException.ThrowIfNull(update);
        if (update.Cursor is not null && update.DirtyRects.Count == 0 && !update.DesktopResized)
        {
            return new RemoteCursorMessage(CreateCursorUpdate(update.Cursor)!);
        }

        return Create(framebuffer, update);
    }

    private static RemoteCursorUpdate? CreateCursorUpdate(RemoteCursor? cursor) =>
        cursor is null
            ? null
            : new RemoteCursorUpdate(
                cursor.HotspotX,
                cursor.HotspotY,
                cursor.Width,
                cursor.Height,
                cursor.GetPixelsBgra32());
}
