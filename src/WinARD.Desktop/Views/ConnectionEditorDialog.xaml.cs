using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinARD.Application.Ports;
using WinARD.Desktop.Services;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Security.Secrets;

namespace WinARD.Desktop.Views;

public sealed partial class ConnectionEditorDialog : ContentDialog, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AsyncUiOperation _operations = new();
    private readonly VaultCredentialStoreSession? _vaultSession;
    private readonly ConnectionEditorHostKeyPrompt? _hostKeyPrompt;
    private readonly ISafeDiagnosticSink? _diagnosticSink;
    private readonly DiagnosticExportService? _diagnosticExportService;
    private readonly Window? _owner;
    private readonly SecretRedactor? _redactor;
    private ConnectionEditorSecretPackage? _pendingHostKeyRetrySecret;
    private bool _suppressSshSecretChange;
    private bool _saved;

    public ConnectionEditorDialog(
        ConnectionEditorViewModel viewModel,
        VaultCredentialStoreSession? vaultSession = null,
        ConnectionEditorHostKeyPrompt? hostKeyPrompt = null,
        ISafeDiagnosticSink? diagnosticSink = null,
        DiagnosticExportService? diagnosticExportService = null,
        Window? owner = null,
        SecretRedactor? redactor = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _vaultSession = vaultSession;
        _hostKeyPrompt = hostKeyPrompt;
        _diagnosticSink = diagnosticSink;
        _diagnosticExportService = diagnosticExportService;
        _owner = owner;
        _redactor = redactor;
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
        SshTargetHostBox.Text = viewModel.SshTargetHost;
        SshTargetPortBox.Value = viewModel.SshTargetPort;
        SshAuthenticationModeBox.SelectedIndex = (int)viewModel.SshAuthenticationMode;
        PrivateKeyPathBox.Text = viewModel.PrivateKeyPath;
        CredentialModeBox.SelectedIndex = viewModel.HasUnsupportedCredentialReference
            ? -1
            : (int)viewModel.CredentialSaveMode;
        WireFieldChanges();
        ViewModel.SshAuthenticationConfigurationChanged += OnSshAuthenticationConfigurationChanged;
        ErrorCard.IsActionEnabled = CanHandleErrorAction;
        ErrorCard.ActionRequested += OnErrorActionRequested;
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
        SshTargetHostBox.TextChanged += (_, _) => { ViewModel.SshTargetHost = SshTargetHostBox.Text; UpdateState(); };
        SshTargetPortBox.ValueChanged += (_, _) => { ViewModel.SshTargetPort = NumberValue(SshTargetPortBox); UpdateState(); };
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

    private void OnSshAuthenticationModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (SshAuthenticationModeBox.SelectedIndex >= 0)
        {
            var selected = (SshAuthenticationMode)SshAuthenticationModeBox.SelectedIndex;
            if (selected != ViewModel.SshAuthenticationMode)
            {
                SshPasswordBox.Password = string.Empty;
                ViewModel.HasSshAuthenticationSecret = false;
                Interlocked.Exchange(ref _pendingHostKeyRetrySecret, null)?.Dispose();
                ViewModel.SshAuthenticationMode = selected;
            }
        }

        UpdateState();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs args)
    {
        if (_suppressSshSecretChange)
        {
            return;
        }

        ViewModel.HasSshAuthenticationSecret = SshPasswordBox.Password.Length > 0;
        UpdateState();
    }

    private void OnSshAuthenticationConfigurationChanged(object? sender, EventArgs args)
    {
        Interlocked.Exchange(ref _pendingHostKeyRetrySecret, null)?.Dispose();
        if (SshPasswordBox.Password.Length == 0)
        {
            return;
        }

        _suppressSshSecretChange = true;
        try
        {
            SshPasswordBox.Password = string.Empty;
        }
        finally
        {
            _suppressSshSecretChange = false;
        }
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
                ShowError(exception);
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
        StartTest(hostKeyPreauthorization: null);
    }

    private void StartTest(IDisposable? hostKeyPreauthorization)
    {
        _ = _operations.RunAsync(async () =>
        {
            ConnectionEditorSecretPackage? retrySecret = null;
            try
            {
                SetBusy(true);
                using var secret = Interlocked.Exchange(ref _pendingHostKeyRetrySecret, null) ?? CaptureSecrets();
                retrySecret = secret?.Clone() as ConnectionEditorSecretPackage;
                await ViewModel.TestConnectionAsync(secret?.Clone(), _lifetime.Token);
                StatusText.Text = ViewModel.StatusMessage;
                StageResults.ItemsSource = ViewModel.TestResults;
                ErrorCard.ViewModel = ViewModel.LastTestError is null
                    ? null
                    : ConnectionErrorViewModel.FromError(
                        ViewModel.LastTestError,
                        ViewModel.LastTestHostKeyFailure?.PreviousFingerprint,
                        ViewModel.LastTestHostKeyFailure?.NewFingerprint);
                if (ViewModel.LastTestHostKeyFailure is not null)
                {
                    _pendingHostKeyRetrySecret = retrySecret;
                    retrySecret = null;
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
            finally
            {
                hostKeyPreauthorization?.Dispose();
                retrySecret?.Dispose();
                SetBusy(false);
            }
        }, _lifetime.Token);
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _hostKeyPrompt?.Cancel();
        Interlocked.Exchange(ref _pendingHostKeyRetrySecret, null)?.Dispose();
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
        SshPasswordBox.Visibility = state.ShowSshSecret ? Visibility.Visible : Visibility.Collapsed;
        SshPasswordBox.Header = state.SshSecretTitle;
        PrivateKeyPathBox.Visibility = state.ShowPrivateKeyPath ? Visibility.Visible : Visibility.Collapsed;
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

        return new ConnectionEditorSecretPackage(mac, ssh, _redactor);
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        WinARD.Security.Vault.VaultLockedException => "加密库已锁定，请重新解锁后重试。",
        CryptographicException => "加密库主密码不正确，仍保持锁定。",
        _ => "操作失败。请检查连接信息和凭据后重试。",
    };

    private void ShowError(Exception exception)
    {
        var error = exception switch
        {
            ConnectionFailedException failed when failed.Result.Error is not null => failed.Result.Error,
            WinARD.Security.Vault.VaultLockedException or CryptographicException => WinArdError.Create(
                ConnectionStage.Authenticating,
                "VAULT_LOCKED",
                "加密库已锁定。",
                Guid.NewGuid().ToString("N")),
            _ => WinArdError.Create(
                ConnectionStage.Connecting,
                "UNEXPECTED_CONNECTION_ERROR",
                "操作失败。",
                Guid.NewGuid().ToString("N")),
        };
        StatusText.Text = SafeMessage(exception);
        ErrorCard.ViewModel = ConnectionErrorViewModel.FromError(error);
        _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
            error.Code,
            error.CorrelationId,
            "Connection editor operation failed.",
            Exception: exception));
    }

    private async void OnErrorActionRequested(object? sender, ConnectionErrorActionKind action)
    {
        try
        {
            switch (action)
            {
                case ConnectionErrorActionKind.Retry:
                    OnTestClicked(TestButton, new RoutedEventArgs());
                    break;
                case ConnectionErrorActionKind.ReenterCredentials:
                    MacPasswordBox.Focus(FocusState.Programmatic);
                    break;
                case ConnectionErrorActionKind.UnlockVault:
                    VaultMasterPasswordBox.Focus(FocusState.Programmatic);
                    break;
                case ConnectionErrorActionKind.CopyCorrelationId:
                    if (ErrorCard.ViewModel is { } card)
                    {
                        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                        package.SetText(card.CorrelationId);
                        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                    }
                    break;
                case ConnectionErrorActionKind.ExportDiagnostics:
                    if (_diagnosticExportService is not null && _owner is not null)
                    {
                        var path = await _diagnosticExportService.ExportAsync(
                            _owner,
                            DesktopDiagnosticContextFactory.Create(),
                            _lifetime.Token);
                        if (path is not null)
                        {
                            StatusText.Text = $"诊断已导出：{path}";
                        }

                        break;
                    }

                    throw new InvalidOperationException("Diagnostic export is not available.");
                case ConnectionErrorActionKind.Cancel:
                    Interlocked.Exchange(ref _pendingHostKeyRetrySecret, null)?.Dispose();
                    ErrorCard.ViewModel = null;
                    break;
                case ConnectionErrorActionKind.OpenHelp:
                    await Windows.System.Launcher.LaunchUriAsync(
                        new Uri("https://support.apple.com/guide/mac-help/control-access-to-screen-recording-mchld6aa7d23/mac"));
                    break;
                case ConnectionErrorActionKind.ReplaceHostKey:
                    if (_hostKeyPrompt is not null && ViewModel.LastTestHostKeyFailure is { } failure)
                    {
                        StartTest(_hostKeyPrompt.Preauthorize(failure));
                        break;
                    }

                    throw new InvalidOperationException("SSH 主机密钥替换上下文已失效。");
                case ConnectionErrorActionKind.Disconnect:
                    Hide();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private bool CanHandleErrorAction(ConnectionErrorActionKind action) => action switch
    {
        ConnectionErrorActionKind.ExportDiagnostics =>
            _diagnosticExportService is not null && _owner is not null,
        ConnectionErrorActionKind.ReplaceHostKey =>
            _hostKeyPrompt is not null && ViewModel.LastTestHostKeyFailure is not null,
        _ => true,
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
        using var registration = RegisterSecret(master);
        await _vaultSession.UnlockAsync(master, _lifetime.Token);
    }

    private IDisposable? RegisterSecret(SecretBuffer secret)
    {
        if (_redactor is null)
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
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Dispose()
    {
        if (_hostKeyPrompt is not null)
        {
            _hostKeyPrompt.StateChanged -= OnHostKeyPromptStateChanged;
            _hostKeyPrompt.Cancel();
        }

        _lifetime.Cancel();
        ViewModel.SshAuthenticationConfigurationChanged -= OnSshAuthenticationConfigurationChanged;
        ErrorCard.ActionRequested -= OnErrorActionRequested;
        ClearPasswords();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
