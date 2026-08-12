using System.Buffers;
using System.Globalization;
using System.Runtime.ExceptionServices;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Desktop.Input;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Clipboard;
using WinARD.Remote.Protocol.Encodings;
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
    internal static ArdDisplayCapabilities ConfirmedStandardQualityCapabilities { get; } = new(
        CapabilitySupport.Observed,
        CapabilitySupport.Observed,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        SafeOnlinePixelFormatSwitch: false,
        SafeOnlineScaleSwitch: false,
        MaximumRefreshRate: null);

    private readonly object _lifecycleSync = new();
    private readonly SessionTrafficCountingStream _traffic;
    private readonly ArdEncryptedStream _transport;
    private readonly FramebufferSnapshotFactory _snapshotFactory;
    private readonly ISafeDiagnosticSink? _diagnosticSink;
    private readonly RemoteInputDiagnosticTracker _inputDiagnostics;
    private readonly bool _requireArdAuthentication;
    private readonly ClientMessageScheduler _messageScheduler;
    private readonly Func<Framebuffer, RemotePixelFormatKind, FramebufferUpdateSession>? _framebufferSessionFactory;
    private RfbHandshakeResult? _handshake;
    private RfbServerInit? _serverInit;
    private Framebuffer? _framebuffer;
    private FramebufferUpdateSession? _framebufferUpdates;
    private ArdAuthenticationResult? _authenticationResult;
    private ArdSessionEncryption? _sessionEncryption;
    private Task? _disposeTask;
    private long _lastFramebufferStatisticsBytes;
    private RemoteQualitySettings? _qualitySettings;
    private bool _framebufferRequestOutstanding;
    private bool _receiveActive;
    private bool _qualityTransitionActive;
    private bool _faulted;
    private bool _shutdownStarted;
    private bool _disposed;

    public RfbClient(
        Stream stream,
        FramebufferSnapshotFactory? snapshotFactory = null,
        ISafeDiagnosticSink? diagnosticSink = null,
        ArdAuthenticationResult? authenticationResult = null,
        bool requireArdAuthentication = false,
        Func<Framebuffer, RemotePixelFormatKind, FramebufferUpdateSession>? framebufferSessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _traffic = new SessionTrafficCountingStream(stream, leaveOpen: true);
        _transport = new ArdEncryptedStream(_traffic, ProtocolLimits.Default);
        _snapshotFactory = snapshotFactory ?? new FramebufferSnapshotFactory();
        _diagnosticSink = diagnosticSink;
        _inputDiagnostics = new RemoteInputDiagnosticTracker(diagnosticSink);
        _authenticationResult = authenticationResult;
        _requireArdAuthentication = requireArdAuthentication;
        _framebufferSessionFactory = framebufferSessionFactory;
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
            framebufferUpdates = CreateFramebufferUpdateSession(
                framebuffer,
                RemotePixelFormatKind.Bgra32);
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
                _qualitySettings = NormalizeSettings(new RemoteQualitySettings(
                    RemotePixelFormatKind.Bgra32,
                    [6, 16, 0, 1, -239, -223],
                    1));
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
        lock (_lifecycleSync)
        {
            ThrowIfProtocolUnavailableNoLock();
            if (_qualityTransitionActive)
            {
                throw new InvalidOperationException("A quality transition is active.");
            }

            if (_framebufferRequestOutstanding)
            {
                throw new InvalidOperationException("A framebuffer update request is already outstanding.");
            }

            _framebufferRequestOutstanding = true;
        }

        return RequestFramebufferUpdateCoreAsync(framebuffer, incremental, cancellationToken);
    }

    private async ValueTask RequestFramebufferUpdateCoreAsync(
        Framebuffer framebuffer,
        bool incremental,
        CancellationToken cancellationToken)
    {
        try
        {
            await _messageScheduler.EnqueueBackgroundAsync(
                token => new ValueTask(RfbSessionInitializer.WriteFramebufferUpdateRequestAsync(
                    _transport,
                    incremental,
                    0,
                    0,
                    checked((ushort)framebuffer.Width),
                    checked((ushort)framebuffer.Height),
                    token)),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_lifecycleSync)
            {
                _framebufferRequestOutstanding = false;
            }

            throw;
        }
    }

    public async ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(
        RemoteQualitySettings settings,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        var framebuffer = _framebuffer ?? throw new InvalidOperationException("RFB initialization has not completed.");
        var normalized = NormalizeSettings(settings);
        FramebufferUpdateSession session;
        lock (_lifecycleSync)
        {
            ThrowIfProtocolUnavailableNoLock();
            if (_framebufferRequestOutstanding || _receiveActive || _qualityTransitionActive)
            {
                return QualityTransitionStatus.CapabilityUnavailable;
            }

            var current = _qualitySettings ?? throw new InvalidOperationException("RFB initialization has not completed.");
            if (SettingsEqual(current, normalized))
            {
                return QualityTransitionStatus.NoChange;
            }

            if (!current.ScaleFactor.Equals(normalized.ScaleFactor))
            {
                // Online server resize timing has not been proven for this connection.
                return QualityTransitionStatus.ReconnectRequired;
            }

            if (normalized.Encodings.Any(encoding => encoding is 1001 or 1002))
            {
                return QualityTransitionStatus.CapabilityUnavailable;
            }

            session = _framebufferUpdates ?? throw new InvalidOperationException("RFB initialization has not completed.");
            _qualityTransitionActive = true;
        }

        FramebufferPixelFormatReconfiguration? pixelReconfiguration = null;
        try
        {
            if (_qualitySettings!.PixelFormat != normalized.PixelFormat)
            {
                pixelReconfiguration = await session.PreparePixelFormatReconfigurationAsync(
                    ToProtocolPixelFormat(normalized.PixelFormat),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lifecycleSync)
            {
                _qualityTransitionActive = false;
            }

            throw;
        }
        catch
        {
            lock (_lifecycleSync)
            {
                _qualityTransitionActive = false;
            }

            return QualityTransitionStatus.CapabilityUnavailable;
        }

        var localCommitted = false;
        var wireAttempted = false;
        try
        {
            await _messageScheduler.EnqueueBackgroundAsync(async token =>
            {
                try
                {
                    var current = _qualitySettings!;
                    var pixelChanged = current.PixelFormat != normalized.PixelFormat;
                    var encodingsChanged = !current.Encodings.SequenceEqual(normalized.Encodings);
                    if (pixelChanged)
                    {
                        wireAttempted = true;
                        await RfbSessionInitializer.WriteSetPixelFormatAsync(
                            _transport,
                            ToProtocolPixelFormat(normalized.PixelFormat),
                            token).ConfigureAwait(false);
                    }

                    if (encodingsChanged)
                    {
                        wireAttempted = true;
                        await RfbSessionInitializer.WriteSetEncodingsAsync(
                            _transport,
                            normalized.Encodings,
                            token).ConfigureAwait(false);
                    }

                    if (pixelReconfiguration is not null)
                    {
                        pixelReconfiguration.Commit();
                        localCommitted = true;
                    }

                    wireAttempted = true;
                    await RfbSessionInitializer.WriteFramebufferUpdateRequestAsync(
                        _transport,
                        incremental: false,
                        0,
                        0,
                        checked((ushort)framebuffer.Width),
                        checked((ushort)framebuffer.Height),
                        token).ConfigureAwait(false);
                    lock (_lifecycleSync)
                    {
                        _qualitySettings = normalized;
                        _framebufferRequestOutstanding = true;
                    }
                }
                catch (OperationCanceledException exception) when (wireAttempted || localCommitted)
                {
                    // A normal scheduler cancellation would allow the next queued writer to run.
                    // Convert it to a scheduler fault before returning cancellation to the caller.
                    throw new QualityTransitionInterruptedException(exception);
                }
            }, cancellationToken).ConfigureAwait(false);

            return QualityTransitionStatus.Applied;
        }
        catch (QualityTransitionInterruptedException exception)
        {
            if (pixelReconfiguration is not null)
            {
                await pixelReconfiguration.DisposeAsync().ConfigureAwait(false);
                pixelReconfiguration = null;
            }

            await FaultAndDisposeDecoderAsync(session).ConfigureAwait(false);
            throw new OperationCanceledException(
                "The quality transition was canceled after protocol output began.",
                exception.InnerException,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (wireAttempted || localCommitted)
            {
                if (pixelReconfiguration is not null)
                {
                    await pixelReconfiguration.DisposeAsync().ConfigureAwait(false);
                    pixelReconfiguration = null;
                }

                await FaultAndDisposeDecoderAsync(session).ConfigureAwait(false);
            }

            throw;
        }
        catch
        {
            if (wireAttempted || localCommitted)
            {
                if (pixelReconfiguration is not null)
                {
                    await pixelReconfiguration.DisposeAsync().ConfigureAwait(false);
                    pixelReconfiguration = null;
                }

                await FaultAndDisposeDecoderAsync(session).ConfigureAwait(false);
                return QualityTransitionStatus.Faulted;
            }

            return QualityTransitionStatus.CapabilityUnavailable;
        }
        finally
        {
            if (pixelReconfiguration is not null)
            {
                await pixelReconfiguration.DisposeAsync().ConfigureAwait(false);
            }

            lock (_lifecycleSync)
            {
                _qualityTransitionActive = false;
            }
        }
    }

    public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Framebuffer framebuffer;
        FramebufferUpdateSession updates;
        lock (_lifecycleSync)
        {
            ThrowIfProtocolUnavailableNoLock();
            if (_receiveActive)
            {
                throw new InvalidOperationException("A server-message receive is already active.");
            }

            if (_qualityTransitionActive)
            {
                throw new InvalidOperationException("A quality transition is active.");
            }

            framebuffer = _framebuffer ?? throw new InvalidOperationException("RFB initialization has not completed.");
            updates = _framebufferUpdates ?? throw new InvalidOperationException("RFB initialization has not completed.");
            _receiveActive = true;
        }
        var handshake = _handshake ?? throw new InvalidOperationException("RFB negotiation has not completed.");
        var reader = new RfbReader(_transport, ProtocolLimits.Default);
        try
        {
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

                            lock (_lifecycleSync)
                            {
                                _framebufferRequestOutstanding = false;
                            }

                            var statistics = new RemoteUpdateStatistics(
                                SessionBytesSinceLastFramebufferStatistics(),
                                update.EncodingCounts,
                                new RemoteFramebufferTransferStatistics(
                                    update.TransferStatistics.RectangleCount,
                                    update.TransferStatistics.WirePayloadBytes,
                                    update.TransferStatistics.PixelWireBytes,
                                    update.TransferStatistics.PixelArea,
                                    update.TransferStatistics.BytesPerPixelMilli,
                                    update.TransferStatistics.RectangleCounts,
                                    update.TransferStatistics.WirePayloadBytesByEncoding,
                                    update.TransferStatistics.PixelWireBytesByEncoding));
                            var message = _snapshotFactory.CreateServerMessage(framebuffer, update, statistics);
                            return message;
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
        finally
        {
            lock (_lifecycleSync)
            {
                _receiveActive = false;
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

    public ArdDisplayCapabilities QualityCapabilities
    {
        get
        {
            ThrowIfDisposed();
            if (_qualitySettings is null)
            {
                throw new InvalidOperationException("RFB initialization has not completed.");
            }

            return ConfirmedStandardQualityCapabilities;
        }
    }

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

    private FramebufferUpdateSession CreateFramebufferUpdateSession(
        Framebuffer framebuffer,
        RemotePixelFormatKind pixelFormat)
    {
        if (_framebufferSessionFactory is not null)
        {
            return _framebufferSessionFactory(framebuffer, pixelFormat);
        }

        return _sessionEncryption is null
            ? FramebufferUpdateReader.CreateSession(framebuffer, ToProtocolPixelFormat(pixelFormat))
            : FramebufferUpdateReader.CreateSession(
                framebuffer,
                ToProtocolPixelFormat(pixelFormat),
                _sessionEncryption.CreateDecoder());
    }

    private async ValueTask FaultAndDisposeDecoderAsync(FramebufferUpdateSession session)
    {
        lock (_lifecycleSync)
        {
            _faulted = true;
        }

        BeginShutdown();
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WriteQualityTransitionCleanupDiagnostic("FaultedConnection", exception);
        }
    }

    private static PixelFormat ToProtocolPixelFormat(RemotePixelFormatKind pixelFormat) =>
        pixelFormat switch
        {
            RemotePixelFormatKind.Bgra32 => PixelFormat.WinArdBgra32,
            RemotePixelFormatKind.Rgb565 => PixelFormat.WinArdRgb565,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat)),
        };

    private RemoteQualitySettings NormalizeSettings(RemoteQualitySettings settings)
    {
        var encodings = new List<int>(settings.Encodings.Count + 6);
        foreach (var encoding in settings.Encodings)
        {
            AddIfMissing(encodings, encoding);
        }

        AddIfMissing(encodings, (int)RfbEncodingType.CopyRect);
        AddIfMissing(encodings, (int)RfbEncodingType.Cursor);
        AddIfMissing(encodings, (int)RfbEncodingType.DesktopSize);
        if (_handshake?.Version == RfbVersion.V3_889)
        {
            AddIfMissing(encodings, (int)RfbEncodingType.ArdDisplayInfo);
            AddIfMissing(encodings, (int)RfbEncodingType.ArdDisplayInfo2);
            if (_sessionEncryption is not null)
            {
                AddIfMissing(encodings, (int)RfbEncodingType.ArdSessionEncryption);
            }
        }

        return new RemoteQualitySettings(settings.PixelFormat, encodings, settings.ScaleFactor);
    }

    private void WriteQualityTransitionCleanupDiagnostic(string stage, Exception exception)
    {
        var failureKind = exception switch
        {
            OperationCanceledException => "Canceled",
            IOException => "IO",
            ObjectDisposedException => "Disposed",
            AggregateException => "Aggregate",
            _ => "Other",
        };
        _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
            "RFB_QUALITY_TRANSITION_CLEANUP",
            Guid.NewGuid().ToString("N"),
            "Quality transition decoder cleanup failed.",
            [
                new("Stage", stage),
                new("FailureKind", failureKind),
            ]));
    }

    private static void AddIfMissing(List<int> encodings, int encoding)
    {
        if (!encodings.Contains(encoding))
        {
            encodings.Add(encoding);
        }
    }

    private static bool SettingsEqual(RemoteQualitySettings left, RemoteQualitySettings right) =>
        left.PixelFormat == right.PixelFormat &&
        left.ScaleFactor.Equals(right.ScaleFactor) &&
        left.Encodings.SequenceEqual(right.Encodings);

    private void ThrowIfProtocolUnavailableNoLock()
    {
        if (_faulted)
        {
            throw new RfbProtocolException("The RFB client is faulted; discard it and the connection.");
        }

        ObjectDisposedException.ThrowIf(_shutdownStarted || _disposed, this);
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

    private sealed class QualityTransitionInterruptedException(OperationCanceledException innerException)
        : InvalidOperationException("A quality transition was interrupted after protocol output began.", innerException);
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
