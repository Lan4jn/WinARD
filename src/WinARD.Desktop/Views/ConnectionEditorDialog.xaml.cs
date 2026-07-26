using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinARD.Application.Ports;
using WinARD.Desktop.Services;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Security.Secrets;

namespace WinARD.Desktop.Views;

public sealed partial class ConnectionEditorDialog : ContentDialog, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AsyncUiOperation _operations = new();
    private readonly VaultCredentialStoreSession? _vaultSession;
    private readonly ConnectionEditorHostKeyPrompt? _hostKeyPrompt;
    private bool _saved;

    public ConnectionEditorDialog(
        ConnectionEditorViewModel viewModel,
        VaultCredentialStoreSession? vaultSession = null,
        ConnectionEditorHostKeyPrompt? hostKeyPrompt = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _vaultSession = vaultSession;
        _hostKeyPrompt = hostKeyPrompt;
        InitializeComponent();
        DisplayNameBox.Text = viewModel.DisplayName;
        HostBox.Text = viewModel.Host;
        PortBox.Value = viewModel.Port;
        MacUsernameBox.Text = viewModel.MacUsername;
        UseSshSwitch.IsOn = viewModel.UseSsh;
        SshSection.IsExpanded = viewModel.UseSsh;
        SshHostBox.Text = viewModel.SshHost;
        SshPortBox.Value = viewModel.SshPort;
        SshUsernameBox.Text = viewModel.SshUsername;
        PrivateKeyPathBox.Text = viewModel.PrivateKeyPath;
        CredentialModeBox.SelectedIndex = viewModel.HasUnsupportedCredentialReference
            ? -1
            : (int)viewModel.CredentialSaveMode;
        WireFieldChanges();
        PrimaryButtonClick += OnSaveClicked;
        Closing += OnClosing;
        if (_hostKeyPrompt is not null)
        {
            _hostKeyPrompt.StateChanged += OnHostKeyPromptStateChanged;
        }

        UpdateState();
        UpdateHostKeyPromptState();
    }

    public ConnectionEditorViewModel ViewModel { get; }

    public ConnectionProfileSaveResult? SavedOutcome { get; private set; }

    public ConnectionProfile? SavedProfile => SavedOutcome?.Profile;

    public Task WhenIdleAsync() => _operations.WhenIdleAsync();

    private void WireFieldChanges()
    {
        DisplayNameBox.TextChanged += (_, _) => { ViewModel.DisplayName = DisplayNameBox.Text; UpdateState(); };
        HostBox.TextChanged += (_, _) => { ViewModel.Host = HostBox.Text; UpdateState(); };
        PortBox.ValueChanged += (_, _) => { ViewModel.Port = NumberValue(PortBox); UpdateState(); };
        MacUsernameBox.TextChanged += (_, _) => { ViewModel.MacUsername = MacUsernameBox.Text; UpdateState(); };
        SshHostBox.TextChanged += (_, _) => { ViewModel.SshHost = SshHostBox.Text; UpdateState(); };
        SshPortBox.ValueChanged += (_, _) => { ViewModel.SshPort = NumberValue(SshPortBox); UpdateState(); };
        SshUsernameBox.TextChanged += (_, _) => { ViewModel.SshUsername = SshUsernameBox.Text; UpdateState(); };
        PrivateKeyPathBox.TextChanged += (_, _) => { ViewModel.PrivateKeyPath = PrivateKeyPathBox.Text; UpdateState(); };
    }

    private void OnUseSshToggled(object sender, RoutedEventArgs args)
    {
        ViewModel.UseSsh = UseSshSwitch.IsOn;
        SshSection.IsExpanded = UseSshSwitch.IsOn;
        UpdateState();
    }

    private void OnCredentialModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (CredentialModeBox.SelectedIndex >= 0)
        {
            ViewModel.CredentialSaveMode = (CredentialSaveMode)CredentialModeBox.SelectedIndex;
        }

        UpdateState();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs args)
    {
        ViewModel.HasSshAuthenticationSecret = SshPasswordBox.Password.Length > 0;
        UpdateState();
    }

    private void OnSaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        _ = _operations.RunAsync(async () =>
        {
            try
            {
                SetBusy(true);
                await UnlockVaultIfRequiredAsync();
                using var secret = CaptureSecrets();
                _ = await ViewModel.SaveAsync(secret?.Clone(), _lifetime.Token);
                SavedOutcome = ViewModel.LastSaveResult ??
                    throw new InvalidOperationException("保存完成但未返回结果。");
                _saved = true;
                Hide();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                StatusText.Text = SafeMessage(exception);
            }
            finally
            {
                SetBusy(false);
                deferral.Complete();
            }
        }, _lifetime.Token);
    }

    private void OnTestClicked(object sender, RoutedEventArgs args)
    {
        _ = _operations.RunAsync(async () =>
        {
            try
            {
                SetBusy(true);
                using var secret = CaptureSecrets();
                await ViewModel.TestConnectionAsync(secret?.Clone(), _lifetime.Token);
                StatusText.Text = ViewModel.StatusMessage;
                StageResults.ItemsSource = ViewModel.TestResults;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                StatusText.Text = SafeMessage(exception);
            }
            finally
            {
                SetBusy(false);
            }
        }, _lifetime.Token);
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _hostKeyPrompt?.Cancel();
        if (!_saved)
        {
            _lifetime.Cancel();
        }

        ClearPasswords();
    }

    private void OnHostKeyPromptStateChanged(object? sender, EventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateHostKeyPromptState();
            return;
        }

        _ = DispatcherQueue.TryEnqueue(UpdateHostKeyPromptState);
    }

    private void UpdateHostKeyPromptState()
    {
        var state = _hostKeyPrompt?.State ?? ConnectionEditorHostKeyPromptState.Hidden;
        HostKeyPromptPanel.Visibility = state.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        HostKeyPromptTitle.Text = state.IsChanged ? "SSH 主机密钥已更改" : "未知的 SSH 主机密钥";
        HostKeyEndpointText.Text = state.Endpoint is { } endpoint
            ? $"端点：{endpoint.Host}:{endpoint.Port}"
            : string.Empty;
        HostKeyAlgorithmText.Text = $"算法：{state.Algorithm}";
        HostKeyPreviousFingerprintText.Text = state.PreviousFingerprint is null
            ? string.Empty
            : $"原 SHA256：{state.PreviousFingerprint}";
        HostKeyPreviousFingerprintText.Visibility = state.PreviousFingerprint is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        HostKeyNewFingerprintText.Text = $"新 SHA256：{state.NewFingerprint}";
        TrustHostKeyButton.Visibility = state.IsChanged ? Visibility.Collapsed : Visibility.Visible;
        ReplaceHostKeyButton.Visibility = state.IsChanged ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTrustHostKeyClicked(object sender, RoutedEventArgs args) => _hostKeyPrompt?.Trust();

    private void OnReplaceHostKeyClicked(object sender, RoutedEventArgs args) => _hostKeyPrompt?.Replace();

    private void OnCancelHostKeyClicked(object sender, RoutedEventArgs args) => _hostKeyPrompt?.Cancel();

    private void SetBusy(bool busy)
    {
        BusyIndicator.IsActive = busy;
        TestButton.IsEnabled = !busy && ViewModel.TestConnectionCommand.CanExecute(null);
        IsPrimaryButtonEnabled = !busy && ViewModel.SaveCommand.CanExecute(null);
    }

    private void UpdateState()
    {
        var state = ConnectionEditorDialogStatePresenter.Present(ViewModel);
        BusyIndicator.IsActive = state.IsBusy;
        TestButton.IsEnabled = state.TestEnabled;
        IsPrimaryButtonEnabled = state.SaveEnabled;
        StatusText.Text = state.StatusMessage;
    }

    private static int NumberValue(NumberBox box) =>
        double.IsNaN(box.Value) ? 0 : checked((int)box.Value);

    private static SecretBuffer? CaptureAndClear(PasswordBox box)
    {
        if (box.Password.Length == 0)
        {
            box.Password = string.Empty;
            return null;
        }

        var bytes = new byte[Encoding.UTF8.GetByteCount(box.Password)];
        try
        {
            _ = Encoding.UTF8.GetBytes(box.Password, bytes);
            return SecretBuffer.CopyFrom(bytes);
        }
        finally
        {
            box.Password = string.Empty;
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private ConnectionEditorSecretPackage? CaptureSecrets()
    {
        using var mac = CaptureAndClear(MacPasswordBox);
        using var ssh = CaptureAndClear(SshPasswordBox);
        if (mac is null && ssh is null)
        {
            return null;
        }

        return new ConnectionEditorSecretPackage(mac, ssh);
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        WinARD.Security.Vault.VaultLockedException => "加密库已锁定，请重新解锁后重试。",
        CryptographicException => "加密库主密码不正确，仍保持锁定。",
        _ => "操作失败。请检查连接信息和凭据后重试。",
    };

    private void ClearPasswords()
    {
        MacPasswordBox.Password = string.Empty;
        SshPasswordBox.Password = string.Empty;
        VaultMasterPasswordBox.Password = string.Empty;
    }

    private async Task UnlockVaultIfRequiredAsync()
    {
        if (ViewModel.CredentialSaveMode != CredentialSaveMode.EncryptedVault ||
            _vaultSession?.IsUnlocked == true)
        {
            VaultMasterPasswordBox.Password = string.Empty;
            return;
        }

        if (_vaultSession is null)
        {
            throw new WinARD.Security.Vault.VaultLockedException();
        }

        using var master = CaptureAndClear(VaultMasterPasswordBox) ??
            throw new WinARD.Security.Vault.VaultLockedException();
        await _vaultSession.UnlockAsync(master, _lifetime.Token);
    }

    public void Dispose()
    {
        if (_hostKeyPrompt is not null)
        {
            _hostKeyPrompt.StateChanged -= OnHostKeyPromptStateChanged;
            _hostKeyPrompt.Cancel();
        }

        _lifetime.Cancel();
        ClearPasswords();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
