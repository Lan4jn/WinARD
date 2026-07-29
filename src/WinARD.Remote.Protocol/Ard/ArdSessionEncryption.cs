using System.Security.Cryptography;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Ard;

public enum ArdSessionEncryptionState
{
    Plaintext,
    Requested,
    PendingActivation,
    Encrypted,
}

public sealed class ArdSessionEncryption : IAsyncDisposable
{
    private readonly ArdEncryptedStream _transport;
    private readonly ArdAuthenticationResult _authentication;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ArdSessionCipherMaterial? _pendingMaterial;
    private ArdSessionEncryptionState _state;
    private bool _disposed;

    public ArdSessionEncryption(
        ArdEncryptedStream transport,
        ArdAuthenticationResult authentication)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    }

    public ArdSessionEncryptionState State
    {
        get
        {
            _gate.Wait();
            try
            {
                ThrowIfDisposed();
                return _state;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public IRfbEncodingDecoder CreateDecoder()
    {
        ThrowIfDisposed();
        return new ArdSessionEncryptionEncoding(this);
    }

    public async ValueTask RequestAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_state != ArdSessionEncryptionState.Plaintext)
            {
                throw NegotiationFailure("ARD session encryption was requested out of order.");
            }

            await new ArdClientMessageWriter(new RfbWriter(_transport))
                .WriteSetEncryptionRequestAsync(cancellationToken)
                .ConfigureAwait(false);
            _state = ArdSessionEncryptionState.Requested;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask CompleteFramebufferUpdateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_state != ArdSessionEncryptionState.PendingActivation)
            {
                return;
            }

            var material = _pendingMaterial ??
                throw NegotiationFailure("ARD session encryption material is unavailable.");
            _pendingMaterial = null;
            await _transport.WritePlaintextAndActivateAsync(
                    ArdClientMessageWriter.SetEncryptionAcknowledgementMessage,
                    material,
                    cancellationToken)
                .ConfigureAwait(false);
            _state = ArdSessionEncryptionState.Encrypted;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void AcceptSessionMaterial(
        ReadOnlySpan<byte> encryptedSessionKey,
        ReadOnlySpan<byte> encryptedInitialIv)
    {
        _gate.Wait();
        try
        {
            ThrowIfDisposed();
            if (_state != ArdSessionEncryptionState.Requested || _pendingMaterial is not null)
            {
                throw NegotiationFailure("ARD session encryption material was received out of order.");
            }

            var sessionKey = new byte[16];
            var initialIv = new byte[16];
            try
            {
                _authentication.DecryptEcb(encryptedSessionKey, sessionKey);
                _authentication.DecryptEcb(encryptedInitialIv, initialIv);
                _pendingMaterial = new ArdSessionCipherMaterial(sessionKey, initialIv);
                sessionKey = null!;
                initialIv = null!;
                _state = ArdSessionEncryptionState.PendingActivation;
            }
            finally
            {
                if (sessionKey is not null)
                {
                    CryptographicOperations.ZeroMemory(sessionKey);
                }

                if (initialIv is not null)
                {
                    CryptographicOperations.ZeroMemory(initialIv);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingMaterial?.Dispose();
            _pendingMaterial = null;
            _authentication.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static RfbProtocolException NegotiationFailure(string message) =>
        RfbProtocolException.Create(
            message,
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.ArdEncryptionNegotiation));
}
