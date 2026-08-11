using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Desktop.Clipboard;
using WinARD.Desktop.Input;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Threading;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;

namespace WinARD.Desktop.ViewModels;

public sealed class RemoteSessionViewModel : ObservableObject, IAsyncDisposable
{
    // Bound diagnostics work for malformed or adversarial exception graphs.
    private const int MaxProtocolFailureExceptionNodes = 256;
    private const int MaxProtocolFailureExceptionEdgeInspections =
        MaxProtocolFailureExceptionNodes * 2;
    private const int MaxProtocolFailureExceptionFrames =
        MaxProtocolFailureExceptionNodes + 1;
    private readonly object _sync = new();
    private readonly IRemoteSessionRuntime _session;
    private readonly IAsyncDisposable _ownership;
    private readonly IFramePresenter _presenter;
    private readonly IUiDispatcher _dispatcher;
    private readonly WindowsClipboardBridge? _clipboardBridge;
    private readonly SessionFrameMailbox _frames = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly WindowsInputMapper _inputMapper;
    private readonly RemotePointerWriteCoalescer _pointerWrites;
    private readonly ISafeDiagnosticSink? _diagnosticSink;
    private readonly ConnectionQualityTracker _connectionQualityTracker;
    private readonly ConnectionQualityPublicationGate _connectionQualityPublicationGate = new();
    private readonly TimeProvider _timeProvider;
    private readonly FramebufferRequestPacer _pacer;
    private AdaptiveQualityController _adaptiveQualityController;
    private readonly ArdDisplayCapabilities _adaptiveQualityCapabilities;
    private readonly QualityDecoderGates _qualityDecoderGates;
    private readonly QualityTransitionCoordinator _qualityTransitionCoordinator;
    private readonly SemaphoreSlim _qualityOperationGate = new(1, 1);
    private CancellationTokenSource _qualityProfileOperations = new();
    private readonly SessionPerformanceTracker _performanceTracker;
    private readonly SessionPerformancePublicationGate _performancePublication;
    private readonly int? _remoteMaximum;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private Task? _presentTask;
    private Task? _monitorTask;
    private Task? _disposeTask;
    private Task? _ownershipDisposeTask;
    private Task? _inputFailureTask;
    private ConnectionQualitySnapshot? _pendingFrameQuality;
    private long _sequence;
    private RemoteFramebufferSize _framebufferSize;
    private RemoteCursorUpdate? _remoteCursor;
    private string _statusMessage = "已连接。";
    private ConnectionQualitySnapshot _connectionQuality;
    private SessionPerformanceSnapshot _performance;
    private FrameRefreshPolicy _refreshPolicy;
    private QualityProfile _qualityProfile;
    private QualityDecision? _latestQualityDecision;
    private QualityTransitionStatus _latestQualityTransitionStatus = QualityTransitionStatus.NoChange;
    private long _qualityPresentationVersion;
    private long _qualityDecisionGeneration;
    private long _qualityProfileEpoch;
    private int _pendingScrollInput;
    private IReadOnlyList<FrameRefreshOption> _frameRefreshOptions = [];
    private FrameRefreshOption _selectedFrameRefreshOption = null!;
    private WinArdError? _error;
    private int _inputUnavailable;
    private int _activeBestEffortUiObserverCount;
    private int _activePerformancePublicationCount;
    private long _performanceGeneration;
    private long _performancePublicationSequence;
    private long _lastAppliedPerformancePublicationSequence;

    public RemoteSessionViewModel(
        IRemoteSessionRuntime session,
        IAsyncDisposable ownership,
        IFramePresenter presenter,
        IUiDispatcher dispatcher,
        WindowsClipboardBridge? clipboardBridge,
        ISafeDiagnosticSink? diagnosticSink = null,
        TimeProvider? timeProvider = null)
        : this(
            session,
            ownership,
            presenter,
            dispatcher,
            clipboardBridge,
            diagnosticSink,
            FrameRefreshPolicy.Automatic,
            timeProvider,
            pacer: null,
            automaticFrameRateController: null,
            performanceTracker: null)
    {
    }

    internal RemoteSessionViewModel(
        IRemoteSessionRuntime session,
        IAsyncDisposable ownership,
        IFramePresenter presenter,
        IUiDispatcher dispatcher,
        WindowsClipboardBridge? clipboardBridge,
        ISafeDiagnosticSink? diagnosticSink,
        FrameRefreshPolicy initialRefreshPolicy,
        TimeProvider? timeProvider = null,
        FramebufferRequestPacer? pacer = null,
        AutomaticFrameRateController? automaticFrameRateController = null,
        SessionPerformanceTracker? performanceTracker = null,
        TimeSpan? performancePublicationInterval = null)
        : this(
            session,
            ownership,
            presenter,
            dispatcher,
            clipboardBridge,
            diagnosticSink,
            QualityProfile.Automatic.WithRefresh(initialRefreshPolicy),
            timeProvider,
            pacer,
            performanceTracker,
            performancePublicationInterval: performancePublicationInterval)
    {
        _ = automaticFrameRateController;
    }

