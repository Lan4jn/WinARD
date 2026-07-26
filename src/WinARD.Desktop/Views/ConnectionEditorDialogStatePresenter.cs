using WinARD.Desktop.ViewModels;

namespace WinARD.Desktop.Views;

public sealed record ConnectionEditorDialogState(
    bool IsBusy,
    bool SaveEnabled,
    bool TestEnabled,
    string StatusMessage);

public static class ConnectionEditorDialogStatePresenter
{
    public static ConnectionEditorDialogState Present(ConnectionEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new ConnectionEditorDialogState(
            viewModel.IsBusy,
            viewModel.SaveCommand.CanExecute(null),
            viewModel.TestConnectionCommand.CanExecute(null),
            viewModel.StatusMessage);
    }
}
