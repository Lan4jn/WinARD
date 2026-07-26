using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using WinARD.Application.Ports;
using WinARD.Desktop.Clipboard;
using WinARD.Desktop.Input;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinRT.Interop;

namespace WinARD.Desktop.Views;

public sealed partial class RemoteSessionWindow : Window, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _closeSync = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly WindowsClipboardBridge _clipboardBridge;
    private readonly RemoteInputOperationRunner _inputOperations;
    private readonly RemoteTextInputBuffer _textInput = new();
    private readonly AppWindow _appWindow;
    private RemoteFramebufferSize _remoteSize;
    private Task? _closeTask;
    private ViewportScaleMode _scaleMode = ViewportScaleMode.Fit;
    private byte _pointerMask;
    private RemotePoint _lastPointer;
    private bool _fullscreen;
    private bool _allowClose;

    public RemoteSessionWindow(
        IRemoteSessionRuntime session,
        IAsyncDisposable ownership,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ownership);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        InitializeComponent();
        _remoteSize = session.FramebufferSize;
        var presenter = new D3DFramePresenter(FramePanel);
        _clipboardBridge = new WindowsClipboardBridge(dispatcher, session.SendClipboardTextAsync);
        ViewModel = new RemoteSessionViewModel(
            session,
            ownership,
            presenter,
            dispatcher,
            _clipboardBridge);
        _inputOperations = new RemoteInputOperationRunner(
            dispatcher,
            ViewModel.ReportInputFailureAsync,
            CloseSessionAsync,
            () => _lifetime.IsCancellationRequested);
        StatusText.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = ViewModel,
            Path = new PropertyPath(nameof(RemoteSessionViewModel.StatusMessage)),
        });

        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        _appWindow.Closing += OnClosing;
        Closed += OnClosed;
        Activated += OnActivated;
        InputSurface.KeyDown += OnKeyDown;
        InputSurface.KeyUp += OnKeyUp;
        InputSurface.CharacterReceived += OnCharacterReceived;
        InputSurface.PointerPressed += OnPointerPressed;
        InputSurface.PointerMoved += OnPointerMoved;
        InputSurface.PointerReleased += OnPointerReleased;
        InputSurface.PointerCanceled += OnPointerCanceled;
        InputSurface.PointerWheelChanged += OnPointerWheelChanged;
        FramePanel.Loaded += OnFramePanelLoaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        FrameScrollViewer.SizeChanged += (_, _) => UpdateFrameSizing();
        UpdateFrameSizing();
    }

    public RemoteSessionViewModel ViewModel { get; }

    public Task CloseSessionAsync()
    {
        lock (_closeSync)
        {
            return _closeTask ??= CloseCoreAsync();
        }
    }

    public ValueTask DisposeAsync() => new(CloseSessionAsync());

    private void OnFramePanelLoaded(object sender, RoutedEventArgs args)
    {
        FramePanel.Loaded -= OnFramePanelLoaded;
        _ = ViewModel.StartAsync(_lifetime.Token);
        _ = ObserveCompletionAsync();
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
        if (_lifetime.IsCancellationRequested || !CurrentTransform().IsValid)
        {
            return;
        }

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
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

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
        _ = InputSurface.CapturePointer(args.Pointer);
        _ = _inputOperations.RunAsync(() => SendPointerAsync(args));
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args) =>
        _ = _inputOperations.RunAsync(() => SendPointerAsync(args));

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        InputSurface.ReleasePointerCapture(args.Pointer);
        _ = _inputOperations.RunAsync(() => SendPointerAsync(args));
        args.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args) =>
        _ = _inputOperations.RunAsync(() => ReleaseInputAsync(_lifetime.Token));

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        _ = _inputOperations.RunAsync(() => SendWheelAsync(args));
        args.Handled = true;
    }

    private async Task SendWheelAsync(PointerRoutedEventArgs args)
    {
        if (!TryGetRemotePoint(args, out var point))
        {
            return;
        }

        var properties = args.GetCurrentPoint(InputSurface).Properties;
        var baseMask = WindowsInputMapper.ToPointerMask(ToButtons(properties));
        var wheelMask = WindowsInputMapper.WithWheel(baseMask, properties.MouseWheelDelta);
        await ViewModel.SendPointerAsync(wheelMask, point, _lifetime.Token);
        await ViewModel.SendPointerAsync(baseMask, point, _lifetime.Token);
    }

    private async Task SendPointerAsync(PointerRoutedEventArgs args)
    {
        if (!TryGetRemotePoint(args, out var point))
        {
            return;
        }

        _lastPointer = point;
        _pointerMask = WindowsInputMapper.ToPointerMask(
            ToButtons(args.GetCurrentPoint(InputSurface).Properties));
        await ViewModel.SendPointerAsync(_pointerMask, point, _lifetime.Token);
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
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        _ = CloseSessionAsync();
    }

    private async Task CloseCoreAsync()
    {
        _lifetime.Cancel();
        try
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
        finally
        {
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

    private async Task ObserveCompletionAsync()
    {
        await ViewModel.Completion.ConfigureAwait(false);
        if (!_lifetime.IsCancellationRequested)
        {
            await _dispatcher.InvokeAsync(
                () => _ = CloseSessionAsync(),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _appWindow.Closing -= OnClosing;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _lifetime.Dispose();
    }

}