    internal RemoteSessionViewModel(
        IRemoteSessionRuntime session,
        IAsyncDisposable ownership,
        IFramePresenter presenter,
        IUiDispatcher dispatcher,
        WindowsClipboardBridge? clipboardBridge,
        ISafeDiagnosticSink? diagnosticSink,
        QualityProfile qualityProfile,
        TimeProvider? timeProvider = null,
        FramebufferRequestPacer? pacer = null,
        SessionPerformanceTracker? performanceTracker = null,
        ArdDisplayCapabilities? adaptiveQualityCapabilities = null,
        QualityDecoderGates? qualityDecoderGates = null,
        AdaptiveQualityController? adaptiveQualityController = null,
        QualityTransitionCoordinator? qualityTransitionCoordinator = null,
        TimeSpan? performancePublicationInterval = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clipboardBridge = clipboardBridge;
        _diagnosticSink = diagnosticSink;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionQualityTracker = new ConnectionQualityTracker(_timeProvider);
        _connectionQuality = _connectionQualityTracker.Current;
        _remoteMaximum = ValidateRemoteMaximum(session.DisplayCapabilities.MaximumRefreshRate);
        _qualityProfile = qualityProfile ?? throw new ArgumentNullException(nameof(qualityProfile));
        _refreshPolicy = qualityProfile.Refresh;
        _adaptiveQualityCapabilities = adaptiveQualityCapabilities ?? WithMaximumRefreshRate(
            session.QualityCapabilities,
            _remoteMaximum);
        _qualityDecoderGates = qualityDecoderGates ?? new QualityDecoderGates();
        _adaptiveQualityController = adaptiveQualityController ?? new AdaptiveQualityController(
            _qualityProfile,
            _adaptiveQualityCapabilities,
            _qualityDecoderGates);
        _qualityTransitionCoordinator = qualityTransitionCoordinator ?? new QualityTransitionCoordinator(
            session,
            new RemoteQualitySettings(
                RemotePixelFormatKind.Bgra32,
                [6, 16, 0, 1, -239, -223],
                1),
            _adaptiveQualityCapabilities,
            _qualityDecoderGates);
        _pacer = pacer ?? new FramebufferRequestPacer(_timeProvider);
        var automaticTarget = InitialAdaptiveTarget(_adaptiveQualityController);
        var targetFramesPerSecond = ResolveTarget(_refreshPolicy, automaticTarget);
        _pacer.SetPolicy(
            _refreshPolicy,
            automaticTarget,
            _remoteMaximum);
        _performanceTracker = performanceTracker ??
            new SessionPerformanceTracker(_timeProvider, _refreshPolicy, targetFramesPerSecond);
        _performancePublication = new SessionPerformancePublicationGate(
            _timeProvider,
            performancePublicationInterval ?? TimeSpan.FromSeconds(1));
        _performance = _performanceTracker.Current;
        RefreshFrameRefreshOptions();
        _inputMapper = new WindowsInputMapper(_session.SendKeyAsync);
        _pointerWrites = new RemotePointerWriteCoalescer(
            (write, cancellationToken) =>
                _session.SendPointerAsync(
                    write.Buttons,
                    write.Point.X,
                    write.Point.Y,
                    cancellationToken),
            HandlePointerWriteFailureAsync);
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

    public ConnectionQualitySnapshot ConnectionQuality
    {
        get => _connectionQuality;
        private set => SetProperty(ref _connectionQuality, value);
    }

    public SessionPerformanceSnapshot Performance
    {
        get => _performance;
        private set
        {
            if (SetProperty(ref _performance, value))
            {
                OnPropertyChanged(nameof(TargetFramesPerSecond));
                OnPropertyChanged(nameof(SessionPerformance));
            }
        }
    }

    public int? TargetFramesPerSecond => Performance.TargetFramesPerSecond;

    public IReadOnlyList<FrameRefreshOption> FrameRefreshOptions => _frameRefreshOptions;

    public FrameRefreshOption SelectedFrameRefreshOption => _selectedFrameRefreshOption;

    public string SessionPerformance => FormatSessionPerformance(Performance);

    public long QualityPresentationVersion => Interlocked.Read(ref _qualityPresentationVersion);

    internal SessionPerformanceDiagnosticSnapshot DiagnosticPerformance =>
        _performanceTracker.CurrentDiagnostics;

    internal bool HasPendingPerformancePublication =>
        Volatile.Read(ref _activePerformancePublicationCount) != 0;

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

    public void SetFrameRefreshPolicy(FrameRefreshPolicy policy)
        => SetQualityProfile(_qualityProfile.WithRefresh(policy));

    public void SetQualityProfile(QualityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var controller = new AdaptiveQualityController(
            profile,
            _adaptiveQualityCapabilities,
            _qualityDecoderGates);
        CancellationTokenSource previousOperations;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            previousOperations = _qualityProfileOperations;
            _qualityProfileOperations = new CancellationTokenSource();
            _qualityProfile = profile;
            _latestQualityDecision = null;
            _latestQualityTransitionStatus = QualityTransitionStatus.NoChange;
            _refreshPolicy = profile.Refresh;
            _adaptiveQualityController = controller;
            _ = Interlocked.Increment(ref _qualityProfileEpoch);
            _ = Interlocked.Increment(ref _performanceGeneration);
            var automaticTarget = InitialAdaptiveTarget(_adaptiveQualityController);
            _pacer.SetPolicy(
                _refreshPolicy,
                automaticTarget,
                _remoteMaximum);
            _performanceTracker.SetRefreshPolicy(
                _refreshPolicy,
                ResolveTarget(_refreshPolicy, automaticTarget));
            Performance = _performanceTracker.Current;
            RefreshFrameRefreshOptions();
        }

        ObserveDetachedFailure(CancelAndDisposeAsync(previousOperations));
    }

    internal static string FormatSessionPerformance(SessionPerformanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var mode = snapshot.Mode switch
        {
            FrameRefreshMode.Automatic => snapshot.TargetFramesPerSecond is { } automaticTarget
                ? $"自动 {automaticTarget} FPS"
                : "自动 —",
            FrameRefreshMode.Fixed => snapshot.TargetFramesPerSecond is { } fixedTarget
                ? $"固定 {fixedTarget} FPS"
                : "固定 —",
            FrameRefreshMode.Unlimited => "无限",
            _ => "未知模式",
        };
        var hasSample = snapshot.SampleSequence > 0;
        var actual = hasSample && snapshot.ActualFramesPerSecond > 0
            ? $"实际 {snapshot.ActualFramesPerSecond} FPS"
            : "实际 —";
        var rate = hasSample && snapshot.ReceiveBytesPerSecond > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{snapshot.ReceiveBytesPerSecond / (1024d * 1024d):0.0} MiB/s")
            : "—";
        var encoding = hasSample
            ? FormatEncoding(snapshot.PrimaryFramebufferEncoding)
            : "—";
        var response = hasSample && snapshot.ResponseMilliseconds > 0
            ? $"{snapshot.ResponseMilliseconds} ms"
            : "—";
        return $"{mode} · {actual} · {rate} · {encoding} · {response}";
    }

    private static string FormatEncoding(int? encoding) => encoding switch
    {
        null => "—",
        (int)RfbEncodingType.Raw => "Raw",
        (int)RfbEncodingType.Zrle => "ZRLE",
        (int)RfbEncodingType.CopyRect => "CopyRect",
        (int)RfbEncodingType.DesktopSize => "DesktopSize",
        (int)RfbEncodingType.Cursor => "Cursor",
        (int)RfbEncodingType.ArdDisplayInfo => "ARD DisplayInfo",
        (int)RfbEncodingType.ArdSessionEncryption => "ARD SessionEncryption",
        (int)RfbEncodingType.ArdDisplayInfo2 => "ARD DisplayInfo2",
        var value => $"编码 {value}",
    };

