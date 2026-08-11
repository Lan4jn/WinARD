using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.ExceptionServices;
using WinARD.Application.Ports;
using WinARD.Desktop.Clipboard;
using WinARD.Desktop.Input;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Services;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Infrastructure.Diagnostics;
using WinRT.Interop;

namespace WinARD.Desktop.Views;

public sealed partial class RemoteSessionWindow : Window, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _saveCancellation;
    private readonly object _pointerStateSync = new();
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
    private readonly FrameRateSelectionCoordinator _frameRateSelection = new();
    private readonly FrameRateSaveStatus _frameRateSaveStatus = new();
    private readonly PerformanceTextPresentationState _performanceTextPresentation = new();
    private readonly Func<FrameRefreshPolicy, CancellationToken, Task<ConnectionProfile>>?
        _updateFrameRefreshPolicy;
    private ConnectionProfile? _profile;
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
        Func<CancellationToken, Task>? retryRequested = null,
        ConnectionProfile? profile = null,
        Func<FrameRefreshPolicy, CancellationToken, Task<ConnectionProfile>>?
            updateFrameRefreshPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ownership);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _saveCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _diagnosticExportService = diagnosticExportService;
        _updateFrameRefreshPolicy = updateFrameRefreshPolicy;
        _profile = profile;
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
            diagnosticSink,
            profile?.Quality ?? QualityProfile.Automatic);
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
            handlers[ConnectionErrorActionKind.ExportDiagnostics] = RunDiagnosticExportAsync;
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
        FrameRateComboBox.ItemsSource = ViewModel.FrameRefreshOptions;
        RefreshFrameRateSelection();
        UpdatePerformanceVisual();
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

    internal Task SelectFrameRefreshPolicyAsync(FrameRefreshPolicy policy) =>
        ApplyFrameRefreshPolicySelectionAsync(
            policy,
            ViewModel.SetFrameRefreshPolicy,
            _updateFrameRefreshPolicy is null
                ? null
                : async (value, cancellationToken) =>
                {
                    var updated = await _updateFrameRefreshPolicy(value, cancellationToken);
                    Volatile.Write(ref _profile, updated);
                },
            ShowFrameRateSaveStatus,
            IsInputClosing,
            _saveCancellation.Token);

    internal static async Task ApplyFrameRefreshPolicySelectionAsync(
        FrameRefreshPolicy policy,
        Action<FrameRefreshPolicy> applyToSession,
        Func<FrameRefreshPolicy, CancellationToken, Task>? persist,
        Action<string> showStatus,
        Func<bool> isClosing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applyToSession);
        ArgumentNullException.ThrowIfNull(showStatus);
        ArgumentNullException.ThrowIfNull(isClosing);
        if (isClosing() || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        applyToSession(policy);
        if (persist is null)
        {
            return;
        }

        try
        {
            await persist(policy, cancellationToken);
        }
        catch (OperationCanceledException) when (
            isClosing() || cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (isClosing())
        {
        }
        catch (Exception) when (isClosing() || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            showStatus("刷新设置未保存，本次会话仍已应用");
        }
    }

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
        try
        {
            await RunDiagnosticExportAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_diagnosticExportState.IsClosing)
            {
                StatusText.Text = "诊断导出失败。";
            }
        }
    }

    private async Task RunDiagnosticExportAsync(CancellationToken cancellationToken)
    {
        if (_diagnosticExportService is null ||
            !_diagnosticExportState.TryBeginExport(cancellationToken, out var exportToken))
        {
            return;
        }

        RefreshDiagnosticActionState();
        try
        {
            await ExportDiagnosticsAsync(exportToken);
        }
        finally
        {
            _diagnosticExportState.CompleteExport();
            if (!_diagnosticExportState.IsClosing)
            {
                RefreshDiagnosticActionState();
            }
        }
    }

    private void RefreshDiagnosticExportState() =>
        ExportDiagnosticsButton.IsEnabled = _diagnosticExportState.IsEnabled;

    private void RefreshDiagnosticActionState()
    {
        RefreshDiagnosticExportState();
        RefreshErrorActionState();
    }

    private void BeginClosingDiagnostics()
    {
        if (Interlocked.Exchange(ref _closingStarted, 1) == 0)
        {
            _saveCancellation.Cancel();
        }

        _diagnosticExportState.BeginClosing();
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
        QueuePointerBarrierSend(args);
        args.Handled = true;
    }

    private async void OnFrameRateSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        var option = FrameRateComboBox.SelectedItem as FrameRefreshOption;
        if (option is null)
        {
            return;
        }

        try
        {
            ClearFrameRateSaveStatus();
            await _frameRateSelection.ApplySelectionAsync(
                option,
                SelectFrameRefreshPolicyAsync);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (IsInputClosing())
        {
        }
        catch (Exception) when (IsInputClosing())
        {
        }
        catch (Exception)
        {
            ShowFrameRateSaveStatus("刷新设置应用失败");
        }
        finally
        {
            RefreshFrameRateSelection();
        }
    }

    private void OnFrameRateDropDownOpened(object sender, object args)
    {
        for (var index = 0; index < ViewModel.FrameRefreshOptions.Count; index++)
        {
            if (FrameRateComboBox.ContainerFromIndex(index) is ComboBoxItem item)
            {
                var option = ViewModel.FrameRefreshOptions[index];
                item.IsEnabled = option.IsEnabled;
                AutomationProperties.SetName(
                    item,
                    FrameRefreshOptionPresentation.AutomationName(option));
            }
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args) =>
        QueuePointerMove(args);

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        FrameSurface.ReleasePointerCapture(args.Pointer);
        QueuePointerBarrierSend(args);
        args.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args)
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
        UpdateLocalPointerState(point, pointerMask: 0);
        _ = _inputOperations.RunAsync(
            () => ReleaseInputAsync(_lifetime.Token, releasePointer: true));
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
        ViewModel.RecordScrollInput();
        var properties = args.GetCurrentPoint(FrameSurface).Properties;
        var baseMask = WindowsInputMapper.ToPointerMask(ToButtons(properties));
        var wheelMask = WindowsInputMapper.WithWheel(baseMask, properties.MouseWheelDelta);
        _ = RemotePointerDispatch.RunAsync(
            () => UpdateLocalPointerState(point, baseMask),
            _inputOperations,
            () => ViewModel.SendPointerBarrierAsync(
                    [new PointerWrite(wheelMask, point), new PointerWrite(baseMask, point)],
                    _lifetime.Token)
                .AsTask());
    }

    private void QueuePointerMove(PointerRoutedEventArgs args)
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
        UpdateLocalPointerState(point, pointerMask);
        try
        {
            ViewModel.QueuePointerMove(pointerMask, point);
        }
        catch (OperationCanceledException) when (IsInputClosing())
        {
        }
        catch (ObjectDisposedException) when (IsInputClosing())
        {
        }
    }

    private void QueuePointerBarrierSend(PointerRoutedEventArgs args)
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
            () => ViewModel.SendPointerBarrierAsync(
                    [new PointerWrite(pointerMask, point)],
                    _lifetime.Token)
                .AsTask());
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
        UpdatePointerState(point, pointerMask);
        UpdateCursorPosition();
    }

    private void UpdatePointerState(RemotePoint point, byte pointerMask)
    {
        lock (_pointerStateSync)
        {
            _lastPointer = point;
            _pointerMask = pointerMask;
        }
    }

    private (RemotePoint Point, byte Mask) ReadPointerState()
    {
        lock (_pointerStateSync)
        {
            return (_lastPointer, _pointerMask);
        }
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

    private async Task ReleaseInputAsync(
        CancellationToken cancellationToken,
        bool releasePointer = false)
    {
        var pointerState = ReadPointerState();
        releasePointer |= pointerState.Mask != 0;
        Task cursorUpdate = Task.CompletedTask;
        if (releasePointer)
        {
            UpdatePointerState(pointerState.Point, pointerMask: 0);
            cursorUpdate = UpdateCursorPositionBestEffortAsync();
        }

        Exception? failure = null;
        try
        {
            await ViewModel.ReleaseInputAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (releasePointer)
        {
            var releasePoint = ReadPointerState().Point;
            UpdatePointerState(releasePoint, pointerMask: 0);
            try
            {
                await ViewModel.SendPointerBarrierAsync(
                    [new PointerWrite(0, releasePoint)],
                    cancellationToken);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        await cursorUpdate;
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task UpdateCursorPositionBestEffortAsync()
    {
        try
        {
            await _dispatcher.InvokeAsync(
                UpdateCursorPosition,
                CancellationToken.None);
        }
        catch (Exception)
        {
            // Remote release must not depend on best-effort cursor presentation.
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(() => OnViewModelPropertyChanged(sender, args));
            return;
        }

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
        else if (args.PropertyName == nameof(RemoteSessionViewModel.SessionPerformance))
        {
            UpdatePerformanceVisual();
            UpdateConnectionQualityAutomationName();
        }
        else if (args.PropertyName == nameof(RemoteSessionViewModel.FrameRefreshOptions) ||
            args.PropertyName == nameof(RemoteSessionViewModel.SelectedFrameRefreshOption))
        {
            FrameRateComboBox.ItemsSource = ViewModel.FrameRefreshOptions;
            RefreshFrameRateSelection();
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
        UpdateConnectionQualityAutomationName();
        AutomationProperties.SetName(QualityIndicator, $"连接质量：{quality.DisplayText}");
    }

    private void RefreshFrameRateSelection()
    {
        _frameRateSelection.SynchronizeSelection(() =>
        {
            FrameRateComboBox.SelectedItem = ViewModel.SelectedFrameRefreshOption;
            var option = ViewModel.SelectedFrameRefreshOption;
            AutomationProperties.SetName(
                FrameRateComboBox,
                $"刷新频率：{FrameRefreshOptionPresentation.AutomationName(option)}");
        });
    }

    private void UpdatePerformanceVisual()
    {
        var performance = ViewModel.SessionPerformance;
        if (!_performanceTextPresentation.TryUpdate(performance))
        {
            return;
        }

        PerformanceText.Text = performance;
        AutomationProperties.SetName(
            PerformanceText,
            $"会话性能：{performance}");
    }

    private void ShowFrameRateSaveStatus(string message)
    {
        _frameRateSaveStatus.Show(message);
        FrameRateSaveStatusText.Text = _frameRateSaveStatus.Message;
        FrameRateSaveStatusText.Visibility = Visibility.Visible;
        AutomationProperties.SetName(
            FrameRateSaveStatusText,
            _frameRateSaveStatus.Message);
    }

    private void ClearFrameRateSaveStatus()
    {
        _frameRateSaveStatus.Clear();
        FrameRateSaveStatusText.Text = string.Empty;
        FrameRateSaveStatusText.Visibility = Visibility.Collapsed;
    }

    private void UpdateConnectionQualityAutomationName() =>
        AutomationProperties.SetName(
            QualityPanel,
            $"连接质量：{ViewModel.ConnectionQuality.DisplayText}；会话性能：{ViewModel.SessionPerformance}");

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
            if (!_diagnosticExportState.IsClosing)
            {
                StatusText.Text = "错误操作未能完成。";
            }
        }
    }

    private bool IsErrorActionEnabled(ConnectionErrorActionKind action) => action switch
    {
        ConnectionErrorActionKind.Retry =>
            _errorActionHandler.CanHandle(action) && _windowLifecycle.CanRetry,
        ConnectionErrorActionKind.Disconnect or ConnectionErrorActionKind.Cancel =>
            _errorActionHandler.CanHandle(action) && _windowLifecycle.CanDisconnect,
        ConnectionErrorActionKind.ExportDiagnostics =>
            _errorActionHandler.CanHandle(action) && _diagnosticExportState.IsEnabled,
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
            DesktopDiagnosticContextFactory.CreateSession(
                Volatile.Read(ref _profile),
                ViewModel.DiagnosticPerformance),
            cancellationToken);
        if (path is not null && !_diagnosticExportState.IsClosing)
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
                var pointerMask = ReadPointerState().Mask;
                send = RemotePointerDispatch.RunAsync(
                    () => UpdateLocalPointerState(point, pointerMask),
                    _inputOperations,
                    () => ViewModel.SendPointerBarrierAsync(
                            [new PointerWrite(pointerMask, point)],
                            _lifetime.Token)
                        .AsTask());
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
        _saveCancellation.Dispose();
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
        var pointer = ReadPointerState().Point;
        if (cursor is null || !transform.IsValid)
        {
            return;
        }

        RemoteCursorOverlay.Width = cursor.Width * transform.DipScale;
        RemoteCursorOverlay.Height = cursor.Height * transform.DipScale;
        Canvas.SetLeft(
            RemoteCursorOverlay,
            transform.OriginX + ((pointer.X - cursor.HotspotX) * transform.DipScale));
        Canvas.SetTop(
            RemoteCursorOverlay,
            transform.OriginY + ((pointer.Y - cursor.HotspotY) * transform.DipScale));
        WriteSmokeMarker(
            "WINARD_REMOTE_SMOKE_POINTER_MARKER",
            $"{pointer.X},{pointer.Y}");
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

internal sealed class FrameRateSelectionCoordinator
{
    private int _synchronizationDepth;
    private int _selectionActive;

    public bool IsSelectionActive => Volatile.Read(ref _selectionActive) != 0;

    public void SynchronizeSelection(Action synchronize)
    {
        ArgumentNullException.ThrowIfNull(synchronize);
        _ = Interlocked.Increment(ref _synchronizationDepth);
        try
        {
            synchronize();
        }
        finally
        {
            _ = Interlocked.Decrement(ref _synchronizationDepth);
        }
    }

    public Task<bool> ApplySelectionAsync(
        FrameRefreshOption? option,
        Func<FrameRefreshPolicy, Task> selectPolicy)
    {
        ArgumentNullException.ThrowIfNull(selectPolicy);
        if (option is null ||
            !option.IsEnabled ||
            Volatile.Read(ref _synchronizationDepth) != 0 ||
            Interlocked.CompareExchange(ref _selectionActive, 1, 0) != 0)
        {
            return Task.FromResult(false);
        }

        return ApplySelectionCoreAsync(option.Policy, selectPolicy);
    }

    private async Task<bool> ApplySelectionCoreAsync(
        FrameRefreshPolicy policy,
        Func<FrameRefreshPolicy, Task> selectPolicy)
    {
        try
        {
            await selectPolicy(policy);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _selectionActive, 0);
        }
    }
}

internal sealed class FrameRateSaveStatus
{
    public string? Message { get; private set; }

    public bool IsVisible => Message is not null;

    public void Show(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Message = message;
    }

    public void Clear() => Message = null;
}

internal sealed class PerformanceTextPresentationState
{
    private string? _text;

    public bool TryUpdate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.Equals(_text, text, StringComparison.Ordinal))
        {
            return false;
        }

        _text = text;
        return true;
    }
}
