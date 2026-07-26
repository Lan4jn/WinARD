using WinARD.Application.Ports;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.Initialization;
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
    private bool _disposed;

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
        _ = await RfbSessionInitializer.InitializeAsync(
            _stream,
            ProtocolLimits.Default,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
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
