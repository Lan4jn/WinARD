using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Desktop.Services;
using WinARD.Desktop.Views;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Domain.Sessions;
using WinARD.Infrastructure.Database;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Security.Secrets;
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
    private readonly Button _editButton = new();
    private readonly Button _connectButton = new();
    private readonly Button _disconnectButton = new();
    private readonly TextBlock _connectionStatus = new();
    private readonly ListView _connectionStages = new();
    private readonly ConnectionEditorService _connectionEditorService;
    private readonly ConnectionSessionController _sessionController;
    private readonly IUiDispatcher _dispatcher;
    private readonly VaultCredentialStoreSession _vaultSession;
    private readonly CredentialPromptService _credentialPromptService;
    private readonly SshHostKeyPromptService _hostKeyPromptService;
    private readonly TextBlock _detailName = new();
    private readonly TextBlock _detailSource = new();
    private readonly TextBlock _detailEndpoint = new();
    private readonly ConnectionErrorCard _connectionErrorCard = new();
    private readonly ISafeDiagnosticSink _diagnosticSink;
    private readonly DiagnosticExportService _diagnosticExportService;
    private readonly SecretRedactor _secretRedactor;
    private Task? _shutdownTask;
    private RemoteSessionWindow? _remoteSessionWindow;
    private SshHostKeyPromptRequest? _pendingHostKeyFailure;
    private bool _allowClose;
    private bool _sessionBusy;
    private int _disposed;

    public MainWindow(
        MainWindowViewModel viewModel,
        WinArdDatabase database,
        ConnectionEditorService connectionEditorService,
        ConnectionSessionController sessionController,
        IUiDispatcher dispatcher,
        VaultCredentialStoreSession vaultSession,
        CredentialPromptService credentialPromptService,
        SshHostKeyPromptService hostKeyPromptService,
        ISafeDiagnosticSink diagnosticSink,
        DiagnosticExportService diagnosticExportService,
        SecretRedactor secretRedactor)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _connectionEditorService = connectionEditorService ?? throw new ArgumentNullException(nameof(connectionEditorService));
        _sessionController = sessionController ?? throw new ArgumentNullException(nameof(sessionController));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _vaultSession = vaultSession ?? throw new ArgumentNullException(nameof(vaultSession));
        _credentialPromptService = credentialPromptService ?? throw new ArgumentNullException(nameof(credentialPromptService));
        _hostKeyPromptService = hostKeyPromptService ?? throw new ArgumentNullException(nameof(hostKeyPromptService));
        _diagnosticSink = diagnosticSink ?? throw new ArgumentNullException(nameof(diagnosticSink));
        _diagnosticExportService = diagnosticExportService ?? throw new ArgumentNullException(nameof(diagnosticExportService));
        _secretRedactor = secretRedactor ?? throw new ArgumentNullException(nameof(secretRedactor));
        InitializeComponent();
        BuildDeviceLibrary();
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Closing += OnClosing;
        Closed += OnClosed;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.AddDeviceRequested += OnAddDeviceRequested;
        ViewModel.EditDeviceRequested += OnEditDeviceRequested;
        _credentialPromptService.SetReferenceHandler(PromptForReferenceCredentialAsync);
        _hostKeyPromptService.SetHandler(PromptForHostKeyAsync);
        _sessionController.ProfileUpdated += OnConnectionProfileUpdated;
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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(add, "AddDeviceButton");
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
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _editButton.Content = "编辑设备";
        _editButton.Visibility = Visibility.Collapsed;
        _editButton.Click += (_, _) => ViewModel.EditDeviceCommand.Execute(null);
        actions.Children.Add(_editButton);
        _deleteButton.Content = "删除设备";
        _deleteButton.Visibility = Visibility.Collapsed;
        _deleteButton.Click += OnDeleteClicked;
        actions.Children.Add(_deleteButton);
        content.Children.Add(actions);

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
        _connectionErrorCard.ActionRequested += OnConnectionErrorActionRequested;
        _connectionErrorCard.IsActionEnabled = action =>
            action != ConnectionErrorActionKind.ReplaceHostKey || _pendingHostKeyFailure is not null;
        details.Children.Add(_connectionErrorCard);
        _connectionStatus.Text = "未连接。";
        _connectionStatus.TextWrapping = TextWrapping.Wrap;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            _connectionStatus,
            "ConnectionStatus");
        details.Children.Add(_connectionStatus);
        details.Children.Add(new TextBlock
        {
            Text = "阶段 / 用时 / 消息",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        _connectionStages.MaxHeight = 180;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            _connectionStages,
            "ConnectionStageResults");
        details.Children.Add(_connectionStages);
        var connectionActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        _connectButton.Content = "连接";
        _connectButton.IsEnabled = false;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            _connectButton,
            "ConnectButton");
        _connectButton.Click += (_, _) =>
            _ = _uiOperation.RunAsync(ConnectSelectedWithHandlingAsync, _shutdown.Token);
        connectionActions.Children.Add(_connectButton);
        _disconnectButton.Content = "断开连接";
        _disconnectButton.IsEnabled = false;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            _disconnectButton,
            "DisconnectButton");
        _disconnectButton.Click += (_, _) =>
            _ = _uiOperation.RunAsync(DisconnectWithHandlingAsync, _shutdown.Token);
        connectionActions.Children.Add(_disconnectButton);
        details.Children.Add(connectionActions);
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
        catch (Exception exception)
        {
            if (!_shutdown.IsCancellationRequested)
            {
                await ShowStartupErrorAsync();
                _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
                    "DEVICE_LIBRARY_INIT_FAILED",
                    Guid.NewGuid().ToString("N"),
                    "Device library initialization failed.",
                    Exception: exception));
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

    private void OnAddDeviceRequested(object? sender, EventArgs args) =>
        _ = _uiOperation.RunAsync(() => OpenConnectionEditorAsync(null), _shutdown.Token);

    private void OnEditDeviceRequested(ConnectionProfile profile) =>
        _ = _uiOperation.RunAsync(() => OpenConnectionEditorAsync(profile), _shutdown.Token);

    private async Task OpenConnectionEditorAsync(ConnectionProfile? profile)
    {
        using var hostKeyPrompt = new ConnectionEditorHostKeyPrompt();
        var viewModel = new ConnectionEditorViewModel(
            profile,
            _connectionEditorService.SaveWithResultAsync,
            (candidate, mode, secret, cancellationToken) =>
                _connectionEditorService.TestAsync(
                    candidate,
                    mode,
                    secret,
                    hostKeyPrompt,
                    cancellationToken));
        using var dialog = new ConnectionEditorDialog(
            viewModel,
            _vaultSession,
            hostKeyPrompt,
            _diagnosticSink,
            _diagnosticExportService,
            this,
            _secretRedactor)
        {
            XamlRoot = ShellRoot.XamlRoot,
        };
        _ = await dialog.ShowAsync();
        await dialog.WhenIdleAsync();
        if (dialog.SavedOutcome is { } saved)
        {
            await ViewModel.ApplySavedProfileAsync(
                saved.Profile,
                saved.Warning,
                _shutdown.Token);
        }
    }

    private async ValueTask<ISecret> PromptForReferenceCredentialAsync(
        CredentialPromptRequest request,
        CancellationToken cancellationToken)
    {
        var (header, title) = request.Purpose switch
        {
            CredentialPromptPurpose.MacPassword =>
                ($"“{request.DisplayName ?? "连接"}”的 Mac 密码", "输入连接凭据"),
            CredentialPromptPurpose.SshPassword => ("SSH 密码", "输入 SSH 凭据"),
            CredentialPromptPurpose.PrivateKeyPassphrase => ("SSH 私钥口令", "输入 SSH 凭据"),
            CredentialPromptPurpose.VaultMaster => ("凭据保险库主密码", "解锁凭据保险库"),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        return await PromptForSecretAsync(header, title, cancellationToken);
    }

    private async ValueTask<ISecret> PromptForSecretAsync(
        string header,
        string title,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var passwordBox = new PasswordBox
        {
            Header = header,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            passwordBox,
            "CredentialPromptPassword");
        var dialog = new ContentDialog
        {
            Title = title,
            Content = passwordBox,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ShellRoot.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (result != ContentDialogResult.Primary || passwordBox.Password.Length == 0)
        {
            passwordBox.Password = string.Empty;
            throw new OperationCanceledException("用户取消了凭据输入。", cancellationToken);
        }

        var bytes = new byte[Encoding.UTF8.GetByteCount(passwordBox.Password)];
        try
        {
            _ = Encoding.UTF8.GetBytes(passwordBox.Password, bytes);
            using var prompt = new CredentialPromptViewModel();
            prompt.Supply(SecretBuffer.CopyFrom(bytes));
            return prompt.Take();
        }
        finally
        {
            passwordBox.Password = string.Empty;
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async ValueTask<SshHostKeyPromptDecision> PromptForHostKeyAsync(
        SshHostKeyPromptRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = new StringBuilder()
            .AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"端点：{request.Endpoint.Host}:{request.Endpoint.Port}")
            .AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"算法：{request.Algorithm}");
        if (request.IsChanged && request.PreviousFingerprint is not null)
        {
            message.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"原 SHA256：{request.PreviousFingerprint}");
        }

        message.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"新 SHA256：{request.NewFingerprint}");
        var dialog = new ContentDialog
        {
            Title = request.IsChanged ? "SSH 主机密钥已更改" : "信任 SSH 主机密钥？",
            Content = new TextBlock
            {
                Text = message.ToString(),
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = request.IsChanged ? "替换并连接" : "信任并连接",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = ShellRoot.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (result != ContentDialogResult.Primary)
        {
            return SshHostKeyPromptDecision.Cancel;
        }

        return request.IsChanged
            ? SshHostKeyPromptDecision.Replace
            : SshHostKeyPromptDecision.Trust;
    }

    private async Task ConnectSelectedWithHandlingAsync()
    {
        var profile = ViewModel.SelectedDevice?.Profile;
        if (profile is null)
        {
            return;
        }

        await ConnectProfileWithHandlingAsync(profile);
    }

    private async Task ConnectProfileWithHandlingAsync(
        ConnectionProfile profile,
        ISshHostKeyPrompt? hostKeyPrompt = null,
        CancellationToken cancellationToken = default)
    {
        using var operationLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token,
            cancellationToken);
        var operationToken = operationLifetime.Token;
        operationToken.ThrowIfCancellationRequested();
        if (_sessionBusy || _sessionController.IsConnected)
        {
            return;
        }

        SetSessionBusy(true);
        if (hostKeyPrompt is null)
        {
            _pendingHostKeyFailure = null;
        }

        _connectionErrorCard.ViewModel = null;
        _connectionStatus.Text = "正在连接…";
        try
        {
            await _sessionController.ConnectAsync(profile, hostKeyPrompt, operationToken);
            var ownership = _sessionController.TransferConnectedSession();
            try
            {
                var remoteWindow = new RemoteSessionWindow(
                    ownership.Session,
                    ownership,
                    _dispatcher,
                    presenter: null,
                    diagnosticSink: _diagnosticSink,
                    diagnosticExportService: _diagnosticExportService,
                    retryRequested: retryToken => _uiOperation.RunAsync(
                        () => ConnectProfileWithHandlingAsync(
                            ownership.Profile,
                            cancellationToken: retryToken),
                        retryToken),
                    profile: ownership.Profile,
                    updateFrameRefreshPolicy:
                        _sessionController.UpdateConnectedFrameRefreshPolicyAsync,
                    updateQualityProfile:
                        _sessionController.UpdateConnectedQualityProfileAsync);
                remoteWindow.Closed += OnRemoteSessionWindowClosed;
                _remoteSessionWindow = remoteWindow;
                remoteWindow.Activate();
            }
            catch
            {
                await ownership.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
        }
        catch (ConnectionFailedException exception)
        {
            var error = exception.Result.Error ?? WinArdError.Create(
                ConnectionStage.Connecting,
                "UNEXPECTED_CONNECTION_ERROR",
                "连接失败。",
                Guid.NewGuid().ToString("N"));
            ShowConnectionError(error, exception.HostKeyFailure);
            _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
                error.Code,
                error.CorrelationId,
                "Connection attempt failed.",
                [new("stage", error.Stage.ToString())],
                exception));
        }
        catch (SessionAlreadyActiveException)
        {
            _connectionStatus.Text = "已有活动连接。";
        }
        catch (Exception exception)
        {
            var error = WinArdError.Create(
                ConnectionStage.Connecting,
                "UNEXPECTED_CONNECTION_ERROR",
                "连接失败。",
                Guid.NewGuid().ToString("N"));
            ShowConnectionError(error, hostKeyFailure: null);
            _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
                error.Code,
                error.CorrelationId,
                "Unexpected connection failure.",
                Exception: exception));
        }
        finally
        {
            SetSessionBusy(false);
            RefreshConnectionPresentation();
        }
    }

    private async Task DisconnectWithHandlingAsync()
    {
        if (_sessionBusy || !_sessionController.IsConnected)
        {
            return;
        }

        SetSessionBusy(true);
        _connectionStatus.Text = "正在断开…";
        try
        {
            if (_remoteSessionWindow is not null)
            {
                await _remoteSessionWindow.CloseSessionAsync();
            }
            else
            {
                await _sessionController.DisconnectAsync(_shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _connectionStatus.Text = "断开连接时出现错误。";
            _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
                "DISCONNECT_FAILED",
                Guid.NewGuid().ToString("N"),
                "Session disconnect failed.",
                Exception: exception));
        }
        finally
        {
            SetSessionBusy(false);
            RefreshConnectionPresentation();
        }
    }

    private void SetSessionBusy(bool value)
    {
        _sessionBusy = value;
        UpdateConnectionActions();
    }

    private void RefreshConnectionPresentation()
    {
        _connectionStatus.Text = _sessionController.StatusMessage.Length == 0
            ? (_sessionController.IsConnected ? "已连接。" : "未连接。")
            : _sessionController.StatusMessage;
        _connectionStages.ItemsSource = _sessionController.StageResults
            .Select(result => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{result.Stage} / {result.Duration.TotalMilliseconds:F0} ms / {result.Message}"))
            .ToArray();
        UpdateConnectionActions();
    }

    private void UpdateConnectionActions()
    {
        var hasSavedProfile = ViewModel.SelectedDevice?.Profile is not null;
        _connectButton.IsEnabled = hasSavedProfile && !_sessionBusy && !_sessionController.IsConnected;
        _disconnectButton.IsEnabled = !_sessionBusy && _sessionController.IsConnected;
        _editButton.IsEnabled = !_sessionBusy && !_sessionController.IsConnected;
        _deleteButton.IsEnabled = !_sessionBusy && !_sessionController.IsConnected && !ViewModel.IsDeleting;
    }

    private void ShowConnectionError(
        WinArdError error,
        SshHostKeyPromptRequest? hostKeyFailure)
    {
        _pendingHostKeyFailure = hostKeyFailure;
        _connectionStatus.Text = "连接失败。请使用下方操作继续。";
        _connectionErrorCard.ViewModel = ConnectionErrorViewModel.FromError(
            error,
            hostKeyFailure?.PreviousFingerprint,
            hostKeyFailure?.NewFingerprint);
    }

    private async void OnConnectionErrorActionRequested(
        object? sender,
        ConnectionErrorActionKind action)
    {
        try
        {
            switch (action)
            {
                case ConnectionErrorActionKind.Retry:
                    await _uiOperation.RunAsync(ConnectSelectedWithHandlingAsync, _shutdown.Token);
                    break;
                case ConnectionErrorActionKind.ReplaceHostKey:
                    if (ViewModel.SelectedDevice?.Profile is { } replaceProfile &&
                        Interlocked.Exchange(ref _pendingHostKeyFailure, null) is { } hostKeyFailure)
                    {
                        await _uiOperation.RunAsync(
                            () => ConnectProfileWithHandlingAsync(
                                replaceProfile,
                                new PreauthorizedHostKeyPrompt(hostKeyFailure)),
                            _shutdown.Token);
                        break;
                    }

                    throw new InvalidOperationException("SSH 主机密钥替换上下文已失效。");
                case ConnectionErrorActionKind.ReenterCredentials:
                case ConnectionErrorActionKind.UnlockVault:
                    if (ViewModel.SelectedDevice?.Profile is { } profile)
                    {
                        await _uiOperation.RunAsync(() => OpenConnectionEditorAsync(profile), _shutdown.Token);
                    }
                    break;
                case ConnectionErrorActionKind.OpenHelp:
                    await Windows.System.Launcher.LaunchUriAsync(
                        new Uri("https://support.apple.com/guide/mac-help/control-access-to-screen-recording-mchld6aa7d23/mac"));
                    break;
                case ConnectionErrorActionKind.CopyCorrelationId:
                    if (_connectionErrorCard.ViewModel is { } card)
                    {
                        var package = new DataPackage();
                        package.SetText(card.CorrelationId);
                        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                    }
                    break;
                case ConnectionErrorActionKind.ExportDiagnostics:
                    var path = await _diagnosticExportService.ExportAsync(
                        this,
                        CreateDiagnosticContext(),
                        _shutdown.Token);
                    if (path is not null)
                    {
                        _connectionStatus.Text = $"诊断已导出：{path}";
                    }
                    break;
                case ConnectionErrorActionKind.Disconnect:
                    await DisconnectWithHandlingAsync();
                    break;
                case ConnectionErrorActionKind.Cancel:
                    _pendingHostKeyFailure = null;
                    _connectionErrorCard.ViewModel = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            _connectionStatus.Text = $"操作失败。关联 ID：{correlationId}";
            _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
                "ERROR_ACTION_FAILED",
                correlationId,
                "Connection error action failed.",
                [new("action", action.ToString())],
                exception));
        }
    }

    private DiagnosticExportContext CreateDiagnosticContext()
    {
        var profile = ViewModel.SelectedDevice?.Profile;
        var profiles = profile is null
            ? Array.Empty<DiagnosticProfileSummary>()
            : new[]
            {
                new DiagnosticProfileSummary(
                    profile.DisplayName,
                    profile.Host,
                    profile.Port,
                    profile.MacUsername,
                    "RFB 3.x",
                    "ARD-30"),
            };
        return DesktopDiagnosticContextFactory.Create(profiles);
    }

    private void OnConnectionProfileUpdated(ConnectionProfile profile) =>
        _ = _uiOperation.RunAsync(
            () => ViewModel.ApplySavedProfileAsync(profile, _shutdown.Token),
            _shutdown.Token);

    private void OnRemoteSessionWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is RemoteSessionWindow window)
        {
            window.Closed -= OnRemoteSessionWindowClosed;
            if (ReferenceEquals(_remoteSessionWindow, window))
            {
                _remoteSessionWindow = null;
            }
        }

        RefreshConnectionPresentation();
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
            UpdateConnectionActions();
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
            _editButton.Visibility = Visibility.Collapsed;
            UpdateConnectionActions();
            return;
        }

        _detailName.Text = item.DisplayName;
        _detailSource.Text = item.SourceLabel;
        _detailEndpoint.Text = $"地址  {item.Endpoint}";
        _detailsHost.Visibility = Visibility.Visible;
        _emptyState.Visibility = Visibility.Collapsed;
        _deleteButton.Visibility = item.Profile is null ? Visibility.Collapsed : Visibility.Visible;
        _editButton.Visibility = item.Profile is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateConnectionActions();
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
        if (_remoteSessionWindow is not null)
        {
            await _remoteSessionWindow.CloseSessionAsync();
        }

        await _sessionController.DisposeAsync();
        await _uiOperation.RunAsync(() => ViewModel.DisposeAsync().AsTask());
        await _uiOperation.RunAsync(() => _database.DisposeAsync().AsTask());
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.AddDeviceRequested -= OnAddDeviceRequested;
        ViewModel.EditDeviceRequested -= OnEditDeviceRequested;
        _credentialPromptService.ClearReferenceHandler();
        _hostKeyPromptService.ClearHandler();
        _sessionController.ProfileUpdated -= OnConnectionProfileUpdated;
        _connectionErrorCard.ActionRequested -= OnConnectionErrorActionRequested;
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
