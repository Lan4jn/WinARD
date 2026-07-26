using WinARD.Application.Ports;

namespace WinARD.Desktop.Services;

public sealed class ConnectionEditorSecretPackage : ISecret
{
    private ISecret? _macSecret;
    private ISecret? _sshSecret;

    public ConnectionEditorSecretPackage(ISecret? macSecret, ISecret? sshSecret)
    {
        if (macSecret is null && sshSecret is null)
        {
            throw new ArgumentException("At least one temporary secret is required.");
        }

        _macSecret = macSecret?.Clone();
        _sshSecret = sshSecret?.Clone();
    }

    public int Length => MacSecret.Length;

    public bool HasMacSecret => _macSecret is not null;

    public bool HasSshSecret => _sshSecret is not null;

    public ISecret CloneMacSecret() => MacSecret.Clone();

    public ISecret CloneSshSecret() => SshSecret.Clone();

    public void CopyTo(Span<byte> destination) => MacSecret.CopyTo(destination);

    public ISecret Clone() => new ConnectionEditorSecretPackage(_macSecret, _sshSecret);

    public void Dispose()
    {
        Interlocked.Exchange(ref _macSecret, null)?.Dispose();
        Interlocked.Exchange(ref _sshSecret, null)?.Dispose();
    }

    private ISecret MacSecret => _macSecret ??
        throw new InvalidOperationException("No Mac credential was supplied.");

    private ISecret SshSecret => _sshSecret ??
        throw new InvalidOperationException("No SSH credential was supplied.");
}
