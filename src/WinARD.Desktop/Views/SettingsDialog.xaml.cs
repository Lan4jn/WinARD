using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinARD.Domain.Settings;

namespace WinARD.Desktop.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly Func<ValueTask> _lockVault;

    public SettingsDialog(AppSettings settings, Func<ValueTask> lockVault)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _lockVault = lockVault ?? throw new ArgumentNullException(nameof(lockVault));
        InitializeComponent();
        ThemeBox.SelectedIndex = (int)settings.Theme;
        DiagnosticLevelBox.SelectedIndex = (int)settings.DiagnosticLevel;
        ClipboardDefaultBox.IsChecked = settings.ClipboardEnabledByDefault;
        CredentialBackendBox.SelectedIndex = (int)settings.DefaultCredentialBackend;
        VaultTimeoutBox.Value = settings.VaultIdleTimeout.TotalMinutes;
    }

    public AppSettings SelectedSettings => AppSettings.Create(
        AppSettings.CurrentVersion,
        (AppTheme)ThemeBox.SelectedIndex,
        (SafeDiagnosticLevel)DiagnosticLevelBox.SelectedIndex,
        ClipboardDefaultBox.IsChecked == true,
        (CredentialBackend)CredentialBackendBox.SelectedIndex,
        TimeSpan.FromMinutes(VaultTimeoutBox.Value));

    private async void OnLockVaultClicked(object sender, RoutedEventArgs args)
    {
        LockVaultButton.IsEnabled = false;
        try
        {
            await _lockVault();
            StatusText.Text = "保险库已锁定。";
        }
        catch
        {
            StatusText.Text = "无法锁定保险库。";
        }
        finally
        {
            LockVaultButton.IsEnabled = true;
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            _ = SelectedSettings;
            StatusText.Text = string.Empty;
        }
        catch (ArgumentException)
        {
            args.Cancel = true;
            StatusText.Text = "请输入 1 到 1440 之间的整数分钟。";
        }
    }
}
