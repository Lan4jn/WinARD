using System.Buffers;
using System.Globalization;
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
    public IRfbClient Create(Stream stream) => new RfbClient(stream, diagnosticSink: diagnosticSink);
}

internal sealed class RfbClient : IRfbClient
{
    private readonly Stream _stream;
    private readonly FramebufferSnapshotFactory _snapshotFactory;
    private readonly ISafeDiagnosticSink? _diagnosticSink;
    private RfbHandshakeResult? _handshake;
    private RfbServerInit? _serverInit;
    private Framebuffer? _framebuffer;
    private FramebufferUpdateSession? _framebufferUpdates;
    private bool _disposed;

    public RfbClient(
        Stream stream,
        FramebufferSnapshotFactory? snapshotFactory = null,
        ISafeDiagnosticSink? diagnosticSink = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _snapshotFactory = snapshotFactory ?? new FramebufferSnapshotFactory();
        _diagnosticSink = diagnosticSink;
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
        _handshake = await RfbHandshake.NegotiateAsync(_stream, cancellationToken).ConfigureAwait(false);
    }

    public async Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(secret);
        var handshake = _handshake ?? throw new InvalidOperationException("RFB negotiation has not completed.");
        using var usernameMaterial = SecretMaterial.FromUtf8(username);
        using var passwordMaterial = new ApplicationSecretMaterial(secret);
        await new ArdAuthenticator().AuthenticateAsync(
            _stream,
            handshake.Version,
            usernameMaterial,
            passwordMaterial,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var handshake = _handshake ?? throw new InvalidOperationException("RFB negotiation has not completed.");
        _serverInit = await RfbSessionInitializer.InitializeAsync(
            _stream,
            handshake,
            ProtocolLimits.Default,
            cancellationToken).ConfigureAwait(false);
        _framebuffer = new Framebuffer(
            _serverInit.Width,
            _serverInit.Height,
            ProtocolLimits.Default);
        _framebufferUpdates = FramebufferUpdateReader.CreateSession(
            _framebuffer,
            PixelFormat.WinArdBgra32);
        WriteNegotiationDiagnostic(handshake, _serverInit);
    }

    public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var framebuffer = _framebuffer ?? throw new InvalidOperationException("RFB initialization has not completed.");
        return new ValueTask(RfbSessionInitializer.WriteFramebufferUpdateRequestAsync(
            _stream,
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
        var reader = new RfbReader(_stream, ProtocolLimits.Default);
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
                        return await updates.ApplyBodyAsync(
                                _stream,
                                (surface, update) => _snapshotFactory.CreateServerMessage(surface, update),
                                cancellationToken)
                            .ConfigureAwait(false);
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
        return new PointerEventWriter(new RfbWriter(_stream)).WriteAsync(
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
        return new KeyEventWriter(new RfbWriter(_stream)).WriteAsync(
            down,
            keysym,
            cancellationToken);
    }

    public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return new ClipboardProtocol(new RfbWriter(_stream)).WriteClientCutTextAsync(
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
        if (_framebufferUpdates is not null)
        {
            await _framebufferUpdates.DisposeAsync().ConfigureAwait(false);
        }

        _framebuffer?.Dispose();
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