    private void RefreshFrameRefreshOptions()
    {
        _frameRefreshOptions = global::WinARD.Desktop.ViewModels.FrameRefreshOptions.Create(
            _refreshPolicy,
            _remoteMaximum);
        _selectedFrameRefreshOption = _frameRefreshOptions.Single(option => option.Policy == _refreshPolicy);
        OnPropertyChanged(nameof(FrameRefreshOptions));
        OnPropertyChanged(nameof(SelectedFrameRefreshOption));
    }

    internal ValueTask PublishSessionPerformanceAsync(
        SessionPerformanceSnapshot snapshot,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var generation = Volatile.Read(ref _performanceGeneration);
        return PublishSessionPerformanceAsync(snapshot, force, generation, cancellationToken);
    }

    private ValueTask PublishSessionPerformanceAsync(
        SessionPerformanceSnapshot snapshot,
        bool force,
        long generation,
        CancellationToken cancellationToken)
    {
        return _performancePublication.ShouldPublish(force)
            ? new ValueTask(PublishSessionPerformanceCoreAsync(
                snapshot,
                generation,
                Interlocked.Increment(ref _performancePublicationSequence),
                cancellationToken))
            : ValueTask.CompletedTask;
    }

    private async Task PublishSessionPerformanceCoreAsync(
        SessionPerformanceSnapshot snapshot,
        long generation,
        long publicationSequence,
        CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _activePerformancePublicationCount);
        try
        {
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (generation != Volatile.Read(ref _performanceGeneration) ||
                        publicationSequence <= Volatile.Read(
                            ref _lastAppliedPerformancePublicationSequence))
                    {
                        return;
                    }

                    Interlocked.Exchange(
                        ref _lastAppliedPerformancePublicationSequence,
                        publicationSequence);
                    Performance = snapshot;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _activePerformancePublicationCount);
        }
    }

    internal int ActiveBestEffortUiObserverCount =>
        Volatile.Read(ref _activeBestEffortUiObserverCount);

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
        CancellationToken cancellationToken)
    {
        RecordInputOccurred(QualityInputActivityKind.Keyboard, PointerDragActive(), 1);
        return _pointerWrites.BarrierAsync(
            token => _inputMapper.KeyDownAsync(key, scanCode, isExtended, text, token),
            cancellationToken);
    }

    public ValueTask KeyUpAsync(
        Windows.System.VirtualKey key,
        int scanCode,
        bool isExtended,
        string? text,
        CancellationToken cancellationToken)
    {
        RecordInputOccurred(QualityInputActivityKind.Keyboard, PointerDragActive(), 1);
        return _pointerWrites.BarrierAsync(
            token => _inputMapper.KeyUpAsync(key, scanCode, isExtended, text, token),
            cancellationToken);
    }

    public ValueTask TextInputAsync(string text, CancellationToken cancellationToken)
    {
        RecordInputOccurred(QualityInputActivityKind.Keyboard, PointerDragActive(), 1);
        return _pointerWrites.BarrierAsync(
            token => _inputMapper.TextInputAsync(text, token),
            cancellationToken);
    }

    public ValueTask ReleaseInputAsync(CancellationToken cancellationToken)
    {
        RecordInputOccurred(QualityInputActivityKind.Keyboard, pointerDragActive: false, 1);
        return _pointerWrites.BarrierAsync(
            token => _inputMapper.ReleaseAllAsync(token),
            cancellationToken);
    }

    public ValueTask SendSecureAttentionSequenceAsync(CancellationToken cancellationToken)
    {
        RecordInputOccurred(QualityInputActivityKind.Keyboard, PointerDragActive(), 1);
        return _pointerWrites.BarrierAsync(
            token => _inputMapper.SendSecureAttentionSequenceAsync(token),
            cancellationToken);
    }

    public ValueTask SendPointerAsync(
        byte buttons,
        RemotePoint point,
        CancellationToken cancellationToken)
    {
        RecordInputOccurred(QualityInputActivityKind.Pointer, HasBasePointerButton(buttons), 1);
        return _session.SendPointerAsync(buttons, point.X, point.Y, cancellationToken);
    }

    public void QueuePointerMove(byte buttons, RemotePoint point)
    {
        if (IsInputUnavailable)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return;
            }
        }

        if (_lifetime.IsCancellationRequested)
        {
            MarkInputUnavailable();
            return;
        }

        try
        {
            RecordInputOccurred(
                QualityInputActivityKind.Pointer,
                HasBasePointerButton(buttons),
                SaturatingPendingCount(_pointerWrites.Snapshot.PendingDepth, 1));
            _pointerWrites.QueueMove(new PointerWrite(buttons, point));
        }
        catch (ObjectDisposedException) when (IsInputUnavailable)
        {
        }
    }

    public ValueTask SendPointerBarrierAsync(
        IReadOnlyList<PointerWrite> writes,
        CancellationToken token)
    {
        ThrowIfInputUnavailable();
        ArgumentNullException.ThrowIfNull(writes);
        var dragActive = writes.Count > 0 && HasBasePointerButton(writes[^1].Buttons);
        if (Interlocked.Exchange(ref _pendingScrollInput, 0) == 0)
        {
            RecordInputOccurred(
                QualityInputActivityKind.Pointer,
                dragActive,
                SaturatingPendingCount(_pointerWrites.Snapshot.PendingDepth, writes.Count));
        }
        else
        {
            _performanceTracker.SetInputActiveState(
                dragActive,
                SaturatingPendingCount(_pointerWrites.Snapshot.PendingDepth, writes.Count));
        }
        return _pointerWrites.BarrierAsync(writes, token);
    }

    public void RecordScrollInput()
    {
        lock (_sync)
        {
            if (_disposeTask is not null || _lifetime.IsCancellationRequested || IsInputUnavailable)
            {
                return;
            }

            _ = Interlocked.Exchange(ref _pendingScrollInput, 1);
            _performanceTracker.RecordInputOccurred(
                QualityInputActivityKind.Scroll,
                _performanceTracker.CurrentActivity.PointerDragActive,
                SaturatingPendingCount(_pointerWrites.Snapshot.PendingDepth, 1));
        }
    }

    internal QualityActivitySnapshot QualityActivity => _performanceTracker.CurrentActivity;

    internal QualityProfile QualityProfile => _qualityProfile;

    internal QualityDecision? LatestQualityDecision => _latestQualityDecision;

    internal QualityTransitionStatus LatestQualityTransitionStatus => _latestQualityTransitionStatus;

    internal long QualityDecisionGeneration => Interlocked.Read(ref _qualityDecisionGeneration);

    private void RecordInputOccurred(
        QualityInputActivityKind kind,
        bool pointerDragActive,
        int pendingInputCount)
    {
        lock (_sync)
        {
            if (_disposeTask is not null || _lifetime.IsCancellationRequested || IsInputUnavailable)
            {
                return;
            }

            _performanceTracker.RecordInputOccurred(
                kind,
                pointerDragActive,
                Math.Max(0, pendingInputCount));
        }
    }

    private bool PointerDragActive() => _performanceTracker.CurrentActivity.PointerDragActive;

    private static bool HasBasePointerButton(byte buttons) =>
        (buttons & (byte)(RemotePointerButtons.Left | RemotePointerButtons.Middle | RemotePointerButtons.Right)) != 0;

    private static int SaturatingPendingCount(int current, int added) =>
        current > int.MaxValue - added ? int.MaxValue : current + added;

    internal RemotePointerCoalescerSnapshot PointerCoalescerSnapshot =>
        _pointerWrites.Snapshot;

    internal bool IsInputUnavailable =>
        Volatile.Read(ref _inputUnavailable) != 0;

    public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) =>
        _session.SendClipboardTextAsync(text, cancellationToken);

    public Task ReportInputFailureAsync()
    {
        Task? handling;
        lock (_sync)
        {
            handling = _inputFailureTask;
        }

        if (handling is not null)
        {
            return handling;
        }

        QueueInputFailureStatusBestEffort();
        return Task.CompletedTask;
    }

    public void ObserveInputFailure(Exception exception)
    {
        _ = HandleInputFailureAsync(exception);
    }

    internal Task HandleInputFailureAsync(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        MarkInputUnavailable();

        TaskCompletionSource? completion = null;
        Task handling;
        lock (_sync)
        {
            if (_inputFailureTask is not null)
            {
                return _inputFailureTask;
            }

            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            handling = completion.Task;
            _inputFailureTask = handling;
        }

        _ = CompleteInputFailureHandlingAsync(exception, completion);
        return handling;
    }

    private WinArdError ObserveInputFailureCore(Exception exception)
    {
        var error = WinArdError.Create(
            ConnectionStage.Connected,
            "REMOTE_INPUT_FAILED",
            "远程输入发送失败。",
            Guid.NewGuid().ToString("N"));
        _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
            error.Code,
            error.CorrelationId,
            "Remote input operation failed.",
            Exception: exception));
        return error;
    }

    private void ThrowIfInputUnavailable()
    {
        if (IsInputUnavailable)
        {
            throw new OperationCanceledException(new CancellationToken(canceled: true));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        }

        _lifetime.Token.ThrowIfCancellationRequested();
    }

    private Task HandlePointerWriteFailureAsync(Exception exception) =>
        HandleInputFailureAsync(exception);

    private Task CompleteInputFailureHandlingAsync(
        Exception exception,
        TaskCompletionSource completion)
    {
        WinArdError? error = null;
        try
        {
            error = ObserveInputFailureCore(exception);
        }
        catch (Exception)
        {
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (error is not null)
        {
            QueueUiUpdateBestEffort(() => Error = error);
        }

        QueueInputFailureStatusBestEffort();
        completion.TrySetResult();
        return Task.CompletedTask;
    }

    private void QueueInputFailureStatusBestEffort() =>
        QueueUiUpdateBestEffort(
            () => StatusMessage = "输入发送失败，会话正在关闭。");

    private void QueueUiUpdateBestEffort(Action update)
    {
        try
        {
            var operation = _dispatcher.InvokeAsync(update, CancellationToken.None);
            ObserveDetachedFailure(operation);
            _ = Interlocked.Increment(ref _activeBestEffortUiObserverCount);
            _ = ObserveBestEffortUiUpdateAsync(operation);
        }
        catch (Exception)
        {
        }
    }

    private async Task ObserveBestEffortUiUpdateAsync(Task operation)
    {
        try
        {
            if (operation.IsCompleted)
            {
                await operation.ConfigureAwait(false);
            }
            else
            {
                await operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            _ = Interlocked.Decrement(ref _activeBestEffortUiObserverCount);
        }
    }

    private static void ObserveDetachedFailure(Task operation)
    {
        _ = operation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                MarkInputUnavailable();
                _performanceTracker.SetInputActiveState(pointerDragActive: false, pendingInputCount: 0);
                _ = Interlocked.Increment(ref _performanceGeneration);
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RequestFramebufferUpdateTrackedAsync(
                incremental: false,
                cancellationToken).ConfigureAwait(false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = await _session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                switch (message)
                {
                    case RemoteFramebufferMessage frame:
                        var frameQuality = _connectionQualityTracker.CompleteResponse();
                        var framebufferResized = FramebufferSize != frame.Size;
                        SessionFrameEnvelope envelope;
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

                                var packet = FramePacket.TakeFrom(
                                    Interlocked.Increment(ref _sequence),
                                    frame);
                                Interlocked.Exchange(ref _pendingFrameQuality, frameQuality);
                                envelope = new SessionFrameEnvelope(
                                    packet,
                                    new SessionFramePerformance(
                                        frame.Statistics,
                                        [.. frame.DirtyRectangles],
                                        frame.Size,
                                        TimeSpan.FromMilliseconds(
                                            frameQuality?.ResponseMilliseconds ?? 0)));
                                _frames.Publish(envelope);
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
                        var presentationResult = await envelope.WaitForPresentationAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (presentationResult.RepairRequestIssued)
                        {
                            TrackInternallyIssuedFramebufferRequest();
                        }
                        else
                        {
                            await RequestFramebufferUpdateTrackedAsync(
                                incremental: !framebufferResized,
                                cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    case RemoteCursorMessage cursorMessage:
                        var cursorQuality = _connectionQualityTracker.CompleteResponse();
                        using (cursorMessage)
                        {
                            await PublishCursorAsync(
                                cursorMessage.TakeCursorOwnership(),
                                cancellationToken,
                                cursorQuality).ConfigureAwait(false);
                            SessionPerformanceSnapshot performance;
                            long performanceGeneration;
                            lock (_sync)
                            {
                                performance = _performanceTracker.ObserveNonFrameUpdate(
                                    cursorMessage.Statistics,
                                    _session.PerformanceSnapshot,
                                    _pointerWrites.Snapshot);
                                performanceGeneration = _performanceGeneration;
                            }
                            await PublishSessionPerformanceAsync(
                                performance,
                                force: false,
                                performanceGeneration,
                                cancellationToken).ConfigureAwait(false);
                        }
                        await RequestFramebufferUpdateTrackedAsync(
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
                using var envelope = await _frames.ReadLatestAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var frame = envelope.Frame;
                    var presentationStarted = _timeProvider.GetTimestamp();
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (Interlocked.Exchange(ref _pendingFrameQuality, null) is { } quality)
                        {
                            PublishConnectionQuality(quality);
                        }

                        _presenter.Resize(frame.Width, frame.Height);
                        _presenter.Present(frame.Pixels.Span, frame.Stride, frame.DirtyRectangles);
                    }, cancellationToken).ConfigureAwait(false);
                    var presentation = _timeProvider.GetElapsedTime(
                        presentationStarted,
                        _timeProvider.GetTimestamp());
                    var transitionStatus = await ObservePerformanceAndQualityAsync(
                        envelope.Performance,
                        presentation,
                        cancellationToken).ConfigureAwait(false);
                    if (transitionStatus == QualityTransitionStatus.Faulted)
                    {
                        throw new InvalidOperationException("The adaptive quality transition failed.");
                    }

                    envelope.CompletePresentation(new SessionPresentationResult(
                        transitionStatus,
                        RepairRequestIssued: transitionStatus == QualityTransitionStatus.Applied));
                }
                catch (Exception exception)
                {
                    envelope.FailPresentation(exception);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask RequestFramebufferUpdateTrackedAsync(
        bool incremental,
        CancellationToken cancellationToken)
    {
        await _pacer.WaitForNextRequestAsync(cancellationToken).ConfigureAwait(false);
        await _session.RequestFramebufferUpdateAsync(incremental, cancellationToken)
            .ConfigureAwait(false);
        _pacer.MarkRequestStarted();
        _connectionQualityTracker.BeginRequest();
    }

    private void TrackInternallyIssuedFramebufferRequest()
    {
        _pacer.MarkRequestStarted();
        _connectionQualityTracker.BeginRequest();
    }

    private async Task<QualityTransitionStatus> ObservePerformanceAndQualityAsync(
        SessionFramePerformance pending,
        TimeSpan presentation,
        CancellationToken cancellationToken)
    {
        var runtime = _session.PerformanceSnapshot;
        var targetChanged = false;
        SessionPerformanceSnapshot snapshot;
        QualityDecision decision;
        bool constraintsSatisfied;
        long profileEpoch;
        CancellationToken profileCancellationToken;
        long performanceGeneration;
        lock (_sync)
        {
            snapshot = _performanceTracker.ObserveFrame(
                pending.Update,
                pending.Dirty,
                pending.Size,
                pending.Response,
                presentation,
                runtime,
                _pointerWrites.Snapshot);
            _performanceTracker.SetInputActiveState(
                _performanceTracker.CurrentActivity.PointerDragActive,
                SaturatingPendingCount(
                    Math.Max(0, runtime.InputQueueDepth),
                    _pointerWrites.Snapshot.PendingDepth));
            var localDecision = _adaptiveQualityController.Observe(
                _performanceTracker.CreateQualityObservation());
            decision = WithGlobalGeneration(localDecision);
            constraintsSatisfied = _adaptiveQualityController.ConstraintsSatisfied;
            profileEpoch = _qualityProfileEpoch;
            profileCancellationToken = _qualityProfileOperations.Token;
            var target = ResolveTarget(_refreshPolicy, decision.TargetFramesPerSecond);
            targetChanged = snapshot.TargetFramesPerSecond != target;
            if (targetChanged)
            {
                _performanceTracker.RecordAutomaticTargetChange();
                _ = Interlocked.Increment(ref _performanceGeneration);
            }

            _pacer.SetPolicy(_refreshPolicy, decision.TargetFramesPerSecond, _remoteMaximum);
            _performanceTracker.SetRefreshPolicy(_refreshPolicy, target);
            snapshot = _performanceTracker.Current;
            performanceGeneration = _performanceGeneration;
        }
        await PublishSessionPerformanceAsync(
            snapshot,
            targetChanged,
            performanceGeneration,
            cancellationToken).ConfigureAwait(false);
        var performancePublished = ReferenceEquals(Performance, snapshot);
        using var transitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            profileCancellationToken);
        var gateAcquired = false;
        var transitionStatus = QualityTransitionStatus.NoChange;
        try
        {
            await _qualityOperationGate.WaitAsync(transitionCancellation.Token).ConfigureAwait(false);
            gateAcquired = true;
            if (profileEpoch != Volatile.Read(ref _qualityProfileEpoch))
            {
                transitionStatus = QualityTransitionStatus.NoChange;
            }
            else if (!constraintsSatisfied)
            {
                transitionStatus = QualityTransitionStatus.CapabilityUnavailable;
            }
            else
            {
                transitionStatus = await _qualityTransitionCoordinator.ApplyAtSafeBoundaryAsync(
                    decision,
                    new QualityTransitionBoundary(
                        FrameResponseCompletedAndPresented: true,
                        HasOutstandingFramebufferRequest: false,
                        HasActiveReceive: false,
                        NextFramebufferRequestProduced: false),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            !gateAcquired &&
            profileCancellationToken.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            transitionStatus = QualityTransitionStatus.NoChange;
        }
        finally
        {
            if (gateAcquired)
            {
                _qualityOperationGate.Release();
            }
        }

        bool presentationChanged;
        lock (_sync)
        {
            if (profileEpoch != _qualityProfileEpoch)
            {
                return transitionStatus;
            }

            var previousDecision = _latestQualityDecision;
            presentationChanged = previousDecision is null ||
                previousDecision.Color != decision.Color ||
                previousDecision.Scale != decision.Scale ||
                previousDecision.TargetFramesPerSecond != decision.TargetFramesPerSecond ||
                previousDecision.Reason != decision.Reason ||
                previousDecision.TargetSatisfied != decision.TargetSatisfied ||
                _latestQualityTransitionStatus != transitionStatus;
            _latestQualityDecision = decision;
            _latestQualityTransitionStatus = transitionStatus;
            if (presentationChanged || performancePublished)
            {
                _ = Interlocked.Increment(ref _qualityPresentationVersion);
            }
        }

        if (presentationChanged || performancePublished)
        {
            await _dispatcher.InvokeAsync(
                () => OnPropertyChanged(nameof(QualityPresentationVersion)),
                cancellationToken).ConfigureAwait(false);
        }

        return transitionStatus;
    }

    private int? ResolveTarget(FrameRefreshPolicy policy, int automaticTarget) => policy.Mode switch
    {
        FrameRefreshMode.Automatic => _remoteMaximum is { } maximum
            ? Math.Min(automaticTarget, maximum)
            : automaticTarget,
        FrameRefreshMode.Fixed => policy.EffectiveMaximum(_remoteMaximum),
        FrameRefreshMode.Unlimited => null,
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    private QualityDecision WithGlobalGeneration(QualityDecision decision) => new(
        Interlocked.Increment(ref _qualityDecisionGeneration),
        decision.ContentState,
        decision.Level,
        decision.Color,
        decision.Scale,
        decision.TargetFramesPerSecond,
        decision.Reason,
        decision.TargetSatisfied,
        decision.LevelChanged,
        decision.ContentStateChanged,
        decision.PreviousLevel,
        decision.PreviousContentState);

    private static int InitialAdaptiveTarget(AdaptiveQualityController controller) =>
        AdaptiveQualityController.QualityTable[(int)controller.CurrentLevel].FramesPerSecond;

    private static ArdDisplayCapabilities WithMaximumRefreshRate(
        ArdDisplayCapabilities capabilities,
        int? fallbackMaximumRefreshRate) => new(
        capabilities.Zlib,
        capabilities.Rgb565,
        capabilities.ServerScaling,
        capabilities.AppleColor1002,
        capabilities.AppleGrayscale1001,
        capabilities.SafeOnlinePixelFormatSwitch,
        capabilities.SafeOnlineScaleSwitch,
        capabilities.MaximumRefreshRate ?? fallbackMaximumRefreshRate);

    private int? ValidateRemoteMaximum(int? maximum)
    {
        if (maximum is null)
        {
            return null;
        }

        if (maximum is >= 30 and <= 240)
        {
            return maximum;
        }

        _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
            "REMOTE_DISPLAY_CAPABILITIES_INVALID",
            Guid.NewGuid().ToString("N"),
            "Remote display capabilities were ignored.",
            [new("Category", "InvalidRefreshRateRange")]));
        return null;
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? failures = null;
        CancelLifetimeBestEffort();
        _pointerWrites.AbortActiveWrites();
        await CaptureFailureAsync(
            _pointerWrites.DisposeAsync().AsTask(),
            failures ??= []).ConfigureAwait(false);
        var ownershipDisposal = DisposeOwnershipOnceAsync();
        var monitor = _monitorTask;
        if (monitor is not null)
        {
            await CaptureFailureAsync(monitor, failures ??= []).ConfigureAwait(false);
        }
        else
        {
            await PublishDisconnectedBestEffortAsync().ConfigureAwait(false);
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
        try
        {
            _qualityTransitionCoordinator.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        _qualityProfileOperations.Dispose();
        _qualityOperationGate.Dispose();
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

    private static async Task CancelAndDisposeAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
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

        var pending = new Stack<ExceptionTraversalFrame>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(new ExceptionTraversalFrame(exception, SelfVisited: false, NextChildIndex: 0));
        var inspectedEdges = 0;
        while (pending.Count > 0)
        {
            var frame = pending.Pop();
            if (!frame.SelfVisited)
            {
                if (visited.Count >= MaxProtocolFailureExceptionNodes)
                {
                    if (visited.Contains(frame.Exception))
                    {
                        continue;
                    }

                    return null;
                }

                if (!visited.Add(frame.Exception))
                {
                    continue;
                }

                if (frame.Exception is RfbProtocolException { Failure: not null } protocolException)
                {
                    return protocolException;
                }

                if (visited.Count >= MaxProtocolFailureExceptionNodes)
                {
                    return null;
                }

                if (frame.Exception is AggregateException aggregateException)
                {
                    if (aggregateException.InnerExceptions.Count == 0)
                    {
                        continue;
                    }

                    if (inspectedEdges >= MaxProtocolFailureExceptionEdgeInspections ||
                        pending.Count > MaxProtocolFailureExceptionFrames - 2)
                    {
                        return null;
                    }

                    inspectedEdges++;
                    pending.Push(new ExceptionTraversalFrame(
                        frame.Exception,
                        SelfVisited: true,
                        NextChildIndex: 1));
                    pending.Push(new ExceptionTraversalFrame(
                        aggregateException.InnerExceptions[0],
                        SelfVisited: false,
                        NextChildIndex: 0));
                }
                else if (frame.Exception.InnerException is { } innerException)
                {
                    if (inspectedEdges >= MaxProtocolFailureExceptionEdgeInspections ||
                        pending.Count >= MaxProtocolFailureExceptionFrames)
                    {
                        return null;
                    }

                    inspectedEdges++;
                    pending.Push(new ExceptionTraversalFrame(
                        innerException,
                        SelfVisited: false,
                        NextChildIndex: 0));
                }

                continue;
            }

            if (visited.Count >= MaxProtocolFailureExceptionNodes ||
                inspectedEdges >= MaxProtocolFailureExceptionEdgeInspections)
            {
                return null;
            }

            var aggregate = (AggregateException)frame.Exception;
            if (frame.NextChildIndex >= aggregate.InnerExceptions.Count)
            {
                continue;
            }

            var hasMoreChildren = frame.NextChildIndex + 1 < aggregate.InnerExceptions.Count;
            var requiredFrames = hasMoreChildren ? 2 : 1;
            if (pending.Count > MaxProtocolFailureExceptionFrames - requiredFrames)
            {
                return null;
            }

            inspectedEdges++;
            if (hasMoreChildren)
            {
                pending.Push(frame with { NextChildIndex = frame.NextChildIndex + 1 });
            }

            pending.Push(new ExceptionTraversalFrame(
                aggregate.InnerExceptions[frame.NextChildIndex],
                SelfVisited: false,
                NextChildIndex: 0));
        }

        return null;
    }

    private readonly record struct ExceptionTraversalFrame(
        Exception Exception,
        bool SelfVisited,
        int NextChildIndex);

    private static List<DiagnosticField>? GetProtocolFailureFields(
        RfbProtocolException? exception)
    {
        if (exception?.Failure is not { } failure)
        {
            return null;
        }

        var fields = new List<DiagnosticField>(9)
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

        if (failure.ArdEncryptionStage is { } ardEncryptionStage)
        {
            fields.Add(new DiagnosticField(
                "ArdEncryptionStage",
                ardEncryptionStage.ToString(),
                DiagnosticFieldCategory.Public));
        }

        if (failure.ArdEncryptionDirection is { } ardEncryptionDirection)
        {
            fields.Add(new DiagnosticField(
                "ArdEncryptionDirection",
                ardEncryptionDirection.ToString(),
                DiagnosticFieldCategory.Public));
        }

        if (failure.ArdEncryptionSequence is { } ardEncryptionSequence)
        {
            fields.Add(new DiagnosticField(
                "ArdEncryptionSequence",
                ardEncryptionSequence.ToString(CultureInfo.InvariantCulture),
                DiagnosticFieldCategory.Public));
        }

        if (failure.ArdCiphertextLength is { } ardCiphertextLength)
        {
            fields.Add(new DiagnosticField(
                "ArdCiphertextLength",
                ardCiphertextLength.ToString(CultureInfo.InvariantCulture),
                DiagnosticFieldCategory.Public));
        }

        return fields;
    }

    private async Task MonitorLoopsAsync(Task receive, Task present)
    {
        try
        {
            var completed = await Task.WhenAny(receive, present).ConfigureAwait(false);
            MarkInputUnavailable();
            var wasTerminalFailure = completed.IsFaulted && !_lifetime.IsCancellationRequested;
            CancelLifetimeBestEffort();

            _pointerWrites.AbortActiveWrites();
            await ObserveFailureAsync(_pointerWrites.DisposeAsync().AsTask()).ConfigureAwait(false);
            var ownershipDisposal = DisposeOwnershipOnceAsync();
            if (wasTerminalFailure)
            {
                var protocolException = ReferenceEquals(completed, receive)
                    ? FindProtocolFailureException(completed.Exception)
                    : null;
                var remoteSessionClosed = protocolException?.Failure?.Kind ==
                    RfbProtocolFailureKind.RemoteSessionClosed;
                var exception = protocolException ?? completed.Exception?.GetBaseException() ??
                    new InvalidOperationException("Remote session terminated unexpectedly.");
                var error = WinArdError.Create(
                    ConnectionStage.Connected,
                    ReferenceEquals(completed, present)
                        ? "REMOTE_PRESENTATION_FAILED"
                        : "REMOTE_SESSION_INTERRUPTED",
                    remoteSessionClosed ? "远程主机已结束共享会话。" : "远程会话已中断。",
                    Guid.NewGuid().ToString("N"));
                var status = ReferenceEquals(completed, present)
                    ? "画面呈现失败，会话正在关闭。"
                    : remoteSessionClosed ? "远程主机已结束会话。" : "连接已中断。";
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
            await PublishDisconnectedBestEffortAsync().ConfigureAwait(false);
            _completion.TrySetResult();
        }
    }

    private async Task PublishDisconnectedBestEffortAsync()
    {
        try
        {
            var disconnected = _connectionQualityTracker.Disconnect();
            await _dispatcher.InvokeAsync(
                () => PublishConnectionQuality(disconnected),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Terminal cleanup must not depend on quality status reporting.
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

    private void MarkInputUnavailable() =>
        _ = Interlocked.Exchange(ref _inputUnavailable, 1);

    private void CancelLifetimeBestEffort()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task PublishCursorAsync(
        RemoteCursorUpdate cursor,
        CancellationToken cancellationToken,
        ConnectionQualitySnapshot? quality = null)
    {
        try
        {
            if (!cursor.IsVisible)
            {
                cursor.Dispose();
                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        RemoteCursor = null;
                        if (quality is not null)
                        {
                            PublishConnectionQuality(quality);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await _dispatcher.InvokeAsync(
                () =>
                {
                    RemoteCursor = cursor;
                    if (quality is not null)
                    {
                        PublishConnectionQuality(quality);
                    }
                },
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

    private void PublishConnectionQuality(ConnectionQualitySnapshot quality)
    {
        if (_connectionQualityPublicationGate.TryAccept(quality))
        {
            ConnectionQuality = quality;
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

internal sealed class SessionPerformancePublicationGate
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minimumInterval;
    private long _lastPublished;
    private bool _hasPublished;

    public SessionPerformancePublicationGate(
        TimeProvider timeProvider,
        TimeSpan minimumInterval)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimumInterval, TimeSpan.Zero);
        _minimumInterval = minimumInterval;
    }

    public bool ShouldPublish(bool force)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetTimestamp();
            if (!force &&
                _hasPublished &&
                _timeProvider.GetElapsedTime(_lastPublished, now) < _minimumInterval)
            {
                return false;
            }

            _lastPublished = now;
            _hasPublished = true;
            return true;
        }
    }
}

internal sealed record SessionFramePerformance(
    RemoteUpdateStatistics Update,
    IReadOnlyList<RemoteRectangle> Dirty,
    RemoteFramebufferSize Size,
    TimeSpan Response)
{
    public SessionFramePerformance MergeFrom(
        SessionFramePerformance earlier,
        IReadOnlyList<RemoteRectangle> mergedDirty)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(mergedDirty);
        return this with
        {
            Update = MergeStatistics(earlier.Update, Update),
            Dirty = [.. mergedDirty],
        };
    }

    private static RemoteUpdateStatistics MergeStatistics(
        RemoteUpdateStatistics earlier,
        RemoteUpdateStatistics later)
    {
        var counts = earlier.EncodingCounts
            .Where(pair => pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var (encoding, count) in later.EncodingCounts)
        {
            if (count <= 0)
            {
                continue;
            }

            counts.TryGetValue(encoding, out var previous);
            counts[encoding] = previous > int.MaxValue - count
                ? int.MaxValue
                : previous + count;
        }

        var bytes = earlier.ReceivedSessionBytes >
            long.MaxValue - later.ReceivedSessionBytes
                ? long.MaxValue
                : earlier.ReceivedSessionBytes + later.ReceivedSessionBytes;
        return new RemoteUpdateStatistics(bytes, counts);
    }
}

internal sealed class SessionFrameEnvelope(
    FramePacket frame,
    SessionFramePerformance performance) : IDisposable
{
    private FramePacket? _frame = frame ?? throw new ArgumentNullException(nameof(frame));
    private readonly TaskCompletionSource<SessionPresentationResult> _presentationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _presentationFailed;

    public FramePacket Frame =>
        _frame ?? throw new ObjectDisposedException(nameof(SessionFrameEnvelope));

    public SessionFramePerformance Performance { get; private set; } =
        performance ?? throw new ArgumentNullException(nameof(performance));

    public void MergeFrom(SessionFrameEnvelope dropped)
    {
        ArgumentNullException.ThrowIfNull(dropped);
        Frame.MergeDirtyRectanglesFrom(dropped.Frame);
        Performance = Performance.MergeFrom(dropped.Performance, Frame.DirtyRectangles);
    }

    public Task<SessionPresentationResult> WaitForPresentationAsync(CancellationToken cancellationToken) =>
        _presentationCompletion.Task.WaitAsync(cancellationToken);

    public void CompletePresentation(SessionPresentationResult result = default) =>
        _presentationCompletion.TrySetResult(result);

    public void FailPresentation(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _ = Interlocked.Exchange(ref _presentationFailed, 1);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _frame, null)?.Dispose();
        if (Volatile.Read(ref _presentationFailed) == 0)
        {
            _presentationCompletion.TrySetResult(default);
        }
    }

}

internal readonly record struct SessionPresentationResult(
    QualityTransitionStatus TransitionStatus,
    bool RepairRequestIssued);

internal sealed class SessionFrameMailbox : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _available = new(0, 1);
    private readonly CancellationTokenSource _disposed = new();
    private SessionFrameEnvelope? _latest;
    private TaskCompletionSource? _disposeCompletion;
    private int _activeReaders;
    private bool _isDisposed;
    private bool _resourcesDisposed;
    private bool _cancellationIssued;
    private readonly SessionFrameMailboxTestCoordinator? _testCoordinator;

    public SessionFrameMailbox(SessionFrameMailboxTestCoordinator? testCoordinator = null)
    {
        _testCoordinator = testCoordinator;
    }

    internal bool ResourcesDisposed
    {
        get
        {
            lock (_sync)
            {
                return _resourcesDisposed;
            }
        }
    }

    public void Publish(SessionFrameEnvelope frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        SessionFrameEnvelope? replaced;
        bool signal;
        lock (_sync)
        {
            if (_isDisposed)
            {
                frame.Dispose();
                throw new ObjectDisposedException(nameof(SessionFrameMailbox));
            }

            replaced = _latest;
            if (replaced is not null)
            {
                frame.MergeFrom(replaced);
            }

            _latest = frame;
            signal = replaced is null;
            if (signal)
            {
                _testCoordinator?.PauseAt(SessionFrameMailboxTestPause.BeforeSignal);
                _available.Release();
            }
        }

        replaced?.Dispose();
    }

    public async ValueTask<SessionFrameEnvelope> ReadLatestAsync(
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_isDisposed)
            {
                throw new OperationCanceledException(new CancellationToken(canceled: true));
            }

            _activeReaders++;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposed.Token);
            await _available.WaitAsync(linked.Token).ConfigureAwait(false);
            SessionFrameEnvelope frame;
            lock (_sync)
            {
                if (_isDisposed)
                {
                    throw new OperationCanceledException(_disposed.Token);
                }

                frame = Interlocked.Exchange(ref _latest, null) ??
                    throw new InvalidOperationException("The frame signal did not have a frame.");
            }

            _testCoordinator?.PauseAt(SessionFrameMailboxTestPause.AfterTake);
            return frame;
        }
        finally
        {
            var completeDisposal = false;
            lock (_sync)
            {
                _activeReaders--;
                completeDisposal = _isDisposed && _cancellationIssued && _activeReaders == 0;
            }

            if (completeDisposal)
            {
                CompleteResourceDisposal();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _testCoordinator?.NotifyDisposeStarted();
        SessionFrameEnvelope? pending;
        Task disposal;
        var completeDisposal = false;
        lock (_sync)
        {
            if (_disposeCompletion is not null)
            {
                return new ValueTask(_disposeCompletion.Task);
            }

            _disposeCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = _disposeCompletion.Task;
            _isDisposed = true;
            pending = Interlocked.Exchange(ref _latest, null);
            completeDisposal = _activeReaders == 0;
        }

        pending?.Dispose();
        _disposed.Cancel();
        lock (_sync)
        {
            _cancellationIssued = true;
            completeDisposal = _activeReaders == 0;
        }
        _testCoordinator?.NotifyDisposeCancellationIssued();

        if (completeDisposal)
        {
            CompleteResourceDisposal();
        }

        return new ValueTask(disposal);
    }

    private void CompleteResourceDisposal()
    {
        TaskCompletionSource? completion;
        lock (_sync)
        {
            if (_resourcesDisposed)
            {
                return;
            }

            if (!_cancellationIssued || _activeReaders != 0)
            {
                return;
            }

            _available.Dispose();
            _disposed.Dispose();
            _resourcesDisposed = true;
            completion = _disposeCompletion;
        }

        completion?.TrySetResult();
    }
}

internal enum SessionFrameMailboxTestPause
{
    AfterTake,
    BeforeSignal,
}

internal sealed class SessionFrameMailboxTestCoordinator
{
    private readonly SessionFrameMailboxTestPause _pause;
    private readonly TaskCompletionSource _paused = NewSignal();
    private readonly TaskCompletionSource _release = NewSignal();
    private readonly TaskCompletionSource _disposeStarted = NewSignal();
    private readonly TaskCompletionSource _disposeCancellationIssued = NewSignal();

    public SessionFrameMailboxTestCoordinator(SessionFrameMailboxTestPause pause)
    {
        _pause = pause;
    }

    public Task Paused => _paused.Task;

    public Task DisposeStarted => _disposeStarted.Task;

    public Task DisposeCancellationIssued => _disposeCancellationIssued.Task;

    public void Release() => _release.TrySetResult();

    internal void PauseAt(SessionFrameMailboxTestPause pause)
    {
        if (pause != _pause)
        {
            return;
        }

        _paused.TrySetResult();
        _release.Task.GetAwaiter().GetResult();
    }

    internal void NotifyDisposeStarted() => _disposeStarted.TrySetResult();

    internal void NotifyDisposeCancellationIssued() =>
        _disposeCancellationIssued.TrySetResult();

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
