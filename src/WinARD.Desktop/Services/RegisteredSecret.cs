using System.Security.Cryptography;
using WinARD.Application.Ports;
using WinARD.Infrastructure.Diagnostics;

namespace WinARD.Desktop.Services;

internal sealed class RegisteredSecret : ISecret
{
    private readonly SecretRedactor _redactor;
    private ISecret? _secret;
    private IDisposable? _registration;

    public RegisteredSecret(ISecret secret, SecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(secret);
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _secret = secret;
        byte[]? bytes = null;
        try
        {
            bytes = new byte[secret.Length];
            secret.CopyTo(bytes);
            _registration = redactor.Register(bytes);
        }
        catch
        {
            Interlocked.Exchange(ref _secret, null)?.Dispose();
            throw;
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    public int Length => Secret.Length;

    public void CopyTo(Span<byte> destination) => Secret.CopyTo(destination);

    public ISecret Clone() => new RegisteredSecret(Secret.Clone(), _redactor);

    public void Dispose()
    {
        Interlocked.Exchange(ref _registration, null)?.Dispose();
        Interlocked.Exchange(ref _secret, null)?.Dispose();
    }

    private ISecret Secret => _secret ?? throw new ObjectDisposedException(nameof(RegisteredSecret));
}
