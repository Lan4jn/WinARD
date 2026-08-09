using WinARD.Application.Ports;
using WinARD.Desktop.Input;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Infrastructure.Diagnostics;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Input;

public sealed class RemotePointerWriteCoalescerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Blocked_move_keeps_only_latest_pending_position()
    {
        var writes = new List<PointerWrite>();
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        await using var coalescer = new RemotePointerWriteCoalescer(
            async (write, _) =>
            {
                writes.Add(write);
                if (writes.Count == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
            });

        coalescer.QueueMove(Write(1));
        await firstStarted.Task.WaitAsync(TestTimeout);
        for (var value = 2; value <= 101; value++)
        {
            coalescer.QueueMove(Write(value));
        }

        releaseFirst.TrySetResult();
        await coalescer.WhenIdleAsync().AsTask().WaitAsync(TestTimeout);

        Assert.Equal([Write(1), Write(101)], writes);
        Assert.Equal(99, coalescer.Snapshot.CoalescedMoves);
        Assert.Equal(0, coalescer.Snapshot.PendingDepth);
    }

    [Fact]
    public async Task Barrier_freezes_latest_move_before_later_moves()
    {
        var writes = new List<PointerWrite>();
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        await using var coalescer = new RemotePointerWriteCoalescer(
            async (write, _) =>
            {
                writes.Add(write);
                if (writes.Count == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
            });

        coalescer.QueueMove(Write(1));
        await firstStarted.Task.WaitAsync(TestTimeout);
        coalescer.QueueMove(Write(2));
        coalescer.QueueMove(Write(3));
        var barrier = coalescer.BarrierAsync([Write(10, buttons: 1)], CancellationToken.None);
        coalescer.QueueMove(Write(4));

        releaseFirst.TrySetResult();
        await barrier.AsTask().WaitAsync(TestTimeout);
        await coalescer.WhenIdleAsync().AsTask().WaitAsync(TestTimeout);

        Assert.Equal([Write(1), Write(3), Write(10, 1), Write(4)], writes);
        Assert.Equal(1, coalescer.Snapshot.CoalescedMoves);
    }

    [Fact]
    public async Task Barrier_writes_remain_adjacent_and_in_order()
    {
        var writes = new List<PointerWrite>();
        var wheelStarted = NewCompletion();
        var releaseWheel = NewCompletion();
        var wheel = Write(20, buttons: 8);
        var baseWrite = Write(20, buttons: 1);
        await using var coalescer = new RemotePointerWriteCoalescer(
            async (write, _) =>
            {
                writes.Add(write);
                if (write == wheel)
                {
                    wheelStarted.TrySetResult();
                    await releaseWheel.Task;
                }
            });

        var barrier = coalescer.BarrierAsync([wheel, baseWrite], CancellationToken.None);
        await wheelStarted.Task.WaitAsync(TestTimeout);
        coalescer.QueueMove(Write(21));
        releaseWheel.TrySetResult();

        await barrier.AsTask().WaitAsync(TestTimeout);
        await coalescer.WhenIdleAsync().AsTask().WaitAsync(TestTimeout);

        Assert.Equal([wheel, baseWrite, Write(21)], writes);
    }

    [Fact]
    public async Task Move_key_move_preserves_one_remote_input_order()
    {
        var events = new List<string>();
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        await using var scheduler = new RemotePointerWriteCoalescer(
            async (write, _) =>
            {
                events.Add($"move:{write.Point.X}");
                if (write.Point.X == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
            });

        scheduler.QueueMove(Write(1));
        await firstStarted.Task.WaitAsync(TestTimeout);
        scheduler.QueueMove(Write(2));
        var key = scheduler.BarrierAsync(
            _ =>
            {
                events.Add("key");
                return ValueTask.CompletedTask;
            });
        scheduler.QueueMove(Write(3));

        releaseFirst.TrySetResult();
        await key.AsTask().WaitAsync(TestTimeout);
        await scheduler.WhenIdleAsync().AsTask().WaitAsync(TestTimeout);

        Assert.Equal(["move:1", "move:2", "key", "move:3"], events);
    }

    [Fact]
    public async Task Text_barrier_is_atomic_against_later_pointer_move()
    {
        var events = new List<string>();
        var downStarted = NewCompletion();
        var releaseDown = NewCompletion();
        await using var scheduler = new RemotePointerWriteCoalescer(
            (write, _) =>
            {
                events.Add($"move:{write.Point.X}");
                return ValueTask.CompletedTask;
            });

        scheduler.QueueMove(Write(1));
        var text = scheduler.BarrierAsync(async _ =>
        {
            events.Add("text-down");
            downStarted.TrySetResult();
            await releaseDown.Task;
            events.Add("text-up");
        });
        await downStarted.Task.WaitAsync(TestTimeout);
        scheduler.QueueMove(Write(2));
        releaseDown.TrySetResult();

        await text.AsTask().WaitAsync(TestTimeout);
        await scheduler.WhenIdleAsync().AsTask().WaitAsync(TestTimeout);

        Assert.Equal(["move:1", "text-down", "text-up", "move:2"], events);
    }

    [Fact]
    public async Task Multiple_barriers_are_fifo_and_lossless()
    {
        var writes = new List<PointerWrite>();
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        await using var coalescer = new RemotePointerWriteCoalescer(
            async (write, _) =>
            {
                writes.Add(write);
                if (writes.Count == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
            });

        var first = coalescer.BarrierAsync([Write(1, 1), Write(2, 0)], CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestTimeout);
        var second = coalescer.BarrierAsync([Write(3, 2)], CancellationToken.None);
        var third = coalescer.BarrierAsync([Write(4, 4), Write(5, 0)], CancellationToken.None);
        releaseFirst.TrySetResult();

        await Task.WhenAll(first.AsTask(), second.AsTask(), third.AsTask()).WaitAsync(TestTimeout);

        Assert.Equal(
            [Write(1, 1), Write(2, 0), Write(3, 2), Write(4, 4), Write(5, 0)],
            writes);
    }

    [Fact]
    public async Task QueueMove_returns_without_waiting_for_sender_and_exposes_no_task()
    {
        using var senderEntered = new ManualResetEventSlim();
        using var releaseSender = new ManualResetEventSlim();
        await using var coalescer = new RemotePointerWriteCoalescer(
            (write, token) =>
            {
                senderEntered.Set();
                releaseSender.Wait(TestTimeout, token);
                return ValueTask.CompletedTask;
            });

        var queueCall = Task.Run(() => coalescer.QueueMove(Write(1)));
        await queueCall.WaitAsync(TestTimeout);
        Assert.True(senderEntered.Wait(TestTimeout));
        Assert.Equal(
            typeof(void),
            typeof(RemotePointerWriteCoalescer).GetMethod(nameof(coalescer.QueueMove))!.ReturnType);

        releaseSender.Set();
        await coalescer.WhenIdleAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task QueueMove_reuses_the_single_worker_and_does_not_create_idle_waiters()
    {
        TaskCompletionSource? senderStarted = null;
        TaskCompletionSource? releaseSender = null;
        await using var coalescer = new RemotePointerWriteCoalescer(
            async (_, _) =>
            {
                senderStarted!.TrySetResult();
                await releaseSender!.Task;
            });

        for (var value = 1; value <= 3; value++)
        {
            senderStarted = NewCompletion();
            releaseSender = NewCompletion();
            coalescer.QueueMove(Write(value));
            await senderStarted.Task.WaitAsync(TestTimeout);
            var idle = coalescer.WhenIdleAsync();
            Assert.False(idle.IsCompleted);
            releaseSender.TrySetResult();
            await idle.AsTask().WaitAsync(TestTimeout);
        }

        Assert.Equal(1, coalescer.WorkerStartCount);
        Assert.Equal(3, coalescer.IdleWaiterCreationCount);
    }

    [Fact]
    public async Task Cancellation_before_barrier_starts_skips_all_barrier_writes()
    {
        var writes = new List<PointerWrite>();
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        await using var coalescer = new RemotePointerWriteCoalescer(
            async (write, _) =>
            {
                writes.Add(write);
                if (writes.Count == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
            });

        coalescer.QueueMove(Write(1));
        await firstStarted.Task.WaitAsync(TestTimeout);
        using var cancellation = new CancellationTokenSource();
        var barrier = coalescer.BarrierAsync([Write(2, 1), Write(3, 0)], cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => barrier.AsTask());
        releaseFirst.TrySetResult();
        await coalescer.WhenIdleAsync().AsTask().WaitAsync(TestTimeout);

        Assert.Equal([Write(1)], writes);
    }

    [Fact]
    public async Task Cancellation_after_barrier_starts_does_not_interrupt_it()
    {
        var writes = new List<PointerWrite>();
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        await using var coalescer = new RemotePointerWriteCoalescer(
            async (write, _) =>
            {
                writes.Add(write);
                if (writes.Count == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
            });
        using var cancellation = new CancellationTokenSource();

        var barrier = coalescer.BarrierAsync([Write(1, 1), Write(2, 0)], cancellation.Token);
        await firstStarted.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        releaseFirst.TrySetResult();

        await barrier.AsTask().WaitAsync(TestTimeout);
        Assert.Equal([Write(1, 1), Write(2, 0)], writes);
    }

    [Fact]
    public async Task Dispose_rejects_new_moves_cancels_unstarted_work_and_waits_for_active_sender()
    {
        var writes = new List<PointerWrite>();
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        var coalescer = new RemotePointerWriteCoalescer(
            async (write, _) =>
            {
                writes.Add(write);
                if (writes.Count == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
            });

        coalescer.QueueMove(Write(1));
        await firstStarted.Task.WaitAsync(TestTimeout);
        coalescer.QueueMove(Write(2));
        var barrier = coalescer.BarrierAsync([Write(3, 1)], CancellationToken.None);
        var disposal = coalescer.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => barrier.AsTask());
        Assert.Throws<ObjectDisposedException>(() => coalescer.QueueMove(Write(4)));
        Assert.False(disposal.IsCompleted);
        releaseFirst.TrySetResult();
        await disposal.WaitAsync(TestTimeout);

        Assert.Equal([Write(1)], writes);
    }

    [Fact]
    public async Task Dispose_does_not_cancel_or_truncate_a_started_multi_write_barrier()
    {
        var firstWrite = Write(1, buttons: 8);
        var secondWrite = Write(1, buttons: 0);
        var writes = new List<PointerWrite>();
        var senderTokens = new List<CancellationToken>();
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        var coalescer = new RemotePointerWriteCoalescer(
            async (write, token) =>
            {
                senderTokens.Add(token);
                token.ThrowIfCancellationRequested();
                if (write == firstWrite)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                    token.ThrowIfCancellationRequested();
                }

                writes.Add(write);
            });

        var barrier = coalescer.BarrierAsync(
            [firstWrite, secondWrite],
            CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestTimeout);
        var disposal = coalescer.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        releaseFirst.TrySetResult();
        await barrier.AsTask().WaitAsync(TestTimeout);
        await disposal.WaitAsync(TestTimeout);

        Assert.Equal([firstWrite, secondWrite], writes);
        Assert.All(senderTokens, token => Assert.False(token.IsCancellationRequested));
    }

    [Fact]
    public async Task Abort_active_writes_cancels_shutdown_token_and_unblocks_dispose()
    {
        var senderStarted = NewCompletion();
        var senderExited = NewCompletion();
        var faultCalls = 0;
        CancellationToken senderToken = default;
        var coalescer = new RemotePointerWriteCoalescer(
            async (_, token) =>
            {
                senderToken = token;
                senderStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    senderExited.TrySetResult();
                }
            },
            exception =>
            {
                Interlocked.Increment(ref faultCalls);
                return Task.CompletedTask;
            });

        var barrier = coalescer.BarrierAsync([Write(1)], CancellationToken.None);
        await senderStarted.Task.WaitAsync(TestTimeout);
        var disposal = coalescer.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        Assert.False(senderToken.IsCancellationRequested);
        coalescer.AbortActiveWrites();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => barrier.AsTask());
        await senderExited.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await disposal.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.True(senderToken.IsCancellationRequested);
        Assert.Equal(0, Volatile.Read(ref faultCalls));
    }

    [Fact]
    public async Task Abort_after_dispose_is_safe_and_idempotent()
    {
        var coalescer = new RemotePointerWriteCoalescer(
            (_, _) => ValueTask.CompletedTask);

        await coalescer.DisposeAsync().AsTask().WaitAsync(TestTimeout);

        var firstAbort = Record.Exception(coalescer.AbortActiveWrites);
        var repeatedAbort = Record.Exception(coalescer.AbortActiveWrites);

        Assert.Null(firstAbort);
        Assert.Null(repeatedAbort);
    }

    [Fact]
    public async Task Concurrent_abort_and_dispose_are_safe()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var coalescer = new RemotePointerWriteCoalescer(
                (_, _) => ValueTask.CompletedTask);
            var start = NewCompletion();
            var abort = Task.Run(async () =>
            {
                await start.Task;
                coalescer.AbortActiveWrites();
                coalescer.AbortActiveWrites();
            });
            var dispose = Task.Run(async () =>
            {
                await start.Task;
                await coalescer.DisposeAsync();
            });

            start.TrySetResult();

            await Task.WhenAll(abort, dispose).WaitAsync(TestTimeout);
            coalescer.AbortActiveWrites();
        }
    }

    [Fact]
    public async Task Sender_failure_faults_waiters_and_invokes_fault_handler_once()
    {
        var failure = new InvalidOperationException("sender failed");
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        var faultHandled = NewCompletion();
        var faultCalls = 0;
        await using var coalescer = new RemotePointerWriteCoalescer(
            async (_, _) =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
                throw failure;
            },
            exception =>
            {
                Assert.Same(failure, exception);
                Interlocked.Increment(ref faultCalls);
                faultHandled.TrySetResult();
                return Task.CompletedTask;
            });

        coalescer.QueueMove(Write(1));
        await firstStarted.Task.WaitAsync(TestTimeout);
        var barrier = coalescer.BarrierAsync([Write(2, 1)], CancellationToken.None);
        var idle = coalescer.WhenIdleAsync();
        releaseFirst.TrySetResult();

        var barrierFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => barrier.AsTask());
        var idleFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => idle.AsTask());
        var laterBarrierFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coalescer.BarrierAsync([Write(3)], CancellationToken.None).AsTask());
        coalescer.QueueMove(Write(4));
        await faultHandled.Task.WaitAsync(TestTimeout);

        Assert.Same(failure, barrierFailure);
        Assert.Same(failure, idleFailure);
        Assert.Same(failure, laterBarrierFailure);
        Assert.Equal(1, Volatile.Read(ref faultCalls));
    }

    [Fact]
    public async Task Fault_handler_failure_does_not_replace_sender_failure_or_repeat_callback()
    {
        var senderFailure = new InvalidOperationException("sender failed");
        var handlerFailure = new InvalidDataException("handler failed");
        var faultCalls = 0;
        await using var coalescer = new RemotePointerWriteCoalescer(
            (_, _) => ValueTask.FromException(senderFailure),
            _ =>
            {
                Interlocked.Increment(ref faultCalls);
                return Task.FromException(handlerFailure);
            });

        var barrier = coalescer.BarrierAsync([Write(1)], CancellationToken.None);
        var barrierFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => barrier.AsTask());
        var idleFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coalescer.WhenIdleAsync().AsTask());
        var laterFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coalescer.BarrierAsync([Write(2)], CancellationToken.None).AsTask());

        Assert.Same(senderFailure, barrierFailure);
        Assert.Same(senderFailure, idleFailure);
        Assert.Same(senderFailure, laterFailure);
        Assert.Equal(1, Volatile.Read(ref faultCalls));
    }

    [Fact]
    public async Task Snapshot_counts_only_replaced_moves_and_reads_pending_depth_safely()
    {
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        await using var coalescer = new RemotePointerWriteCoalescer(
            async (write, _) =>
            {
                if (write == Write(1))
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
            });

        coalescer.QueueMove(Write(1));
        await firstStarted.Task.WaitAsync(TestTimeout);
        coalescer.QueueMove(Write(2));
        var barrier = coalescer.BarrierAsync([Write(3, 1)], CancellationToken.None);
        coalescer.QueueMove(Write(4));
        coalescer.QueueMove(Write(5));

        var busy = coalescer.Snapshot;
        Assert.Equal(1, busy.CoalescedMoves);
        Assert.Equal(3, busy.PendingDepth);

        releaseFirst.TrySetResult();
        await barrier.AsTask().WaitAsync(TestTimeout);
        await coalescer.WhenIdleAsync().AsTask().WaitAsync(TestTimeout);

        Assert.Equal(new RemotePointerCoalescerSnapshot(1, 0), coalescer.Snapshot);
    }

    [Fact]
    public async Task Repeated_barriers_are_not_stranded_while_worker_goes_idle()
    {
        var sent = 0;
        await using var coalescer = new RemotePointerWriteCoalescer(
            (_, _) =>
            {
                Interlocked.Increment(ref sent);
                return ValueTask.CompletedTask;
            });

        var exercise = Task.Run(async () =>
        {
            for (var value = 1; value <= 10_000; value++)
            {
                await coalescer.BarrierAsync([Write(value)], CancellationToken.None);
            }
        });

        await exercise.WaitAsync(TestTimeout);
        Assert.Equal(10_000, Volatile.Read(ref sent));
    }

    [Fact]
    public async Task Move_sender_failure_immediately_makes_later_moves_safe_no_ops()
    {
        var runtime = new FailingPointerRuntime();
        var diagnostics = new RecordingDiagnosticSink();
        await using var viewModel = CreateViewModel(runtime, diagnostics, new CountingDispatcher());

        viewModel.QueuePointerMove(buttons: 0, new RemotePoint(1, 1));
        await diagnostics.FirstWrite.Task.WaitAsync(TestTimeout);

        Assert.True(viewModel.IsInputUnavailable);
        var exception = Record.Exception(
            () => viewModel.QueuePointerMove(buttons: 0, new RemotePoint(2, 2)));
        Assert.Null(exception);
        Assert.Equal(1, runtime.PointerAttempts);
    }

    [Fact]
    public async Task Barrier_failure_is_observed_and_reported_once_when_runner_also_sees_it()
    {
        var runtime = new FailingPointerRuntime();
        var diagnostics = new RecordingDiagnosticSink();
        var dispatcher = new CountingDispatcher();
        await using var viewModel = CreateViewModel(runtime, diagnostics, dispatcher);
        var stopCalls = 0;
        var runner = new RemoteInputOperationRunner(
            dispatcher,
            viewModel.ReportInputFailureAsync,
            () =>
            {
                Interlocked.Increment(ref stopCalls);
                return Task.CompletedTask;
            },
            () => false,
            viewModel.ObserveInputFailure);

        await runner.RunAsync(
            () => viewModel.SendPointerBarrierAsync(
                [Write(1)],
                CancellationToken.None).AsTask());

        Assert.Equal(1, diagnostics.WriteCount);
        Assert.Equal(3, dispatcher.InvocationCount);
        Assert.Equal(1, Volatile.Read(ref stopCalls));
        Assert.Equal(1, runtime.PointerAttempts);
    }

    [Fact]
    public async Task Never_completing_dispatcher_does_not_block_fault_shutdown_runner_or_dispose()
    {
        var runtime = new FailingPointerRuntime();
        var diagnostics = new RecordingDiagnosticSink();
        var dispatcher = new NeverCompletingDispatcher();
        var viewModel = CreateViewModel(runtime, diagnostics, dispatcher);
        var stopEntered = NewCompletion();
        var stopCalls = 0;
        var runner = new RemoteInputOperationRunner(
            new CountingDispatcher(),
            viewModel.ReportInputFailureAsync,
            () =>
            {
                _ = Interlocked.Increment(ref stopCalls);
                stopEntered.TrySetResult();
                return Task.CompletedTask;
            },
            () => false,
            viewModel.ObserveInputFailure);

        var run = runner.RunAsync(
            () => viewModel.SendPointerBarrierAsync(
                [Write(1)],
                CancellationToken.None).AsTask());
        await diagnostics.FirstWrite.Task.WaitAsync(TestTimeout);

        Assert.True(viewModel.IsInputUnavailable);
        await stopEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await run.WaitAsync(TimeSpan.FromMilliseconds(500));
        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500));

        try
        {
            Assert.Equal(1, diagnostics.WriteCount);
            Assert.Equal(1, Volatile.Read(ref stopCalls));
            Assert.Equal(2, dispatcher.PendingCount);
            Assert.Equal(0, viewModel.ActiveBestEffortUiObserverCount);
        }
        finally
        {
            dispatcher.FailPendingUpdates();
        }
    }

    [Fact]
    public async Task ViewModel_shutdown_aborts_active_pointer_write_before_releasing_ownership()
    {
        var runtime = new BlockingPointerRuntime();
        var ownership = new OrderingOwnership(runtime.SenderExited.Task);
        var viewModel = CreateViewModel(
            runtime,
            new RecordingDiagnosticSink(),
            new CountingDispatcher(),
            ownership);

        viewModel.QueuePointerMove(buttons: 0, new RemotePoint(1, 1));
        await runtime.SenderStarted.Task.WaitAsync(TestTimeout);

        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500));

        Assert.True(runtime.SenderToken.IsCancellationRequested);
        Assert.True(runtime.SenderExited.Task.IsCompletedSuccessfully);
        Assert.Equal(1, ownership.DisposeCount);
        Assert.True(ownership.SenderExitedBeforeDispose);
    }

    [Fact]
    public async Task ViewModel_orders_keyboard_barrier_between_pointer_moves()
    {
        var runtime = new OrderedInputRuntime();
        await using var viewModel = CreateViewModel(
            runtime,
            new RecordingDiagnosticSink(),
            new CountingDispatcher());

        viewModel.QueuePointerMove(0, new RemotePoint(1, 1));
        await runtime.FirstPointerStarted.Task.WaitAsync(TestTimeout);
        viewModel.QueuePointerMove(0, new RemotePoint(2, 2));
        var key = viewModel.KeyDownAsync(
            Windows.System.VirtualKey.A,
            scanCode: 30,
            isExtended: false,
            text: null,
            CancellationToken.None);
        viewModel.QueuePointerMove(0, new RemotePoint(3, 3));

        runtime.ReleaseFirstPointer.TrySetResult();
        await key.AsTask().WaitAsync(TestTimeout);
        await runtime.FourEvents.Task.WaitAsync(TestTimeout);

        Assert.Equal(["pointer:1", "pointer:2", "key:97:True", "pointer:3"], runtime.Events);
    }

    private static PointerWrite Write(int value, byte buttons = 0) =>
        new(buttons, new RemotePoint(value, value));

    private static RemoteSessionViewModel CreateViewModel(
        IRemoteSessionRuntime runtime,
        ISafeDiagnosticSink diagnostics,
        IUiDispatcher dispatcher,
        IAsyncDisposable? ownership = null) =>
        new(
            runtime,
            ownership ?? new NoOpOwnership(),
            new NoOpPresenter(),
            dispatcher,
            clipboardBridge: null,
            diagnostics);

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FailingPointerRuntime : IRemoteSessionRuntime
    {
        private int _pointerAttempts;

        public int PointerAttempts => Volatile.Read(ref _pointerAttempts);

        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(
            bool incremental,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        public ValueTask SendPointerAsync(
            byte buttons,
            int x,
            int y,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _pointerAttempts);
            return ValueTask.FromException(new IOException("sensitive pointer failure"));
        }

        public ValueTask SendKeyAsync(
            uint keysym,
            bool down,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask SendClipboardTextAsync(
            string text,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingPointerRuntime : IRemoteSessionRuntime
    {
        public TaskCompletionSource SenderStarted { get; } = NewCompletion();

        public TaskCompletionSource SenderExited { get; } = NewCompletion();

        public CancellationToken SenderToken { get; private set; }

        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(
            bool incremental,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        public async ValueTask SendPointerAsync(
            byte buttons,
            int x,
            int y,
            CancellationToken cancellationToken)
        {
            SenderToken = cancellationToken;
            SenderStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                SenderExited.TrySetResult();
            }
        }

        public ValueTask SendKeyAsync(
            uint keysym,
            bool down,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask SendClipboardTextAsync(
            string text,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class OrderedInputRuntime : IRemoteSessionRuntime
    {
        private readonly object _sync = new();

        public List<string> Events { get; } = [];
        public TaskCompletionSource FirstPointerStarted { get; } = NewCompletion();
        public TaskCompletionSource ReleaseFirstPointer { get; } = NewCompletion();
        public TaskCompletionSource FourEvents { get; } = NewCompletion();
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
        public async ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken)
        {
            Add($"pointer:{x}");
            if (x == 1)
            {
                FirstPointerStarted.TrySetResult();
                await ReleaseFirstPointer.Task.WaitAsync(cancellationToken);
            }
        }
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken)
        {
            Add($"key:{keysym}:{down}");
            return ValueTask.CompletedTask;
        }
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;

        private void Add(string value)
        {
            lock (_sync)
            {
                Events.Add(value);
                if (Events.Count == 4)
                {
                    FourEvents.TrySetResult();
                }
            }
        }
    }

    private sealed class NoOpOwnership : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OrderingOwnership(Task senderExited) : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public bool SenderExitedBeforeDispose { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            SenderExitedBeforeDispose = senderExited.IsCompletedSuccessfully;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpPresenter : IFramePresenter
    {
        public void Resize(int width, int height)
        {
        }

        public void Present(
            ReadOnlySpan<byte> bgra32,
            int stride,
            IReadOnlyList<RemoteRectangle> dirtyRectangles)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingDispatcher : IUiDispatcher
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _invocationCount);
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class NeverCompletingDispatcher : IUiDispatcher
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource> _pending = [];
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public int PendingCount
        {
            get
            {
                lock (_sync)
                {
                    return _pending.Count;
                }
            }
        }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invocation = Interlocked.Increment(ref _invocationCount);
            if (invocation > 2)
            {
                action();
                return Task.CompletedTask;
            }

            var completion = NewCompletion();
            lock (_sync)
            {
                _pending.Add(completion);
            }

            return completion.Task;
        }

        public void FailPendingUpdates()
        {
            TaskCompletionSource[] pending;
            lock (_sync)
            {
                pending = [.. _pending];
                _pending.Clear();
            }

            foreach (var completion in pending)
            {
                completion.TrySetException(new InvalidOperationException("dispatcher stopped"));
            }
        }
    }

    private sealed class RecordingDiagnosticSink : ISafeDiagnosticSink
    {
        private int _writeCount;

        public TaskCompletionSource FirstWrite { get; } = NewCompletion();

        public int WriteCount => Volatile.Read(ref _writeCount);

        public void Write(SafeDiagnosticEventInput diagnosticEvent)
        {
            _ = Interlocked.Increment(ref _writeCount);
            FirstWrite.TrySetResult();
        }

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
    }
}
