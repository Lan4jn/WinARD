using CommunityToolkit.Mvvm.ComponentModel;
using WinARD.Application.Ports;
using WinARD.Desktop.Clipboard;
using WinARD.Desktop.Input;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Threading;

namespace WinARD.Desktop.ViewModels;

public sealed class RemoteSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IRemoteSessionRuntime _session;
    private readonly IAsyncDisposable _ownership;
    private readonly IFramePresenter _presenter;
    private readonly IUiDispatcher _dispatcher;
    private readonly WindowsClipboardBridge? _clipboardBridge;
    private readonly LatestFrameMailbox _frames = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly WindowsInputMapper _inputMapper;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private Task? _presentTask;
    private Task? _disposeTask;
    private Task? _ownershipDisposeTask;
    private long _sequence;
    private RemoteFramebufferSize _framebufferSize;
    private string _statusMessage = "已连接。";

    public RemoteSessionViewModel(
        IRemoteSessionRuntime session,
        IAsyncDisposable ownership,
        IFramePresenter presenter,
        IUiDispatcher dispatcher,
        WindowsClipboardBridge? clipboardBridge)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clipboardBridge = clipboardBridge;
        _inputMapper = new WindowsInputMapper(_session.SendKeyAsync);
        _framebufferSize = session.FramebufferSize;
    }

    public RemoteFramebufferSize FramebufferSize
    {
        get => _framebufferSize;
        private set => SetProperty(ref _framebufferSize, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public Task Completion => _completion.Task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_receiveTask is not null)
            {
                return Task.CompletedTask;
            }

            _receiveTask = ReceiveLoopAsync(_lifetime.Token);
            _presentTask = PresentLoopAsync(_lifetime.Token);
            return Task.CompletedTask;
        }
    }

    public ValueTask KeyDownAsync(
        Windows.System.VirtualKey key,
        int scanCode,
        bool isExtended,
        string? text,
        CancellationToken cancellationToken) =>
        _inputMapper.KeyDownAsync(key, scanCode, isExtended, text, cancellationToken);

    public ValueTask KeyUpAsync(
        Windows.System.VirtualKey key,
        int scanCode,
        bool isExtended,
        string? text,
        CancellationToken cancellationToken) =>
        _inputMapper.KeyUpAsync(key, scanCode, isExtended, text, cancellationToken);

    public ValueTask TextInputAsync(string text, CancellationToken cancellationToken) =>
        _inputMapper.TextInputAsync(text, cancellationToken);

    public ValueTask ReleaseInputAsync(CancellationToken cancellationToken) =>
        _inputMapper.ReleaseAllAsync(cancellationToken);

    public ValueTask SendSecureAttentionSequenceAsync(CancellationToken cancellationToken) =>
        _inputMapper.SendSecureAttentionSequenceAsync(cancellationToken);

    public ValueTask SendPointerAsync(
        byte buttons,
        RemotePoint point,
        CancellationToken cancellationToken) =>
        _session.SendPointerAsync(buttons, point.X, point.Y, cancellationToken);

    public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) =>
        _session.SendClipboardTextAsync(text, cancellationToken);

    public Task ReportInputFailureAsync() =>
        _dispatcher.InvokeAsync(
            () => StatusMessage = "输入发送失败，会话正在关闭。",
            CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.RequestFramebufferUpdateAsync(
                incremental: false,
                cancellationToken).ConfigureAwait(false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = await _session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                switch (message)
                {
                    case RemoteFramebufferMessage frame:
                        if (FramebufferSize != frame.Size)
                        {
                            await _dispatcher.InvokeAsync(
                                () => FramebufferSize = frame.Size,
                                cancellationToken).ConfigureAwait(false);
                        }

                        _frames.Publish(FramePacket.CopyFrom(
                            Interlocked.Increment(ref _sequence),
                            frame));
                        await _session.RequestFramebufferUpdateAsync(
                            incremental: true,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case RemoteClipboardMessage clipboard when _clipboardBridge is not null:
                        await _clipboardBridge.SetRemoteTextAsync(clipboard.Text, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case RemoteBellMessage:
                        break;
                }

            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await _dispatcher.InvokeAsync(
                () => StatusMessage = "连接已中断。",
                CancellationToken.None).ConfigureAwait(false);
            await DisposeOwnershipOnceAsync().ConfigureAwait(false);
        }
        finally
        {
            _completion.TrySetResult();
        }
    }

    private async Task PresentLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var frame = await _frames.ReadLatestAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        _presenter.Resize(frame.Width, frame.Height);
                        _presenter.Present(frame.Pixels.Span, frame.Stride, frame.DirtyRectangles);
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    await _dispatcher.InvokeAsync(
                        () => StatusMessage = "画面呈现暂时不可用。",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? failures = null;
        _lifetime.Cancel();
        var receive = _receiveTask;
        var present = _presentTask;
        if (receive is not null)
        {
            await CaptureFailureAsync(receive, failures ??= []).ConfigureAwait(false);
        }
        if (present is not null)
        {
            await CaptureFailureAsync(present, failures ??= []).ConfigureAwait(false);
        }

        await CaptureFailureAsync(_inputMapper.DisposeAsync().AsTask(), failures ??= []).ConfigureAwait(false);
        if (_clipboardBridge is not null)
        {
            await CaptureFailureAsync(_clipboardBridge.DisposeAsync().AsTask(), failures ??= []).ConfigureAwait(false);
        }

        await CaptureFailureAsync(_frames.DisposeAsync().AsTask(), failures ??= []).ConfigureAwait(false);
        Task presenterDisposal = Task.CompletedTask;
        await CaptureFailureAsync(
            DisposePresenterAsync(),
            failures ??= []).ConfigureAwait(false);
        await CaptureFailureAsync(DisposeOwnershipOnceAsync(), failures ??= []).ConfigureAwait(false);
        _completion.TrySetResult();
        _lifetime.Dispose();
        if (failures.Count != 0)
        {
            throw new AggregateException("Remote session cleanup failed.", failures);
        }

        async Task DisposePresenterAsync()
        {
            await _dispatcher.InvokeAsync(
                () => presenterDisposal = _presenter.DisposeAsync().AsTask(),
                CancellationToken.None).ConfigureAwait(false);
            await presenterDisposal.ConfigureAwait(false);
        }
    }

    private static async Task CaptureFailureAsync(Task operation, List<Exception> failures)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private Task DisposeOwnershipOnceAsync()
    {
        lock (_sync)
        {
            return _ownershipDisposeTask ??= _ownership.DisposeAsync().AsTask();
        }
    }
}
