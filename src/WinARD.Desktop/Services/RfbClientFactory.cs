using WinARD.Application.Ports;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Clipboard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.Initialization;
using WinARD.Remote.Protocol.Input;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Desktop.Services;

public sealed class RfbClientFactory : IRfbClientFactory
{
    public IRfbClient Create(Stream stream) => new RfbClient(stream);
}

internal sealed class RfbClient(Stream stream) : IRfbClient
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private RfbHandshakeResult? _handshake;
    private RfbServerInit? _serverInit;
    private Framebuffer? _framebuffer;
    private FramebufferUpdateSession? _framebufferUpdates;
    private bool _disposed;

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
        _serverInit = await RfbSessionInitializer.InitializeAsync(
            _stream,
            ProtocolLimits.Default,
            cancellationToken).ConfigureAwait(false);
        _framebuffer = new Framebuffer(
            _serverInit.Width,
            _serverInit.Height,
            ProtocolLimits.Default);
        _framebufferUpdates = FramebufferUpdateReader.CreateSession(
            _framebuffer,
            PixelFormat.WinArdBgra32);
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
        var reader = new RfbReader(_stream, ProtocolLimits.Default);
        var type = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        switch (type)
        {
            case 0:
                {
                    var update = await updates.ApplyBodyAsync(_stream, cancellationToken).ConfigureAwait(false);
                    return new RemoteFramebufferMessage(
                        new RemoteFramebufferSize(framebuffer.Width, framebuffer.Height),
                        framebuffer.GetPixelsBgra32(),
                        framebuffer.Stride,
                        update.DirtyRects
                            .Select(rectangle => new RemoteRectangle(
                                rectangle.X,
                                rectangle.Y,
                                rectangle.Width,
                                rectangle.Height))
                            .ToArray());
                }
            case 2:
                return new RemoteBellMessage();
            case 3:
                return new RemoteClipboardMessage(
                    await ClipboardProtocol.ReadServerCutTextBodyAsync(
                        reader,
                        cancellationToken).ConfigureAwait(false));
            default:
                throw new RfbProtocolException($"Unsupported RFB server message type {type}.");
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

    private sealed class ApplicationSecretMaterial(ISecret secret) : ISecretMaterial
    {
        private ISecret? _secret = secret.Clone();

        public int Length => Secret.Length;

        public void CopyTo(Span<byte> destination) => Secret.CopyTo(destination);

        public void Dispose() => Interlocked.Exchange(ref _secret, null)?.Dispose();

        private ISecret Secret => _secret ?? throw new ObjectDisposedException(nameof(ApplicationSecretMaterial));
    }
}
