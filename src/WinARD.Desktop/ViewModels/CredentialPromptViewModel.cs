using CommunityToolkit.Mvvm.ComponentModel;
using WinARD.Application.Ports;

namespace WinARD.Desktop.ViewModels;

public sealed class CredentialPromptViewModel : ObservableObject, IDisposable
{
    private ISecret? _secret;

    public bool HasSecret => _secret is not null;

    public void Supply(ISecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Interlocked.Exchange(ref _secret, secret)?.Dispose();
        OnPropertyChanged(nameof(HasSecret));
    }

    public ISecret Take()
    {
        var secret = Interlocked.Exchange(ref _secret, null) ??
            throw new InvalidOperationException("尚未提供凭据。");
        OnPropertyChanged(nameof(HasSecret));
        return secret;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _secret, null)?.Dispose();
        OnPropertyChanged(nameof(HasSecret));
    }
}
