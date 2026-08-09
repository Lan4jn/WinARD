using System.Buffers;
using System.Globalization;
using System.Runtime.ExceptionServices;
using WinARD.Application.Ports;
using WinARD.Desktop.Input;
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
    private readonly object _lifecycleSync = new();
    private readonly SessionTrafficCountingStream _traffic;
    private readonly ArdEncryptedStream _transport;
    private readonly FramebufferSnapshotFactory _snapshotFactory;
    private readonly ISafeDiagnosticSink? _diagnosticSink;
    private readonly RemoteInputDiagnosticTracker _inputDiagnostics;
    private readonly bool _requireArdAuthentication;
    private readonly ClientMessageScheduler _messageScheduler;
    private RfbHandshakeResult? _handshake;
    private RfbServerInit? _serverInit;
    private Framebuffer? _framebuffer;
    private FramebufferUpdateSession? _framebufferUpdates;
    private ArdAuthenticationResult? _authenticationResult;
    private ArdSessionEncryption? _sessionEncryption;
    private Task? _disposeTask;
    private long _lastFramebufferStatisticsBytes;
    private bool _shutdownStarted;
    private bool _disposed;

    public RfbClient(
        Stream stream,
        FramebufferSnapshotFactory? snapshotFactory = null,
        ISafeDiagnosticSink? diagnosticSink = null,
        ArdAuthenticationResult? authenticationResult = null,
        bool requireArdAuthentication = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _traffic = new SessionTrafficCountingStream(stream, leaveOpen: true);
        _transport = new ArdEncryptedStream(_traffic, ProtocolLimits.Default);
        _snapshotFactory = snapshotFactory ?? new FramebufferSnapshotFactory();
        _diagnosticSink = diagnosticSink;
        _inputDiagnostics = new RemoteInputDiagnosticTracker(diagnosticSink);
        _authenticationResult = authenticationResult;
        _requireArdAuthentication = requireArdAuthentication;
        _messageScheduler = new ClientMessageScheduler();
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

        RfbServerInit serverInit;
        if (handshake.Version == RfbVersion.V3_889 && _authenticationResult is not null)
        {
            _sessionEncryption = new ArdSessionEncryption(_transport, _authenticationResult);
            _authenticationResult = null;
            serverInit = await RfbSessionInitializer.InitializeAsync(
                _transport,
                handshake,
                ProtocolLimits.Default,
                _sessionEncryption,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            serverInit = await RfbSessionInitializer.InitializeAsync(
                _transport,
                handshake,
                ProtocolLimits.Default,
                cancellationToken).ConfigureAwait(false);
        }

        var framebuffer = new Framebuffer(
            serverInit.Width,
            serverInit.Height,
            ProtocolLimits.Default);
        FramebufferUpdateSession framebufferUpdates;
        try
        {
            framebufferUpdates = _sessionEncryption is null
                ? FramebufferUpdateReader.CreateSession(framebuffer, PixelFormat.WinArdBgra32)
                : FramebufferUpdateReader.CreateSession(
                    framebuffer,
                    PixelFormat.WinArdBgra32,
                    _sessionEncryption.CreateDecoder());
        }
        catch
        {
            framebuffer.Dispose();
            throw;
        }

        var published = false;
        lock (_lifecycleSync)
        {
            if (!_shutdownStarted)
            {
                _serverInit = serverInit;
                _framebuffer = framebuffer;
                _framebufferUpdates = framebufferUpdates;
                published = true;
            }
        }

        if (!published)
        {
            await framebufferUpdates.DisposeAsync().ConfigureAwait(false);
            framebuffer.Dispose();
            throw new ObjectDisposedException(nameof(RfbClient));
        }

        _lastFramebufferStatisticsBytes = _traffic.BytesRead;
        WriteNegotiationDiagnostic(handshake, serverInit);
    }

    public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var framebuffer = _framebuffer ?? throw new InvalidOperationException("RFB initialization has not completed.");
        return _messageScheduler.EnqueueBackgroundAsync(
            token => new ValueTask(RfbSessionInitializer.WriteFramebufferUpdateRequestAsync(
                _transport,
                incremental,
                0,
                0,
                checked((ushort)framebuffer.Width),
                checked((ushort)framebuffer.Height),
                token)),
            cancellationToken);
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

                        var statistics = new RemoteUpdateStatistics(
                            SessionBytesSinceLastFramebufferStatistics(),
                            update.EncodingCounts);
                        return _snapshotFactory.CreateServerMessage(framebuffer, update, statistics);
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
                            await _messageScheduler.EnqueueBackgroundAsync(
                                    token => new ArdClientMessageWriter(new RfbWriter(_transport))
                                        .WriteAutoFramebufferUpdateAsync(
                                            checked((ushort)framebuffer.Width),
                                            checked((ushort)framebuffer.Height),
                                            token),
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

    public async ValueTask SendPointerAsync(
        byte buttons,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_sessionEncryption is not null)
        {
            await _sessionEncryption.WaitUntilEncryptedAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnqueueInputAsync(async token =>
        {
            var encrypted = _transport.IsEncrypted;
            _inputDiagnostics.Record(
                RemoteInputKind.Pointer,
                RemoteInputBoundary.ProtocolWriteStarted,
                encrypted);
            await new PointerEventWriter(new RfbWriter(_transport)).WriteAsync(
                    buttons,
                    x,
                    y,
                    token)
                .ConfigureAwait(false);
            _inputDiagnostics.Record(
                RemoteInputKind.Pointer,
                RemoteInputBoundary.ProtocolWriteCompleted,
                encrypted);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendKeyAsync(
        uint keysym,
        bool down,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_sessionEncryption is not null)
        {
            await _sessionEncryption.WaitUntilEncryptedAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnqueueInputAsync(async token =>
        {
            var encrypted = _transport.IsEncrypted;
            _inputDiagnostics.Record(
                RemoteInputKind.Keyboard,
                RemoteInputBoundary.ProtocolWriteStarted,
                encrypted);
            await new KeyEventWriter(new RfbWriter(_transport)).WriteAsync(
                    down,
                    keysym,
                    token)
                .ConfigureAwait(false);
            _inputDiagnostics.Record(
                RemoteInputKind.Keyboard,
                RemoteInputBoundary.ProtocolWriteCompleted,
                encrypted);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_sessionEncryption is not null)
        {
            await _sessionEncryption.WaitUntilEncryptedAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnqueueBackgroundAsync(async token =>
        {
            await new ClipboardProtocol(new RfbWriter(_transport)).WriteClientCutTextAsync(
                    text,
                    token)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public void BeginShutdown()
    {
        lock (_lifecycleSync)
        {
            if (_shutdownStarted)
            {
                return;
            }

            _shutdownStarted = true;
        }

        _messageScheduler.AbortActiveWrites();
    }

    public RemoteDisplayCapabilities DisplayCapabilities => RemoteDisplayCapabilities.Unknown;

    public RemoteRuntimePerformanceSnapshot PerformanceSnapshot =>
        _messageScheduler.PerformanceSnapshot;

    public ValueTask DisposeAsync()
    {
        BeginShutdown();
        lock (_lifecycleSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        try
        {
            await _messageScheduler.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

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
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _traffic.DisposeAsync().ConfigureAwait(false);
        }

        lock (_lifecycleSync)
        {
            _disposed = true;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_shutdownStarted || _disposed, this);
        }
    }

    private ValueTask EnqueueInputAsync(
        Func<CancellationToken, ValueTask> write,
        CancellationToken cancellationToken) =>
        _messageScheduler.EnqueueInputAsync(write, cancellationToken);

    private ValueTask EnqueueBackgroundAsync(
        Func<CancellationToken, ValueTask> write,
        CancellationToken cancellationToken) =>
        _messageScheduler.EnqueueBackgroundAsync(write, cancellationToken);

    private long SessionBytesSinceLastFramebufferStatistics()
    {
        var currentBytes = _traffic.BytesRead;
        var previousBytes = Interlocked.Exchange(
            ref _lastFramebufferStatisticsBytes,
            currentBytes);
        return currentBytes >= previousBytes ? currentBytes - previousBytes : 0;
    }

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
        FramebufferUpdateResult update,
        RemoteUpdateStatistics? statistics = null)
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
                cursor,
                statistics);
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
        FramebufferUpdateResult update,
        RemoteUpdateStatistics? statistics = null)
    {
        ArgumentNullException.ThrowIfNull(framebuffer);
        ArgumentNullException.ThrowIfNull(update);
        if (update.Cursor is not null && update.DirtyRects.Count == 0 && !update.DesktopResized)
        {
            return new RemoteCursorMessage(CreateCursorUpdate(update.Cursor)!, statistics);
        }

        return Create(framebuffer, update, statistics);
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
