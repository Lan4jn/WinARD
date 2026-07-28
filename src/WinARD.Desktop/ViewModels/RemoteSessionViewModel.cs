using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WinARD.Application.Ports;
using WinARD.Desktop.Clipboard;
using WinARD.Desktop.Input;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Threading;
using WinARD.Domain.Errors;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Remote.Protocol.Errors;

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
    private readonly ISafeDiagnosticSink? _diagnosticSink;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private Task? _presentTask;
    private Task? _monitorTask;
    private Task? _disposeTask;
    private Task? _ownershipDisposeTask;
    private long _sequence;
    private RemoteFramebufferSize _framebufferSize;
    private RemoteCursorUpdate? _remoteCursor;
    private string _statusMessage = "已连接。";
    private WinArdError? _error;

    public RemoteSessionViewModel(
        IRemoteSessionRuntime session,
        IAsyncDisposable ownership,
        IFramePresenter presenter,
        IUiDispatcher dispatcher,
        WindowsClipboardBridge? clipboardBridge,
        ISafeDiagnosticSink? diagnosticSink = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clipboardBridge = clipboardBridge;
        _diagnosticSink = diagnosticSink;
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

    public RemoteCursorUpdate? RemoteCursor
    {
        get => _remoteCursor;
        private set
        {
            var previous = _remoteCursor;
            if (SetProperty(ref _remoteCursor, value))
            {
                previous?.Dispose();
            }
        }
    }

    public WinArdError? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
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
            _monitorTask = MonitorLoopsAsync(_receiveTask, _presentTask);
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

    public void ObserveInputFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var error = WinArdError.Create(
            ConnectionStage.Connected,
            "REMOTE_INPUT_FAILED",
            "远程输入发送失败。",
            Guid.NewGuid().ToString("N"));
        _ = _dispatcher.InvokeAsync(
            () => Error = error,
            CancellationToken.None);
        _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
            error.Code,
            error.CorrelationId,
            "Remote input operation failed.",
            Exception: exception));
    }

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
                        var framebufferResized = FramebufferSize != frame.Size;
                        using (frame)
                        {
                            var cursor = frame.TakeCursorOwnership();
                            try
                            {
                                if (framebufferResized)
                                {
                                    await _dispatcher.InvokeAsync(
                                        () => FramebufferSize = frame.Size,
                                        cancellationToken).ConfigureAwait(false);
                                }

                                _frames.Publish(FramePacket.TakeFrom(
                                    Interlocked.Increment(ref _sequence),
                                    frame));
                                if (cursor is not null)
                                {
                                    await PublishCursorAsync(cursor, cancellationToken).ConfigureAwait(false);
                                    cursor = null;
                                }
                            }
                            finally
                            {
                                if (cursor is not null && !ReferenceEquals(RemoteCursor, cursor))
                                {
                                    cursor.Dispose();
                                }
                            }
                        }
                        await _session.RequestFramebufferUpdateAsync(
                            incremental: !framebufferResized,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case RemoteCursorMessage cursorMessage:
                        using (cursorMessage)
                        {
                            await PublishCursorAsync(
                                cursorMessage.TakeCursorOwnership(),
                                cancellationToken).ConfigureAwait(false);
                        }
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
    }

    private async Task PresentLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var frame = await _frames.ReadLatestAsync(cancellationToken).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() =>
                {
                    _presenter.Resize(frame.Width, frame.Height);
                    _presenter.Present(frame.Pixels.Span, frame.Stride, frame.DirtyRectangles);
                }, cancellationToken).ConfigureAwait(false);
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
        var ownershipDisposal = DisposeOwnershipOnceAsync();
        var monitor = _monitorTask;
        if (monitor is not null)
        {
            await CaptureFailureAsync(monitor, failures ??= []).ConfigureAwait(false);
        }
        else
        {
            _completion.TrySetResult();
        }

        await CaptureFailureAsync(_inputMapper.DisposeAsync().AsTask(), failures ??= []).ConfigureAwait(false);
        if (_clipboardBridge is not null)
        {
            await CaptureFailureAsync(_clipboardBridge.DisposeAsync().AsTask(), failures ??= []).ConfigureAwait(false);
        }

        await CaptureFailureAsync(_frames.DisposeAsync().AsTask(), failures ??= []).ConfigureAwait(false);
        await CaptureFailureAsync(DisposeCursorAsync(), failures ??= []).ConfigureAwait(false);
        Task presenterDisposal = Task.CompletedTask;
        await CaptureFailureAsync(
            DisposePresenterAsync(),
            failures ??= []).ConfigureAwait(false);
        await CaptureFailureAsync(ownershipDisposal, failures ??= []).ConfigureAwait(false);
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
            if (_ownershipDisposeTask is null)
            {
                try
                {
                    _ownershipDisposeTask = _ownership.DisposeAsync().AsTask();
                }
                catch (Exception exception)
                {
                    _ownershipDisposeTask = Task.FromException(exception);
                }
            }

            return _ownershipDisposeTask;
        }
    }

    private static IReadOnlyList<DiagnosticField>? GetPresentationFailureFields(
        Exception exception) =>
        exception is D3DPresentationException presentation
            ? [new DiagnosticField(
                "PresentationStage",
                presentation.Stage.ToString(),
                DiagnosticFieldCategory.Public)]
            : null;

    private static RfbProtocolException? FindProtocolFailureException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        if (exception is RfbProtocolException { Failure: not null } protocolException)
        {
            return protocolException;
        }

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.InnerExceptions)
            {
                var match = FindProtocolFailureException(innerException);
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }

        return FindProtocolFailureException(exception.InnerException);
    }

    private static List<DiagnosticField>? GetProtocolFailureFields(
        RfbProtocolException? exception)
    {
        if (exception?.Failure is not { } failure)
        {
            return null;
        }

        var fields = new List<DiagnosticField>(5)
        {
            new(
                "ProtocolFailureKind",
                failure.Kind.ToString(),
                DiagnosticFieldCategory.Public),
        };
        if (failure.ReadStage is { } readStage)
        {
            fields.Add(new DiagnosticField(
                "ProtocolReadStage",
                readStage.ToString(),
                DiagnosticFieldCategory.Public));
        }

        if (failure.ServerMessageType is { } serverMessageType)
        {
            fields.Add(new DiagnosticField(
                "ServerMessageType",
                $"0x{serverMessageType.ToString("X2", CultureInfo.InvariantCulture)}",
                DiagnosticFieldCategory.Public));
        }

        if (failure.EncodingId is { } encodingId)
        {
            fields.Add(new DiagnosticField(
                "EncodingId",
                encodingId.ToString(CultureInfo.InvariantCulture),
                DiagnosticFieldCategory.Public));
        }

        if (failure.RectangleIndex is { } rectangleIndex)
        {
            fields.Add(new DiagnosticField(
                "RectangleIndex",
                rectangleIndex.ToString(CultureInfo.InvariantCulture),
                DiagnosticFieldCategory.Public));
        }

        return fields;
    }

    private async Task MonitorLoopsAsync(Task receive, Task present)
    {
        try
        {
            var completed = await Task.WhenAny(receive, present).ConfigureAwait(false);
            var wasTerminalFailure = completed.IsFaulted && !_lifetime.IsCancellationRequested;
            if (wasTerminalFailure)
            {
                _lifetime.Cancel();
            }

            var ownershipDisposal = DisposeOwnershipOnceAsync();
            if (wasTerminalFailure)
            {
                var protocolException = ReferenceEquals(completed, receive)
                    ? FindProtocolFailureException(completed.Exception)
                    : null;
                var exception = protocolException ?? completed.Exception?.GetBaseException() ??
                    new InvalidOperationException("Remote session terminated unexpectedly.");
                var error = WinArdError.Create(
                    ConnectionStage.Connected,
                    ReferenceEquals(completed, present)
                        ? "REMOTE_PRESENTATION_FAILED"
                        : "REMOTE_SESSION_INTERRUPTED",
                    "远程会话已中断。",
                    Guid.NewGuid().ToString("N"));
                var status = ReferenceEquals(completed, present)
                    ? "画面呈现失败，会话正在关闭。"
                    : "连接已中断。";
                try
                {
                    await _dispatcher.InvokeAsync(
                        () =>
                        {
                            StatusMessage = status;
                            Error = error;
                        },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Terminal cleanup must not depend on status reporting.
                }

                _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
                    error.Code,
                    error.CorrelationId,
                    "Remote session loop failed.",
                    Fields: ReferenceEquals(completed, present)
                        ? GetPresentationFailureFields(exception)
                        : GetProtocolFailureFields(protocolException),
                    Exception: exception));
            }

            await ObserveFailureAsync(receive).ConfigureAwait(false);
            await ObserveFailureAsync(present).ConfigureAwait(false);
            await ObserveFailureAsync(ownershipDisposal).ConfigureAwait(false);
        }
        finally
        {
            _completion.TrySetResult();
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

    private async Task PublishCursorAsync(
        RemoteCursorUpdate cursor,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!cursor.IsVisible)
            {
                cursor.Dispose();
                await _dispatcher.InvokeAsync(
                    () => RemoteCursor = null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await _dispatcher.InvokeAsync(
                () => RemoteCursor = cursor,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!ReferenceEquals(RemoteCursor, cursor))
            {
                cursor.Dispose();
            }

            throw;
        }
    }

    private async Task DisposeCursorAsync()
    {
        try
        {
            await _dispatcher.InvokeAsync(
                () => RemoteCursor = null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _remoteCursor, null)?.Dispose();
            throw;
        }
    }
}
