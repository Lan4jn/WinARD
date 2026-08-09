using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using WinARD.Application.Ports;
using WinARD.Desktop.Clipboard;
using WinARD.Desktop.Input;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Services;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Infrastructure.Diagnostics;
using WinRT.Interop;

namespace WinARD.Desktop.Views;

public sealed partial class RemoteSessionWindow : Window, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly WindowsClipboardBridge _clipboardBridge;
    private readonly RemoteInputOperationRunner _inputOperations;
    private readonly RemoteInputDiagnosticTracker _inputDiagnostics;
    private readonly RemoteCursorVisibilityController _cursorVisibility;
    private readonly RemoteTextInputBuffer _textInput = new();
    private readonly AppWindow _appWindow;
    private readonly DiagnosticExportService? _diagnosticExportService;
    private readonly RemoteSessionDiagnosticExportState _diagnosticExportState;
    private readonly ConnectionErrorActionHandler _errorActionHandler;
    private readonly RemoteSessionWindowLifecycle _windowLifecycle;
    private readonly KeyEventHandler _keyDownHandler;
    private readonly KeyEventHandler _keyUpHandler;
    private readonly PointerEventHandler _pointerPressedHandler;
    private readonly PointerEventHandler _pointerMovedHandler;
    private readonly PointerEventHandler _pointerReleasedHandler;
    private readonly PointerEventHandler _pointerCanceledHandler;
    private readonly PointerEventHandler _pointerWheelChangedHandler;
    private RemoteFramebufferSize _remoteSize;
    private ViewportScaleMode _scaleMode = ViewportScaleMode.Fit;
    private byte _pointerMask;
    private RemotePoint _lastPointer;
    private int _closingStarted;
    private bool _fullscreen;
    private bool _allowClose;

    public RemoteSessionWindow(
        IRemoteSessionRuntime session,
        IAsyncDisposable ownership,
        IUiDispatcher dispatcher,
        IFramePresenter? presenter = null,
        ISafeDiagnosticSink? diagnosticSink = null,
        DiagnosticExportService? diagnosticExportService = null,
        ConnectionErrorViewModel? initialError = null,
        Func<CancellationToken, Task>? retryRequested = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ownership);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _diagnosticExportService = diagnosticExportService;
        _inputDiagnostics = new RemoteInputDiagnosticTracker(diagnosticSink);
        _diagnosticExportState = new RemoteSessionDiagnosticExportState(
            serviceAvailable: diagnosticExportService is not null);
        InitializeComponent();
        RefreshDiagnosticExportState();
        _remoteSize = session.FramebufferSize;
        presenter ??= new D3DFramePresenter(FramePanel);
        _clipboardBridge = new WindowsClipboardBridge(dispatcher, session.SendClipboardTextAsync);
        ViewModel = new RemoteSessionViewModel(
            session,
            ownership,
            presenter,
            dispatcher,
            _clipboardBridge,
            diagnosticSink);
        _windowLifecycle = new RemoteSessionWindowLifecycle(
            ViewModel.Completion,
            () => ViewModel.Error is not null,
            StopSessionCoreAsync,
            CloseWindowCoreAsync,
            retryRequested,
            BeginClosingDiagnostics);
        var handlers = new Dictionary<ConnectionErrorActionKind, Func<CancellationToken, Task>>
        {
            [ConnectionErrorActionKind.CopyCorrelationId] = CopyCorrelationIdAsync,
            [ConnectionErrorActionKind.Disconnect] = _ => _windowLifecycle.DisconnectAsync(),
            [ConnectionErrorActionKind.Cancel] = _ => _windowLifecycle.DisconnectAsync(),
        };
        if (_diagnosticExportService is not null)
        {
            handlers[ConnectionErrorActionKind.ExportDiagnostics] = ExportDiagnosticsAsync;
        }
        if (_windowLifecycle.CanRetry)
        {
            handlers[ConnectionErrorActionKind.Retry] = _windowLifecycle.RetryAsync;
        }

        _errorActionHandler = new ConnectionErrorActionHandler(handlers);
        SessionErrorCard.IsActionEnabled = IsErrorActionEnabled;
        SessionErrorCard.ActionRequested += OnErrorActionRequested;
        SessionErrorCard.ViewModel = initialError;
        _inputOperations = new RemoteInputOperationRunner(
            dispatcher,
            ViewModel.ReportInputFailureAsync,
            _windowLifecycle.StopSessionAsync,
            IsInputClosing,
            ViewModel.ObserveInputFailure);
        _cursorVisibility = new RemoteCursorVisibilityController(
            InputSurface.SetHostCursorHidden,
            visible => RemoteCursorOverlay.Visibility = visible
                ? Visibility.Visible
                : Visibility.Collapsed);
        StatusText.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = ViewModel,
            Path = new PropertyPath(nameof(RemoteSessionViewModel.StatusMessage)),
        });
        UpdateConnectionQualityVisual();

        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        _appWindow.Closing += OnClosing;
        Closed += OnClosed;
        Activated += OnActivated;
        _keyDownHandler = OnKeyDown;
        _keyUpHandler = OnKeyUp;
        _pointerPressedHandler = OnPointerPressed;
        _pointerMovedHandler = OnPointerMoved;
        _pointerReleasedHandler = OnPointerReleased;
        _pointerCanceledHandler = OnPointerCanceled;
        _pointerWheelChangedHandler = OnPointerWheelChanged;
        InputSurface.AddHandler(
            UIElement.KeyDownEvent,
            _keyDownHandler,
            handledEventsToo: true);
        InputSurface.AddHandler(
            UIElement.KeyUpEvent,
            _keyUpHandler,
            handledEventsToo: true);
        InputSurface.CharacterReceived += OnCharacterReceived;
        FrameSurface.AddHandler(
            UIElement.PointerPressedEvent,
            _pointerPressedHandler,
            handledEventsToo: true);
        FrameSurface.AddHandler(
            UIElement.PointerMovedEvent,
            _pointerMovedHandler,
            handledEventsToo: true);
        FrameSurface.AddHandler(
            UIElement.PointerReleasedEvent,
            _pointerReleasedHandler,
            handledEventsToo: true);
        FrameSurface.AddHandler(
            UIElement.PointerCanceledEvent,
            _pointerCanceledHandler,
            handledEventsToo: true);
        FrameSurface.AddHandler(
            UIElement.PointerWheelChangedEvent,
            _pointerWheelChangedHandler,
            handledEventsToo: true);
        FramePanel.Loaded += OnFramePanelLoaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        FrameScrollViewer.SizeChanged += (_, _) => UpdateFrameSizing();
        UpdateFrameSizing();
    }

    public RemoteSessionViewModel ViewModel { get; }

    public Task CloseSessionAsync()
        => _windowLifecycle.DisconnectAsync();

    public ValueTask DisposeAsync() => new(CloseSessionAsync());

    private void OnFramePanelLoaded(object sender, RoutedEventArgs args)
    {
        FramePanel.Loaded -= OnFramePanelLoaded;
        _ = ViewModel.StartAsync(_lifetime.Token);
        _ = ObserveFailureAsync(_windowLifecycle.ObserveCompletionAsync(_lifetime.Token));
        _ = ObserveFailureAsync(ObserveSmokePointerProbeAsync());
    }

    private void OnFitClicked(object sender, RoutedEventArgs args)
    {
        _scaleMode = ViewportScaleMode.Fit;
        UpdateFrameSizing();
    }

    private void OnActualSizeClicked(object sender, RoutedEventArgs args)
    {
        _scaleMode = ViewportScaleMode.ActualSize;
        UpdateFrameSizing();
    }

    private void OnSecureAttentionClicked(object sender, RoutedEventArgs args) =>
        _ = _inputOperations.RunAsync(
            () => ViewModel.SendSecureAttentionSequenceAsync(_lifetime.Token).AsTask());

    private void OnClipboardClicked(object sender, RoutedEventArgs args) =>
        _clipboardBridge.IsEnabled = ClipboardButton.IsChecked == true;

    private async void OnExportDiagnosticsClicked(object sender, RoutedEventArgs args)
    {
        if (_diagnosticExportService is null || !_diagnosticExportState.TryBeginExport())
        {
            return;
        }

        RefreshDiagnosticExportState();
        try
        {
            await ExportDiagnosticsAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            StatusText.Text = "诊断导出失败。";
        }
        finally
        {
            _diagnosticExportState.CompleteExport();
            RefreshDiagnosticExportState();
        }
    }

    private void RefreshDiagnosticExportState() =>
        ExportDiagnosticsButton.IsEnabled = _diagnosticExportState.IsEnabled;

    private void BeginClosingDiagnostics()
    {
        _ = Interlocked.Exchange(ref _closingStarted, 1);
        _diagnosticExportState.BeginClosing();
        _ = ObserveFailureAsync(_dispatcher.InvokeAsync(
            RefreshDiagnosticExportState,
            CancellationToken.None));
    }

    private void OnFullscreenClicked(object sender, RoutedEventArgs args)
    {
        _fullscreen = !_fullscreen;
        _appWindow.SetPresenter(
            _fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
    }

    private void OnDisconnectClicked(object sender, RoutedEventArgs args) =>
        _ = CloseSessionAsync();

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (IsInputClosing())
        {
            RecordKeyboardDropped(RemoteInputDropReason.SessionClosing);
            return;
        }

        if (!CurrentTransform().IsValid)
        {
            RecordKeyboardDropped(RemoteInputDropReason.InvalidTransform);
            return;
        }

        RecordKeyboardCaptured();
        args.Handled = true;
        _textInput.OnPhysicalKeyDown(
            args.Key,
            checked((int)args.KeyStatus.ScanCode),
            args.KeyStatus.IsExtendedKey);
        _ = _inputOperations.RunAsync(
            () => ViewModel.KeyDownAsync(
                args.Key,
                checked((int)args.KeyStatus.ScanCode),
                args.KeyStatus.IsExtendedKey,
                text: null,
                _lifetime.Token).AsTask());
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (IsInputClosing())
        {
            RecordKeyboardDropped(RemoteInputDropReason.SessionClosing);
            return;
        }

        RecordKeyboardCaptured();
        args.Handled = true;
        _textInput.OnPhysicalKeyUp(
            args.Key,
            checked((int)args.KeyStatus.ScanCode),
            args.KeyStatus.IsExtendedKey);
        _ = _inputOperations.RunAsync(
            () => ViewModel.KeyUpAsync(
                args.Key,
                checked((int)args.KeyStatus.ScanCode),
                args.KeyStatus.IsExtendedKey,
                text: null,
                _lifetime.Token).AsTask());
    }

    private void OnCharacterReceived(object sender, CharacterReceivedRoutedEventArgs args)
    {
        if (IsInputClosing())
        {
            RecordKeyboardDropped(RemoteInputDropReason.SessionClosing);
            return;
        }

        RecordKeyboardCaptured();
        args.Handled = true;
        var text = _textInput.AcceptCharacter(args.Character);
        if (text is null)
        {
            return;
        }

        _ = _inputOperations.RunAsync(
            () => ViewModel.TextInputAsync(text, _lifetime.Token).AsTask());
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        _ = InputSurface.Focus(FocusState.Pointer);
        _ = FrameSurface.CapturePointer(args.Pointer);
        QueuePointerSend(args);
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args) =>
        QueuePointerSend(args);

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        FrameSurface.ReleasePointerCapture(args.Pointer);
        QueuePointerSend(args);
        args.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (IsInputClosing())
        {
            RecordPointerDropped(RemoteInputDropReason.SessionClosing);
            return;
        }

        RecordPointerCaptured();
        _ = _inputOperations.RunAsync(() => ReleaseInputAsync(_lifetime.Token));
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        QueueWheelSend(args);
        args.Handled = true;
    }

    private void QueueWheelSend(PointerRoutedEventArgs args)
    {
        if (IsInputClosing())
        {
            RecordPointerDropped(RemoteInputDropReason.SessionClosing);
            return;
        }

        if (!TryGetRemotePoint(args, out var point))
        {
            RecordPointerDropped(RemoteInputDropReason.InvalidTransform);
            return;
        }

        RecordPointerCaptured();
        var properties = args.GetCurrentPoint(FrameSurface).Properties;
        var baseMask = WindowsInputMapper.ToPointerMask(ToButtons(properties));
        var wheelMask = WindowsInputMapper.WithWheel(baseMask, properties.MouseWheelDelta);
        _ = RemotePointerDispatch.RunAsync(
            () => UpdateLocalPointerState(point, baseMask),
            _inputOperations,
            async () =>
            {
                await ViewModel.SendPointerAsync(wheelMask, point, _lifetime.Token);
                await ViewModel.SendPointerAsync(baseMask, point, _lifetime.Token);
            });
    }

    private void QueuePointerSend(PointerRoutedEventArgs args)
    {
        if (IsInputClosing())
        {
            RecordPointerDropped(RemoteInputDropReason.SessionClosing);
            return;
        }

        if (!TryGetRemotePoint(args, out var point))
        {
            RecordPointerDropped(RemoteInputDropReason.InvalidTransform);
            return;
        }

        RecordPointerCaptured();
        var pointerMask = WindowsInputMapper.ToPointerMask(
            ToButtons(args.GetCurrentPoint(FrameSurface).Properties));
        _ = RemotePointerDispatch.RunAsync(
            () => UpdateLocalPointerState(point, pointerMask),
            _inputOperations,
            () => ViewModel.SendPointerAsync(
                pointerMask,
                point,
                _lifetime.Token).AsTask());
    }

    private void RecordKeyboardCaptured() => _inputDiagnostics.Record(
        RemoteInputKind.Keyboard,
        RemoteInputBoundary.UiCaptured);

    private void RecordPointerCaptured() => _inputDiagnostics.Record(
        RemoteInputKind.Pointer,
        RemoteInputBoundary.UiCaptured);

    private void RecordKeyboardDropped(RemoteInputDropReason reason) =>
        _inputDiagnostics.RecordDropped(RemoteInputKind.Keyboard, reason);

    private void RecordPointerDropped(RemoteInputDropReason reason) =>
        _inputDiagnostics.RecordDropped(RemoteInputKind.Pointer, reason);

    private bool IsInputClosing() =>
        Volatile.Read(ref _closingStarted) != 0 ||
        _lifetime.IsCancellationRequested ||
        _windowLifecycle.IsSessionStopped;

    private void UpdateLocalPointerState(RemotePoint point, byte pointerMask)
    {
        _lastPointer = point;
        _pointerMask = pointerMask;
        UpdateCursorPosition();
    }

    private bool TryGetRemotePoint(PointerRoutedEventArgs args, out RemotePoint point)
    {
        var position = args.GetCurrentPoint(ViewportHost).Position;
        return CurrentTransform().TryMapToRemote(position.X, position.Y, out point);
    }

    private ViewportTransform CurrentTransform()
    {
        var viewportWidth = EffectiveViewportWidth();
        var viewportHeight = EffectiveViewportHeight();
        return ViewportTransform.Create(
            _remoteSize.Width,
            _remoteSize.Height,
            viewportWidth,
            viewportHeight,
            RootGrid.XamlRoot?.RasterizationScale ?? 1,
            _scaleMode,
            FrameScrollViewer.HorizontalOffset,
            FrameScrollViewer.VerticalOffset);
    }

    private static RemotePointerButtons ToButtons(PointerPointProperties properties)
    {
        var buttons = RemotePointerButtons.None;
        if (properties.IsLeftButtonPressed) buttons |= RemotePointerButtons.Left;
        if (properties.IsMiddleButtonPressed) buttons |= RemotePointerButtons.Middle;
        if (properties.IsRightButtonPressed) buttons |= RemotePointerButtons.Right;
        return buttons;
    }

    private void UpdateFrameSizing()
    {
        var dpi = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        var layout = ViewportLayout.Create(
            _remoteSize.Width,
            _remoteSize.Height,
            EffectiveViewportWidth(),
            EffectiveViewportHeight(),
            dpi,
            _scaleMode,
            FrameScrollViewer.HorizontalOffset,
            FrameScrollViewer.VerticalOffset);
        if (!layout.IsValid)
        {
            return;
        }

        FrameSurface.Width = layout.SurfaceWidth;
        FrameSurface.Height = layout.SurfaceHeight;
        FrameViewbox.Width = layout.SurfaceWidth;
        FrameViewbox.Height = layout.SurfaceHeight;
        var scale = _scaleMode == ViewportScaleMode.ActualSize ? dpi : 1;
        FramePanel.Width = _remoteSize.Width / scale;
        FramePanel.Height = _remoteSize.Height / scale;
        FrameViewbox.Stretch = _scaleMode == ViewportScaleMode.Fit
            ? Microsoft.UI.Xaml.Media.Stretch.Uniform
            : Microsoft.UI.Xaml.Media.Stretch.None;
        FrameViewbox.HorizontalAlignment = _scaleMode == ViewportScaleMode.Fit
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
        FrameViewbox.VerticalAlignment = _scaleMode == ViewportScaleMode.Fit
            ? VerticalAlignment.Stretch
            : VerticalAlignment.Top;
        FrameScrollViewer.HorizontalScrollMode = layout.IsScrollingEnabled
            ? ScrollMode.Enabled
            : ScrollMode.Disabled;
        FrameScrollViewer.VerticalScrollMode = layout.IsScrollingEnabled
            ? ScrollMode.Enabled
            : ScrollMode.Disabled;
        FrameScrollViewer.HorizontalScrollBarVisibility = layout.IsScrollingEnabled
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
        FrameScrollViewer.VerticalScrollBarVisibility = layout.IsScrollingEnabled
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
        _ = FrameScrollViewer.ChangeView(
            layout.ScrollOffsetX,
            layout.ScrollOffsetY,
            null,
            disableAnimation: true);
        FitButton.IsEnabled = _scaleMode != ViewportScaleMode.Fit;
        ActualSizeButton.IsEnabled = _scaleMode != ViewportScaleMode.ActualSize;
        UpdateCursorPosition();
    }

    private double EffectiveViewportWidth() =>
        FrameScrollViewer.ViewportWidth > 0
            ? FrameScrollViewer.ViewportWidth
            : ViewportHost.ActualWidth;

    private double EffectiveViewportHeight() =>
        FrameScrollViewer.ViewportHeight > 0
            ? FrameScrollViewer.ViewportHeight
            : ViewportHost.ActualHeight;

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _ = _inputOperations.RunAsync(() => ReleaseInputAsync(_lifetime.Token));
        }
    }

    private async Task ReleaseInputAsync(CancellationToken cancellationToken)
    {
        await ViewModel.ReleaseInputAsync(cancellationToken);
        if (_pointerMask != 0)
        {
            _pointerMask = 0;
            await ViewModel.SendPointerAsync(0, _lastPointer, cancellationToken);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RemoteSessionViewModel.FramebufferSize))
        {
            _remoteSize = ViewModel.FramebufferSize;
            UpdateFrameSizing();
        }
        else if (args.PropertyName == nameof(RemoteSessionViewModel.RemoteCursor))
        {
            UpdateRemoteCursor();
        }
        else if (args.PropertyName == nameof(RemoteSessionViewModel.StatusMessage))
        {
            WriteSmokeMarker("WINARD_REMOTE_SMOKE_STATUS_MARKER", ViewModel.StatusMessage);
        }
        else if (args.PropertyName == nameof(RemoteSessionViewModel.ConnectionQuality))
        {
            UpdateConnectionQualityVisual();
        }
        else if (args.PropertyName == nameof(RemoteSessionViewModel.Error))
        {
            var error = ViewModel.Error;
            SessionErrorCard.ViewModel = error is null
                ? null
                : ConnectionErrorViewModel.FromError(error);
            if (error is not null)
            {
                WriteSmokeMarker("WINARD_REMOTE_SMOKE_ERROR_MARKER", error.Code);
                TriggerSmokeErrorAction();
            }
        }
    }

    private void UpdateConnectionQualityVisual()
    {
        var quality = ViewModel.ConnectionQuality;
        var color = quality.Level switch
        {
            ConnectionQualityLevel.Good => Microsoft.UI.Colors.LimeGreen,
            ConnectionQualityLevel.Fair => Microsoft.UI.Colors.Goldenrod,
            ConnectionQualityLevel.Poor or ConnectionQualityLevel.Disconnected =>
                Microsoft.UI.Colors.IndianRed,
            _ => Microsoft.UI.Colors.Gray,
        };
        QualityIndicator.Fill = new SolidColorBrush(color);
        QualityText.Text = quality.DisplayText;
        AutomationProperties.SetName(QualityPanel, $"连接质量：{quality.DisplayText}");
        AutomationProperties.SetName(QualityIndicator, $"连接质量：{quality.DisplayText}");
    }

    private void TriggerSmokeErrorAction()
    {
        var value = Environment.GetEnvironmentVariable(
            "WINARD_REMOTE_SMOKE_ERROR_ACTION");
        if (!Enum.TryParse<ConnectionErrorActionKind>(
                value,
                ignoreCase: true,
                out var action) ||
            !_errorActionHandler.CanHandle(action))
        {
            return;
        }

        var operation = _errorActionHandler.HandleAsync(action, CancellationToken.None);
        RefreshErrorActionState();
        _ = ObserveFailureAsync(operation);
    }

    private async void OnErrorActionRequested(object? sender, ConnectionErrorActionKind action)
    {
        try
        {
            var operation = _errorActionHandler.HandleAsync(action, _lifetime.Token);
            RefreshErrorActionState();
            await operation;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            StatusText.Text = "错误操作未能完成。";
        }
    }

    private bool IsErrorActionEnabled(ConnectionErrorActionKind action) => action switch
    {
        ConnectionErrorActionKind.Retry =>
            _errorActionHandler.CanHandle(action) && _windowLifecycle.CanRetry,
        ConnectionErrorActionKind.Disconnect or ConnectionErrorActionKind.Cancel =>
            _errorActionHandler.CanHandle(action) && _windowLifecycle.CanDisconnect,
        _ => _errorActionHandler.CanHandle(action),
    };

    private void RefreshErrorActionState()
    {
        if (SessionErrorCard.ViewModel is { } error)
        {
            SessionErrorCard.ViewModel = error;
        }
    }

    private Task CopyCorrelationIdAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SessionErrorCard.ViewModel is { } card)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(card.CorrelationId);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }

        return Task.CompletedTask;
    }

    private async Task ExportDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (_diagnosticExportService is null)
        {
            throw new InvalidOperationException("Diagnostic export is not available.");
        }

        var path = await _diagnosticExportService.ExportAsync(
            this,
            DesktopDiagnosticContextFactory.Create(),
            cancellationToken);
        if (path is not null)
        {
            StatusText.Text = $"诊断已导出：{path}";
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        BeginClosingDiagnostics();
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        _ = CloseSessionAsync();
    }

    private async Task StopSessionCoreAsync()
    {
        try
        {
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var release = ReleaseInputAsync(releaseTimeout.Token);
            try
            {
                await release.WaitAsync(TimeSpan.FromMilliseconds(250));
            }
            catch (TimeoutException)
            {
                _ = ObserveFailureAsync(release);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            await ViewModel.DisposeAsync();
        }
        catch (Exception)
        {
        }
    }

    private async Task CloseWindowCoreAsync()
    {
        _lifetime.Cancel();
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _allowClose = true;
                Close();
            }, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private static async Task ObserveFailureAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private async Task ObserveSmokePointerProbeAsync()
    {
        var trigger = Environment.GetEnvironmentVariable(
            "WINARD_REMOTE_SMOKE_POINTER_PROBE_TRIGGER");
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return;
        }

        while (!File.Exists(trigger))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), _lifetime.Token).ConfigureAwait(false);
        }

        Task send = Task.CompletedTask;
        await _dispatcher.InvokeAsync(
            () =>
            {
                var point = new RemotePoint(
                    Math.Max(0, _remoteSize.Width / 2),
                    Math.Max(0, _remoteSize.Height / 2));
                send = RemotePointerDispatch.RunAsync(
                    () => UpdateLocalPointerState(point, _pointerMask),
                    _inputOperations,
                    () => ViewModel.SendPointerAsync(
                        _pointerMask,
                        point,
                        _lifetime.Token).AsTask());
            },
            _lifetime.Token).ConfigureAwait(false);
        await send.ConfigureAwait(false);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _appWindow.Closing -= OnClosing;
        InputSurface.RemoveHandler(UIElement.KeyDownEvent, _keyDownHandler);
        InputSurface.RemoveHandler(UIElement.KeyUpEvent, _keyUpHandler);
        FrameSurface.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressedHandler);
        FrameSurface.RemoveHandler(UIElement.PointerMovedEvent, _pointerMovedHandler);
        FrameSurface.RemoveHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler);
        FrameSurface.RemoveHandler(UIElement.PointerCanceledEvent, _pointerCanceledHandler);
        FrameSurface.RemoveHandler(UIElement.PointerWheelChangedEvent, _pointerWheelChangedHandler);
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SessionErrorCard.ActionRequested -= OnErrorActionRequested;
        _windowLifecycle.Dispose();
        _cursorVisibility.Reset();
        InputSurface.Dispose();
        _lifetime.Dispose();
    }

    private void UpdateRemoteCursor()
    {
        var cursor = ViewModel.RemoteCursor;
        if (cursor is null)
        {
            RemoteCursorOverlay.Source = null;
            _cursorVisibility.SetRemoteCursorVisible(false);
            WriteSmokeMarker("WINARD_REMOTE_SMOKE_CURSOR_MARKER", "hidden");
            WriteHostCursorSmokeMarker();
            return;
        }

        var bitmap = new WriteableBitmap(cursor.Width, cursor.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(cursor.Bgra32.Span);
        }

        RemoteCursorOverlay.Source = bitmap;
        _cursorVisibility.SetRemoteCursorVisible(true);
        WriteSmokeMarker("WINARD_REMOTE_SMOKE_CURSOR_MARKER", "visible");
        WriteHostCursorSmokeMarker();
        UpdateCursorPosition();
    }

    private void WriteHostCursorSmokeMarker()
    {
        var value = InputSurface.CurrentHostCursor switch
        {
            InputDesktopResourceCursor cursor =>
                $"{nameof(InputDesktopResourceCursor)}|{cursor.ResourceId}|{cursor.ModuleName}",
            InputSystemCursor cursor =>
                $"{nameof(InputSystemCursor)}|{cursor.CursorShape}",
            null => "null",
            var cursor => cursor.GetType().Name
        };
        WriteSmokeMarker("WINARD_REMOTE_SMOKE_HOST_CURSOR_MARKER", value);
    }

    private void UpdateCursorPosition()
    {
        var cursor = ViewModel.RemoteCursor;
        var transform = CurrentTransform();
        if (cursor is null || !transform.IsValid)
        {
            return;
        }

        RemoteCursorOverlay.Width = cursor.Width * transform.DipScale;
        RemoteCursorOverlay.Height = cursor.Height * transform.DipScale;
        Canvas.SetLeft(
            RemoteCursorOverlay,
            transform.OriginX + ((_lastPointer.X - cursor.HotspotX) * transform.DipScale));
        Canvas.SetTop(
            RemoteCursorOverlay,
            transform.OriginY + ((_lastPointer.Y - cursor.HotspotY) * transform.DipScale));
        WriteSmokeMarker(
            "WINARD_REMOTE_SMOKE_POINTER_MARKER",
            $"{_lastPointer.X},{_lastPointer.Y}");
    }

    private static void WriteSmokeMarker(string environmentVariable, string value)
    {
        var marker = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(marker))
        {
            return;
        }

        try
        {
            File.WriteAllText(marker, value);
        }
        catch (Exception)
        {
            // Smoke diagnostics are best-effort and never affect a real session.
        }
    }

}
