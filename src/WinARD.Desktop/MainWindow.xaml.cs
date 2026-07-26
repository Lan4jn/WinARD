using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Infrastructure.Database;
using WinRT.Interop;

namespace WinARD.Desktop;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly WinArdDatabase _database;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly AsyncUiOperation _uiOperation = new();
    private readonly AppWindow _appWindow;
    private readonly Task _initializationTask;
    private readonly Grid _detailsHost = new();
    private readonly StackPanel _emptyState = new();
    private readonly Button _deleteButton = new();
    private readonly TextBlock _detailName = new();
    private readonly TextBlock _detailSource = new();
    private readonly TextBlock _detailEndpoint = new();
    private Task? _shutdownTask;
    private bool _allowClose;
    private int _disposed;

    public MainWindow(MainWindowViewModel viewModel, WinArdDatabase database)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        InitializeComponent();
        BuildDeviceLibrary();
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Closing += OnClosing;
        Closed += OnClosed;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ResizeWindow();
        _initializationTask = _uiOperation.RunAsync(InitializeWithErrorHandlingAsync, _shutdown.Token);
    }

    public MainWindowViewModel ViewModel { get; }

    private void BuildDeviceLibrary()
    {
        ShellRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        ShellRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = new Grid
        {
            Padding = new Thickness(20, 18, 18, 18),
            RowSpacing = 12,
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        foreach (var height in new[]
        {
            GridLength.Auto, GridLength.Auto, GridLength.Auto,
            new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto,
            new GridLength(0.7, GridUnitType.Star), GridLength.Auto,
        })
        {
            sidebar.RowDefinitions.Add(new RowDefinition { Height = height });
        }

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(new TextBlock { Text = "设备", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "连接到你的 Mac", Opacity = 0.68 });
        header.Children.Add(heading);
        var add = new Button { Content = "添加设备", HorizontalAlignment = HorizontalAlignment.Right };
        add.Click += (_, _) => ViewModel.AddDeviceCommand.Execute(null);
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        sidebar.Children.Add(header);

        var search = new AutoSuggestBox { PlaceholderText = "搜索设备" };
        search.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                ViewModel.SearchText = search.Text;
            }
        };
        Grid.SetRow(search, 1);
        sidebar.Children.Add(search);

        AddSidebarLabel(sidebar, "已保存", 2);
        var saved = CreateDeviceList(ViewModel.SavedDevices);
        Grid.SetRow(saved, 3);
        sidebar.Children.Add(saved);
        var separator = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Opacity = 0.35,
        };
        Grid.SetRow(separator, 4);
        sidebar.Children.Add(separator);
        AddSidebarLabel(sidebar, "附近发现", 5);
        var discovered = CreateDeviceList(ViewModel.DiscoveredDevices);
        Grid.SetRow(discovered, 6);
        sidebar.Children.Add(discovered);
        var status = new TextBlock { FontSize = 12, Opacity = 0.68, TextWrapping = TextWrapping.Wrap };
        status.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = ViewModel,
            Path = new PropertyPath(nameof(MainWindowViewModel.StatusMessage)),
        });
        Grid.SetRow(status, 7);
        sidebar.Children.Add(status);
        ShellRoot.Children.Add(sidebar);

        var content = new Grid { Padding = new Thickness(36) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(content, 1);
        _deleteButton.Content = "删除设备";
        _deleteButton.HorizontalAlignment = HorizontalAlignment.Right;
        _deleteButton.Visibility = Visibility.Collapsed;
        _deleteButton.Click += OnDeleteClicked;
        content.Children.Add(_deleteButton);

        var body = new Grid();
        Grid.SetRow(body, 1);
        _emptyState.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyState.VerticalAlignment = VerticalAlignment.Center;
        _emptyState.Spacing = 12;
        _emptyState.Children.Add(new FontIcon { Glyph = "\uE977", FontSize = 44 });
        _emptyState.Children.Add(new TextBlock
        {
            Text = "选择一台设备",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _emptyState.Children.Add(new TextBlock
        {
            Text = "从已保存或附近发现的设备中选择，查看连接详情。",
            MaxWidth = 420,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68,
        });
        var emptyAdd = new Button { Content = "添加设备", HorizontalAlignment = HorizontalAlignment.Center };
        emptyAdd.Click += (_, _) => ViewModel.AddDeviceCommand.Execute(null);
        _emptyState.Children.Add(emptyAdd);
        body.Children.Add(_emptyState);

        _detailsHost.Visibility = Visibility.Collapsed;
        _detailsHost.MaxWidth = 720;
        _detailsHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _detailsHost.VerticalAlignment = VerticalAlignment.Center;
        var card = new Border
        {
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(28),
        };
        var details = new StackPanel { Spacing = 16 };
        _detailName.FontSize = 28;
        _detailName.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _detailSource.Opacity = 0.62;
        _detailEndpoint.FontSize = 16;
        details.Children.Add(_detailName);
        details.Children.Add(_detailSource);
        details.Children.Add(_detailEndpoint);
        details.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "设备库 MVP",
            Message = "远程画面与交互连接将在后续阶段提供。",
        });
        details.Children.Add(new Button { Content = "连接", IsEnabled = false });
        card.Child = details;
        _detailsHost.Children.Add(card);
        body.Children.Add(_detailsHost);
        content.Children.Add(body);
        ShellRoot.Children.Add(content);
    }

    private ListView CreateDeviceList(System.Collections.IEnumerable items)
    {
        var list = new ListView
        {
            ItemsSource = items,
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.Single,
        };
        list.ItemClick += OnDeviceItemClick;
        return list;
    }

    private static void AddSidebarLabel(Grid sidebar, string text, int row)
    {
        var label = new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Grid.SetRow(label, row);
        sidebar.Children.Add(label);
    }

    private async Task InitializeWithErrorHandlingAsync()
    {
        try
        {
            await Task.Run(() => _database.InitializeAsync(_shutdown.Token), _shutdown.Token);
            await ViewModel.InitializeAsync(_shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_shutdown.IsCancellationRequested)
            {
                await ShowStartupErrorAsync();
            }
        }
    }

    private void OnDeviceItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not DeviceItemViewModel item)
        {
            return;
        }

        ViewModel.SelectedDevice = item;
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs args)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _uiOperation.RunAsync(DeleteSelectedFromDialogAsync, _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
        }
    }

    private async Task DeleteSelectedFromDialogAsync()
    {
        var item = ViewModel.SelectedDevice;
        if (item?.Profile is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = $"删除“{item.DisplayName}”？",
            Content = "你可以只删除设备，也可以同时删除 Windows 凭据管理器中的关联凭据。",
            PrimaryButtonText = "删除设备和凭据",
            SecondaryButtonText = "仅删除设备",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = ShellRoot.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        bool? deleteCredential = result switch
        {
            ContentDialogResult.Primary => true,
            ContentDialogResult.Secondary => false,
            _ => null,
        };
        await ViewModel.DeleteSelectedAsync(deleteCredential, _shutdown.Token);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.IsDeleting))
        {
            _deleteButton.IsEnabled = !ViewModel.IsDeleting;
            return;
        }

        if (args.PropertyName != nameof(MainWindowViewModel.SelectedDevice))
        {
            return;
        }

        var item = ViewModel.SelectedDevice;
        if (item is null)
        {
            _detailsHost.Visibility = Visibility.Collapsed;
            _emptyState.Visibility = Visibility.Visible;
            _deleteButton.Visibility = Visibility.Collapsed;
            return;
        }

        _detailName.Text = item.DisplayName;
        _detailSource.Text = item.SourceLabel;
        _detailEndpoint.Text = $"地址  {item.Endpoint}";
        _detailsHost.Visibility = Visibility.Visible;
        _emptyState.Visibility = Visibility.Collapsed;
        _deleteButton.Visibility = item.Profile is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task ShowStartupErrorAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "无法载入设备库",
            Content = "WinARD 无法初始化本地设备数据。请关闭应用后重试。",
            CloseButtonText = "关闭",
            XamlRoot = ShellRoot.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        _shutdownTask ??= ShutdownAndCloseAsync();
    }

    private async Task ShutdownAndCloseAsync()
    {
        _shutdown.Cancel();
        await _initializationTask;
        await _uiOperation.WhenIdleAsync();
        await _uiOperation.RunAsync(() => ViewModel.DisposeAsync().AsTask());
        await _uiOperation.RunAsync(() => _database.DisposeAsync().AsTask());
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _allowClose = true;
        try
        {
            Close();
        }
        catch (Exception exception)
        {
            _uiOperation.Report(exception);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _appWindow.Closing -= OnClosing;
        Dispose();
    }

    private void ResizeWindow()
    {
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
