using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinARD.Desktop.ViewModels;

namespace WinARD.Desktop.Views;

public sealed partial class ConnectionErrorCard : UserControl
{
    private ConnectionErrorViewModel? _viewModel;

    public ConnectionErrorCard()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    public ConnectionErrorViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Render();
        }
    }

    public event EventHandler<ConnectionErrorActionKind>? ActionRequested;

    private void Render()
    {
        ActionPanel.Items.Clear();
        if (_viewModel is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        TitleText.Text = _viewModel.Title;
        SummaryText.Text = _viewModel.Summary;
        CorrelationText.Text = _viewModel.CorrelationId;
        foreach (var action in _viewModel.Actions)
        {
            var button = new Button { Content = action.Label, Tag = action.Kind };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                button,
                $"ConnectionErrorAction_{action.Kind}");
            button.Click += OnActionClicked;
            ActionPanel.Items.Add(button);
        }

        Visibility = Visibility.Visible;
    }

    private void OnActionClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: ConnectionErrorActionKind kind })
        {
            ActionRequested?.Invoke(this, kind);
        }
    }
}
