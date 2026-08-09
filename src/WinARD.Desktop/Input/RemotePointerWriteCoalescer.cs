using WinARD.Application.Ports;

namespace WinARD.Desktop.Input;

public readonly record struct PointerWrite(byte Buttons, RemotePoint Point);

internal readonly record struct RemotePointerCoalescerSnapshot(
    long CoalescedMoves,
    int PendingDepth);

internal sealed class RemotePointerWriteCoalescer : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<PointerWrite, CancellationToken, ValueTask> _sender;
    private readonly Func<Exception, Task> _faultHandler;
    private readonly Queue<WorkItem> _queue = new();
    private readonly SemaphoreSlim _signal = new(initialCount: 0, maxCount: 1);
    private readonly CancellationTokenSource _activeWriteShutdown = new();
    private readonly Task _workerTask;
    private TaskCompletionSource? _idleCompletion;
    private PointerWrite? _pendingMove;
    private Task? _disposeTask;
    private Exception? _fault;
    private long _coalescedMoves;
    private int _pendingDepth;
    private int _faultHandlerInvoked;
    private int _workerStartCount;
    private int _idleWaiterCreationCount;
    private bool _senderActive;
    private bool _signalPending;
    private bool _activeWritesAborted;
    private bool _resourcesDisposed;
    private bool _disposed;

    public RemotePointerWriteCoalescer(
        Func<PointerWrite, CancellationToken, ValueTask> sender,
        Func<Exception, Task>? faultHandler = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _faultHandler = faultHandler ?? (_ => Task.CompletedTask);
        _workerStartCount = 1;
        _workerTask = Task.Run(ProcessAsync);
    }

    internal int WorkerStartCount => Volatile.Read(ref _workerStartCount);

    internal int IdleWaiterCreationCount
    {
        get
        {
            lock (_sync)
            {
                return _idleWaiterCreationCount;
            }
        }
    }

    public RemotePointerCoalescerSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new RemotePointerCoalescerSnapshot(
                    Interlocked.Read(ref _coalescedMoves),
                    _pendingDepth);
            }
        }
    }

    public void QueueMove(PointerWrite write)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fault is not null || _activeWritesAborted)
            {
                return;
            }

            if (_pendingMove.HasValue)
            {
                _ = Interlocked.Increment(ref _coalescedMoves);
            }
            else
            {
                _pendingDepth++;
            }

            _pendingMove = write;
            SignalWorkerNoLock();
        }
    }

    public ValueTask BarrierAsync(
        IReadOnlyList<PointerWrite> writes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        cancellationToken.ThrowIfCancellationRequested();

        WorkItem workItem;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fault is not null)
            {
                return ValueTask.FromException(_fault);
            }

            if (_activeWritesAborted)
            {
                return ValueTask.FromCanceled(new CancellationToken(canceled: true));
            }

            if (_pendingMove is { } pendingMove)
            {
                _queue.Enqueue(WorkItem.FrozenMove(pendingMove));
                _pendingMove = null;
            }

            workItem = WorkItem.Barrier([.. writes], cancellationToken);
            _queue.Enqueue(workItem);
            _pendingDepth += workItem.Writes.Count;
            workItem.CancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var registration = (BarrierCancellationState)state!;
                    registration.Owner.CancelUnstartedBarrier(registration.WorkItem);
                },
                new BarrierCancellationState(this, workItem));
            SignalWorkerNoLock();
        }

        return new ValueTask(WaitForBarrierAsync(workItem));
    }

    public ValueTask BarrierAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        WorkItem workItem;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fault is not null)
            {
                return ValueTask.FromException(_fault);
            }

            if (_activeWritesAborted)
            {
                return ValueTask.FromCanceled(new CancellationToken(canceled: true));
            }

            if (_pendingMove is { } pendingMove)
            {
                _queue.Enqueue(WorkItem.FrozenMove(pendingMove));
                _pendingMove = null;
            }

            workItem = WorkItem.Barrier(operation, cancellationToken);
            _queue.Enqueue(workItem);
            workItem.CancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var registration = (BarrierCancellationState)state!;
                    registration.Owner.CancelUnstartedBarrier(registration.WorkItem);
                },
                new BarrierCancellationState(this, workItem));
            SignalWorkerNoLock();
        }

        return new ValueTask(WaitForBarrierAsync(workItem));
    }

    public ValueTask WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_sync)
        {
            if (_fault is not null)
            {
                return ValueTask.FromException(_fault);
            }

            if (_activeWritesAborted)
            {
                return ValueTask.FromCanceled(new CancellationToken(canceled: true));
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!HasPendingWorkNoLock())
            {
                return ValueTask.CompletedTask;
            }

            _idleCompletion ??= CreateIdleCompletionNoLock();
            idle = _idleCompletion.Task;
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask(idle.WaitAsync(cancellationToken))
            : new ValueTask(idle);
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? disposalCompletion = null;
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            disposalCompletion = NewCompletion();
            _disposeTask = disposalCompletion.Task;
            CancelPendingWorkNoLock();
            SignalWorkerNoLock();
        }

        _ = CompleteDisposeAsync(disposalCompletion);
        return new ValueTask(disposalCompletion.Task);
    }

    internal void AbortActiveWrites()
    {
        lock (_sync)
        {
            if (_activeWritesAborted)
            {
                return;
            }

            _activeWritesAborted = true;
            CancelPendingWorkNoLock();
            if (!_resourcesDisposed)
            {
                SignalWorkerNoLock();
            }
        }

        try
        {
            _activeWriteShutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _signalPending = false;
                if (_fault is not null || _activeWritesAborted ||
                    (_disposed && !HasPendingWorkNoLock()))
                {
                    return;
                }
            }

            while (TryTakeNext(
                out var writes,
                out var operation,
                out var completion,
                out var registration))
            {
                registration.Dispose();
                try
                {
                    if (operation is not null)
                    {
                        await operation(_activeWriteShutdown.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        foreach (var write in writes)
                        {
                            await _sender(write, _activeWriteShutdown.Token).ConfigureAwait(false);
                        }
                    }

                    completion?.TrySetResult();
                }
                catch (OperationCanceledException)
                    when (_activeWriteShutdown.IsCancellationRequested)
                {
                    completion?.TrySetCanceled(_activeWriteShutdown.Token);
                    return;
                }
                catch (Exception exception)
                {
                    completion?.TrySetException(exception);
                    await FaultAsync(exception).ConfigureAwait(false);
                    return;
                }
                finally
                {
                    lock (_sync)
                    {
                        _senderActive = false;
                        CompleteIdleNoLock();
                    }
                }
            }

            lock (_sync)
            {
                CompleteIdleNoLock();
                if (_fault is not null || _activeWritesAborted || _disposed)
                {
                    return;
                }

                if (HasPendingWorkNoLock())
                {
                    SignalWorkerNoLock();
                }
            }
        }
    }

    private bool TryTakeNext(
        out IReadOnlyList<PointerWrite> writes,
        out Func<CancellationToken, ValueTask>? operation,
        out TaskCompletionSource? completion,
        out CancellationTokenRegistration cancellationRegistration)
    {
        lock (_sync)
        {
            while (_queue.Count > 0)
            {
                var workItem = _queue.Dequeue();
                cancellationRegistration = workItem.CancellationRegistration;
                if (workItem.Canceled)
                {
                    continue;
                }

                workItem.Started = true;
                if (workItem.Counted)
                {
                    _pendingDepth -= workItem.Writes.Count;
                    workItem.Counted = false;
                }

                writes = workItem.Writes;
                operation = workItem.Operation;
                completion = workItem.Completion;
                _senderActive = true;
                return true;
            }

            if (_pendingMove is { } pendingMove)
            {
                _pendingMove = null;
                _pendingDepth--;
                writes = [pendingMove];
                operation = null;
                completion = null;
                cancellationRegistration = default;
                _senderActive = true;
                return true;
            }

            writes = [];
            operation = null;
            completion = null;
            cancellationRegistration = default;
            return false;
        }
    }

    private void CancelUnstartedBarrier(WorkItem workItem)
    {
        lock (_sync)
        {
            if (workItem.Started || workItem.Canceled)
            {
                return;
            }

            workItem.Canceled = true;
            if (workItem.Counted)
            {
                _pendingDepth -= workItem.Writes.Count;
                workItem.Counted = false;
            }

            workItem.Completion!.TrySetCanceled(workItem.CancellationToken);
        }
    }

    private async Task FaultAsync(Exception exception)
    {
        lock (_sync)
        {
            if (_fault is not null)
            {
                return;
            }

            _fault = exception;
            _pendingMove = null;
            _pendingDepth = 0;
            _senderActive = false;
            while (_queue.Count > 0)
            {
                var workItem = _queue.Dequeue();
                workItem.Canceled = true;
                workItem.Counted = false;
                workItem.Completion?.TrySetException(exception);
            }

            _idleCompletion?.TrySetException(exception);
            _idleCompletion = null;
        }

        if (Interlocked.Exchange(ref _faultHandlerInvoked, 1) != 0)
        {
            return;
        }

        try
        {
            await _faultHandler(exception).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The sender exception remains the coalescer's terminal failure.
        }
    }

    private bool HasPendingWorkNoLock() =>
        _senderActive || _queue.Count > 0 || _pendingMove.HasValue;

    private TaskCompletionSource CreateIdleCompletionNoLock()
    {
        _idleWaiterCreationCount++;
        return NewCompletion();
    }

    private void CompleteIdleNoLock()
    {
        if (_fault is not null || _activeWritesAborted || _disposed || HasPendingWorkNoLock())
        {
            return;
        }

        _idleCompletion?.TrySetResult();
        _idleCompletion = null;
    }

    private void SignalWorkerNoLock()
    {
        if (_signalPending)
        {
            return;
        }

        _signalPending = true;
        _signal.Release();
    }

    private void CancelPendingWorkNoLock()
    {
        _pendingMove = null;
        _pendingDepth = 0;
        while (_queue.Count > 0)
        {
            var workItem = _queue.Dequeue();
            workItem.Canceled = true;
            workItem.Counted = false;
            workItem.Completion?.TrySetCanceled(new CancellationToken(canceled: true));
        }

        _idleCompletion?.TrySetCanceled(new CancellationToken(canceled: true));
        _idleCompletion = null;
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
                completion.TrySetResult();
            }
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task WaitForBarrierAsync(WorkItem workItem)
    {
        try
        {
            await workItem.Completion!.Task.ConfigureAwait(false);
        }
        finally
        {
            workItem.CancellationRegistration.Dispose();
        }
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class WorkItem
    {
        private WorkItem(
            IReadOnlyList<PointerWrite> writes,
            Func<CancellationToken, ValueTask>? operation,
            TaskCompletionSource? completion,
            CancellationToken cancellationToken)
        {
            Writes = writes;
            Operation = operation;
            Completion = completion;
            CancellationToken = cancellationToken;
            Counted = true;
        }

        public IReadOnlyList<PointerWrite> Writes { get; }

        public Func<CancellationToken, ValueTask>? Operation { get; }

        public TaskCompletionSource? Completion { get; }

        public CancellationToken CancellationToken { get; }

        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public bool Counted { get; set; }

        public bool Started { get; set; }

        public bool Canceled { get; set; }

        public static WorkItem FrozenMove(PointerWrite write) =>
            new([write], operation: null, completion: null, CancellationToken.None);

        public static WorkItem Barrier(
            IReadOnlyList<PointerWrite> writes,
            CancellationToken cancellationToken) =>
            new(writes, operation: null, NewCompletion(), cancellationToken);

        public static WorkItem Barrier(
            Func<CancellationToken, ValueTask> operation,
            CancellationToken cancellationToken) =>
            new([], operation, NewCompletion(), cancellationToken);
    }

    private sealed record BarrierCancellationState(
        RemotePointerWriteCoalescer Owner,
        WorkItem WorkItem);
}
