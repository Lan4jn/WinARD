using WinARD.Desktop.ViewModels;

namespace WinARD.Desktop.Views;

public sealed record ConnectionEditorDialogState(
    bool IsBusy,
    bool SaveEnabled,
    bool TestEnabled,
    string StatusMessage,
    bool ShowSshSecret,
    bool ShowPrivateKeyPath,
    string SshSecretTitle);

public static class ConnectionEditorDialogStatePresenter
{
    public static ConnectionEditorDialogState Present(ConnectionEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new ConnectionEditorDialogState(
            viewModel.IsBusy,
            viewModel.SaveCommand.CanExecute(null),
            viewModel.TestConnectionCommand.CanExecute(null),
            viewModel.StatusMessage,
            ShowSshSecret: true,
            viewModel.SshAuthenticationMode == SshAuthenticationMode.PrivateKey,
            viewModel.SshAuthenticationMode == SshAuthenticationMode.PrivateKey
                ? "私钥口令（可选）"
                : "SSH 密码");
    }
}
