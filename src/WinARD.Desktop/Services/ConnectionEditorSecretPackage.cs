using WinARD.Application.Ports;
using WinARD.Infrastructure.Diagnostics;

namespace WinARD.Desktop.Services;

public sealed class ConnectionEditorSecretPackage : ISecret
{
    private ISecret? _macSecret;
    private ISecret? _sshSecret;
    private readonly SecretRedactor? _redactor;
    private IDisposable? _macRegistration;
    private IDisposable? _sshRegistration;

    public ConnectionEditorSecretPackage(
        ISecret? macSecret,
        ISecret? sshSecret,
        SecretRedactor? redactor = null)
    {
        if (macSecret is null && sshSecret is null)
        {
            throw new ArgumentException("At least one temporary secret is required.");
        }

        _redactor = redactor;
        try
        {
            _macSecret = macSecret?.Clone();
            _sshSecret = sshSecret?.Clone();
            _macRegistration = Register(_macSecret);
            _sshRegistration = Register(_sshSecret);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Length => MacSecret.Length;

    public bool HasMacSecret => _macSecret is not null;

    public bool HasSshSecret => _sshSecret is not null;

    public ISecret CloneMacSecret() => MacSecret.Clone();

    public ISecret CloneSshSecret() => SshSecret.Clone();

    public void CopyTo(Span<byte> destination) => MacSecret.CopyTo(destination);

    public ISecret Clone() => new ConnectionEditorSecretPackage(_macSecret, _sshSecret, _redactor);

    public void Dispose()
    {
        Interlocked.Exchange(ref _macRegistration, null)?.Dispose();
        Interlocked.Exchange(ref _sshRegistration, null)?.Dispose();
        Interlocked.Exchange(ref _macSecret, null)?.Dispose();
        Interlocked.Exchange(ref _sshSecret, null)?.Dispose();
    }

    private IDisposable? Register(ISecret? secret)
    {
        if (secret is null || _redactor is null)
        {
            return null;
        }

        var bytes = new byte[secret.Length];
        try
        {
            secret.CopyTo(bytes);
            return _redactor.Register(bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private ISecret MacSecret => _macSecret ??
        throw new InvalidOperationException("No Mac credential was supplied.");

    private ISecret SshSecret => _sshSecret ??
        throw new InvalidOperationException("No SSH credential was supplied.");
}
