using System.Runtime.ExceptionServices;
using System.Diagnostics;
using WinARD.Application.Ports;

namespace WinARD.Desktop.Services;

internal sealed class ClientMessageScheduler : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly int _maximumInputBatch;
    private readonly Queue<WorkItem> _inputQueue = new();
    private readonly Queue<WorkItem> _backgroundQueue = new();
    private readonly SemaphoreSlim _signal = new(initialCount: 0, maxCount: 1);
    private readonly CancellationTokenSource _activeWriteShutdown = new();
    private readonly Task _workerTask;
    private ExceptionDispatchInfo? _fault;
    private Task? _disposeTask;
    private int _consecutiveInputWrites;
    private int _queuedInputDepth;
    private int _lastInputWriteMilliseconds;
    private long _performanceSequence;
    private RemoteRuntimePerformanceSnapshot _performanceSnapshot =
        RemoteRuntimePerformanceSnapshot.Empty;
    private bool _writeActive;
    private bool _signalPending;
    private bool _activeWritesAborted;
    private bool _resourcesDisposed;
    private bool _disposed;

    public ClientMessageScheduler(int maximumInputBatch = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInputBatch);

        _maximumInputBatch = maximumInputBatch;
        _workerTask = Task.Run(ProcessAsync);
    }

    public RemoteRuntimePerformanceSnapshot PerformanceSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _performanceSnapshot;
            }
        }
    }

    public ValueTask EnqueueInputAsync(
        Func<CancellationToken, ValueTask> write,
        CancellationToken token) =>
        EnqueueAsync(write, isInput: true, token);

    public ValueTask EnqueueBackgroundAsync(
        Func<CancellationToken, ValueTask> write,
        CancellationToken token) =>
        EnqueueAsync(write, isInput: false, token);

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? disposalCompletion;
        List<CancellationTokenRegistration> registrations = [];
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            disposalCompletion = NewCompletion();
            _disposeTask = disposalCompletion.Task;
            CancelPendingNoLock(registrations);
            SignalWorkerNoLock();
        }

        DisposeRegistrations(registrations);
        _ = CompleteDisposeAsync(disposalCompletion);
        return new ValueTask(disposalCompletion.Task);
    }

    internal void AbortActiveWrites()
    {
        List<CancellationTokenRegistration> registrations = [];
        lock (_sync)
        {
            if (_activeWritesAborted)
            {
                return;
            }

            _activeWritesAborted = true;
            CancelPendingNoLock(registrations);
            if (!_resourcesDisposed)
            {
                SignalWorkerNoLock();
            }
        }

        DisposeRegistrations(registrations);
        try
        {
            _activeWriteShutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private ValueTask EnqueueAsync(
        Func<CancellationToken, ValueTask> write,
        bool isInput,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (token.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(token);
        }

        WorkItem workItem;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fault is not null)
            {
                return ValueTask.FromException(_fault.SourceException);
            }

            if (_activeWritesAborted)
            {
                return ValueTask.FromCanceled(new CancellationToken(canceled: true));
            }

            if (token.IsCancellationRequested)
            {
                return ValueTask.FromCanceled(token);
            }

            workItem = new WorkItem(write, isInput, token);
            if (isInput)
            {
                _inputQueue.Enqueue(workItem);
                _queuedInputDepth++;
                PublishPerformanceNoLock();
            }
            else
            {
                _backgroundQueue.Enqueue(workItem);
            }

            if (token.CanBeCanceled)
            {
                workItem.CancellationRegistration = token.Register(
                    static state =>
                    {
                        var cancellationState = (CancellationState)state!;
                        cancellationState.Owner.CancelUnstarted(cancellationState.WorkItem);
                    },
                    new CancellationState(this, workItem));
            }

            SignalWorkerNoLock();
        }

        return new ValueTask(workItem.Completion.Task);
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _signalPending = false;
                if (ShouldExitNoLock())
                {
                    return;
                }
            }

            while (TryTakeNext(out var workItem))
            {
                var continueProcessing = await ExecuteAsync(workItem).ConfigureAwait(false);
                lock (_sync)
                {
                    _writeActive = false;
                }

                if (!continueProcessing)
                {
                    return;
                }
            }

            lock (_sync)
            {
                if (ShouldExitNoLock())
                {
                    return;
                }

                if (HasPendingNoLock())
                {
                    SignalWorkerNoLock();
                }
            }
        }
    }

    private bool TryTakeNext(out WorkItem workItem)
    {
        WorkItem? selected = null;
        List<CancellationTokenRegistration> registrations = [];
        lock (_sync)
        {
            while (!ShouldExitNoLock() && HasPendingNoLock())
            {
                var takeInput = _inputQueue.Count > 0 &&
                    (_backgroundQueue.Count == 0 || _consecutiveInputWrites < _maximumInputBatch);
                var candidate = takeInput
                    ? _inputQueue.Dequeue()
                    : _backgroundQueue.Dequeue();
                if (candidate.Canceled)
                {
                    registrations.Add(candidate.CancellationRegistration);
                    continue;
                }

                candidate.Started = true;
                if (candidate.IsInput && candidate.CountedInInputDepth)
                {
                    _queuedInputDepth--;
                    candidate.CountedInInputDepth = false;
                    PublishPerformanceNoLock();
                }
                registrations.Add(candidate.CancellationRegistration);
                if (takeInput)
                {
                    _consecutiveInputWrites = Math.Min(
                        _maximumInputBatch,
                        _consecutiveInputWrites + 1);
                }
                else
                {
                    _consecutiveInputWrites = 0;
                }

                _writeActive = true;
                selected = candidate;
                break;
            }
        }

        DisposeRegistrations(registrations);
        workItem = selected!;
        return selected is not null;
    }

    private async Task<bool> ExecuteAsync(WorkItem workItem)
    {
        CancellationTokenSource? linkedCancellation = null;
        var writeToken = _activeWriteShutdown.Token;
        if (workItem.CancellationToken.CanBeCanceled)
        {
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                workItem.CancellationToken,
                _activeWriteShutdown.Token);
            writeToken = linkedCancellation.Token;
        }

        try
        {
            await workItem.Write(writeToken).ConfigureAwait(false);
            if (workItem.IsInput)
            {
                lock (_sync)
                {
                    _lastInputWriteMilliseconds = (int)Math.Clamp(
                        Math.Round(
                            Stopwatch.GetElapsedTime(workItem.EnqueuedTimestamp).TotalMilliseconds,
                            MidpointRounding.AwayFromZero),
                        0,
                        int.MaxValue);
                    PublishPerformanceNoLock();
                }
            }
            workItem.Completion.TrySetResult();
            return true;
        }
        catch (OperationCanceledException) when (_activeWriteShutdown.IsCancellationRequested)
        {
            workItem.Completion.TrySetCanceled(_activeWriteShutdown.Token);
            return false;
        }
        catch (OperationCanceledException) when (workItem.CancellationToken.IsCancellationRequested)
        {
            workItem.Completion.TrySetCanceled(workItem.CancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            List<CancellationTokenRegistration> registrations = [];
            lock (_sync)
            {
                _fault ??= ExceptionDispatchInfo.Capture(exception);
                FailPendingNoLock(_fault.SourceException, registrations);
            }

            DisposeRegistrations(registrations);
            workItem.Completion.TrySetException(_fault.SourceException);
            return false;
        }
        finally
        {
            linkedCancellation?.Dispose();
        }
    }

    private void CancelUnstarted(WorkItem workItem)
    {
        lock (_sync)
        {
            if (workItem.Started || workItem.Canceled)
            {
                return;
            }

            workItem.Canceled = true;
            RemoveInputDepthNoLock(workItem);
            workItem.Completion.TrySetCanceled(workItem.CancellationToken);
        }
    }

    private bool ShouldExitNoLock() =>
        _fault is not null || _activeWritesAborted || (_disposed && !_writeActive);

    private bool HasPendingNoLock() =>
        _inputQueue.Count > 0 || _backgroundQueue.Count > 0;

    private void CancelPendingNoLock(List<CancellationTokenRegistration> registrations)
    {
        var canceledToken = new CancellationToken(canceled: true);
        CancelQueueNoLock(_inputQueue, registrations, canceledToken);
        CancelQueueNoLock(_backgroundQueue, registrations, canceledToken);
    }

    private void CancelQueueNoLock(
        Queue<WorkItem> queue,
        List<CancellationTokenRegistration> registrations,
        CancellationToken canceledToken)
    {
        while (queue.Count > 0)
        {
            var workItem = queue.Dequeue();
            registrations.Add(workItem.CancellationRegistration);
            if (workItem.Canceled)
            {
                continue;
            }

            workItem.Canceled = true;
            RemoveInputDepthNoLock(workItem);
            workItem.Completion.TrySetCanceled(canceledToken);
        }
    }

    private void FailPendingNoLock(
        Exception exception,
        List<CancellationTokenRegistration> registrations)
    {
        FailQueueNoLock(_inputQueue, exception, registrations);
        FailQueueNoLock(_backgroundQueue, exception, registrations);
    }

    private void FailQueueNoLock(
        Queue<WorkItem> queue,
        Exception exception,
        List<CancellationTokenRegistration> registrations)
    {
        while (queue.Count > 0)
        {
            var workItem = queue.Dequeue();
            registrations.Add(workItem.CancellationRegistration);
            if (workItem.Canceled)
            {
                continue;
            }

            workItem.Canceled = true;
            RemoveInputDepthNoLock(workItem);
            workItem.Completion.TrySetException(exception);
        }
    }

    private void SignalWorkerNoLock()
    {
        if (_signalPending || _resourcesDisposed)
        {
            return;
        }

        _signalPending = true;
        _signal.Release();
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await _workerTask.ConfigureAwait(false);
            lock (_sync)
            {
                _activeWriteShutdown.Dispose();
                _signal.Dispose();
                _resourcesDisposed = true;
            }

            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static void DisposeRegistrations(
        IEnumerable<CancellationTokenRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            registration.Dispose();
        }
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RemoveInputDepthNoLock(WorkItem workItem)
    {
        if (!workItem.IsInput || !workItem.CountedInInputDepth)
        {
            return;
        }

        _queuedInputDepth--;
        workItem.CountedInInputDepth = false;
        PublishPerformanceNoLock();
    }

    private void PublishPerformanceNoLock() =>
        _performanceSnapshot = new RemoteRuntimePerformanceSnapshot(
            _lastInputWriteMilliseconds,
            Math.Max(0, _queuedInputDepth),
            ++_performanceSequence);

    private sealed class WorkItem(
        Func<CancellationToken, ValueTask> write,
        bool isInput,
        CancellationToken cancellationToken)
    {
        public Func<CancellationToken, ValueTask> Write { get; } = write;

        public CancellationToken CancellationToken { get; } = cancellationToken;
        public bool IsInput { get; } = isInput;
        public bool CountedInInputDepth { get; set; } = isInput;
        public long EnqueuedTimestamp { get; } = Stopwatch.GetTimestamp();

        public TaskCompletionSource Completion { get; } = NewCompletion();

        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public bool Started { get; set; }

        public bool Canceled { get; set; }
    }

    private sealed record CancellationState(
        ClientMessageScheduler Owner,
        WorkItem WorkItem);
}
