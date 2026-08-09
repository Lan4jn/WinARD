using System.Collections.Concurrent;
using WinARD.Desktop.Services;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Services;

public sealed class ClientMessageSchedulerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Constructor_and_enqueue_validate_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientMessageScheduler(0));

        var scheduler = new ClientMessageScheduler();
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = scheduler.EnqueueInputAsync(null!, CancellationToken.None).AsTask();
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = scheduler.EnqueueBackgroundAsync(null!, CancellationToken.None).AsTask();
        });
        await scheduler.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Input_precedes_background_that_has_not_started()
    {
        var order = new List<string>();
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        await using var scheduler = new ClientMessageScheduler();
        using var releaseGuard = new CompletionRelease(releaseFirst);
        var first = scheduler.EnqueueInputAsync(async token =>
        {
            order.Add("i0");
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
        }, CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TestTimeout);
        var background = scheduler.EnqueueBackgroundAsync(_ =>
        {
            order.Add("background");
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask();
        var input = scheduler.EnqueueInputAsync(_ =>
        {
            order.Add("input");
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask();

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, input, background).WaitAsync(TestTimeout);

        Assert.Equal(["i0", "input", "background"], order);
    }

    [Fact]
    public async Task Background_is_served_after_thirty_two_input_writes()
    {
        var order = new List<string>();
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        await using var scheduler = new ClientMessageScheduler(maximumInputBatch: 32);
        using var releaseGuard = new CompletionRelease(releaseFirst);
        var first = scheduler.EnqueueInputAsync(async token =>
        {
            order.Add("i0");
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
        }, CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TestTimeout);
        var input = Enumerable.Range(1, 39)
            .Select(i => scheduler.EnqueueInputAsync(_ =>
            {
                order.Add($"i{i}");
                return ValueTask.CompletedTask;
            }, CancellationToken.None).AsTask())
            .ToArray();
        var background = scheduler.EnqueueBackgroundAsync(_ =>
        {
            order.Add("frame");
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask();

        releaseFirst.TrySetResult();
        await Task.WhenAll(input.Append(first).Append(background)).WaitAsync(TestTimeout);

        Assert.Equal(32, order.IndexOf("frame"));
    }

    [Fact]
    public async Task Each_priority_is_fifo()
    {
        var order = new List<string>();
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        await using var scheduler = new ClientMessageScheduler();
        using var releaseGuard = new CompletionRelease(releaseFirst);
        var first = scheduler.EnqueueBackgroundAsync(async token =>
        {
            order.Add("b0");
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
        }, CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TestTimeout);
        var input1 = EnqueueInput("i1");
        var background1 = EnqueueBackground("b1");
        var input2 = EnqueueInput("i2");
        var background2 = EnqueueBackground("b2");

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, input1, input2, background1, background2).WaitAsync(TestTimeout);

        Assert.Equal(["b0", "i1", "i2", "b1", "b2"], order);

        Task EnqueueInput(string value) => scheduler.EnqueueInputAsync(_ =>
        {
            order.Add(value);
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask();

        Task EnqueueBackground(string value) => scheduler.EnqueueBackgroundAsync(_ =>
        {
            order.Add(value);
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask();
    }

    [Fact]
    public async Task Background_runs_immediately_when_input_is_empty()
    {
        var invoked = false;
        await using var scheduler = new ClientMessageScheduler();

        await scheduler.EnqueueBackgroundAsync(_ =>
        {
            invoked = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask().WaitAsync(TestTimeout);

        Assert.True(invoked);
    }

    [Fact]
    public async Task Input_continues_without_a_background_request()
    {
        var order = new List<int>();
        await using var scheduler = new ClientMessageScheduler(maximumInputBatch: 4);
        var requests = Enumerable.Range(0, 40)
            .Select(value => scheduler.EnqueueInputAsync(_ =>
            {
                order.Add(value);
                return ValueTask.CompletedTask;
            }, CancellationToken.None).AsTask())
            .ToArray();

        await Task.WhenAll(requests).WaitAsync(TestTimeout);

        Assert.Equal(Enumerable.Range(0, 40), order);
    }

    [Fact]
    public async Task Cancellation_before_start_skips_only_that_request()
    {
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        var canceledInvoked = false;
        var laterInvoked = false;
        using var cancellation = new CancellationTokenSource();
        await using var scheduler = new ClientMessageScheduler();
        using var releaseGuard = new CompletionRelease(releaseFirst);
        var first = scheduler.EnqueueInputAsync(async token =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
        }, CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TestTimeout);
        var canceled = scheduler.EnqueueInputAsync(_ =>
        {
            canceledInvoked = true;
            return ValueTask.CompletedTask;
        }, cancellation.Token).AsTask();
        var later = scheduler.EnqueueInputAsync(_ =>
        {
            laterInvoked = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, later).WaitAsync(TestTimeout);

        Assert.False(canceledInvoked);
        Assert.True(laterInvoked);
    }

    [Fact]
    public async Task Cancellation_after_start_is_passed_to_the_write_delegate()
    {
        var started = NewCompletion();
        using var cancellation = new CancellationTokenSource();
        await using var scheduler = new ClientMessageScheduler();
        var request = scheduler.EnqueueInputAsync(async token =>
        {
            started.TrySetResult();
            await TaskCompletionSourceTask().WaitAsync(token);
        }, cancellation.Token).AsTask();
        await started.Task.WaitAsync(TestTimeout);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request)
            .WaitAsync(TestTimeout);

        static Task TaskCompletionSourceTask() => NewCompletion().Task;
    }

    [Fact]
    public async Task Started_write_that_ignores_cancellation_can_complete()
    {
        var started = NewCompletion();
        var release = NewCompletion();
        using var cancellation = new CancellationTokenSource();
        await using var scheduler = new ClientMessageScheduler();
        using var releaseGuard = new CompletionRelease(release);
        var request = scheduler.EnqueueInputAsync(async token =>
        {
            Assert.True(token.CanBeCanceled);
            started.TrySetResult();
            await release.Task;
        }, cancellation.Token).AsTask();
        await started.Task.WaitAsync(TestTimeout);

        cancellation.Cancel();
        Assert.False(request.IsCompleted);
        release.TrySetResult();
        await request.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Write_fault_fails_current_pending_and_future_requests_with_original_exception()
    {
        var failure = new IOException("write failed");
        var firstStarted = NewCompletion();
        var releaseFirst = NewCompletion();
        var pendingInvocations = 0;
        await using var scheduler = new ClientMessageScheduler();
        using var releaseGuard = new CompletionRelease(releaseFirst);
        var first = scheduler.EnqueueInputAsync(async token =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
            throw failure;
        }, CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TestTimeout);
        var pendingInput = scheduler.EnqueueInputAsync(token =>
        {
            _ = token;
            _ = Interlocked.Increment(ref pendingInvocations);
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask();
        var pendingBackground = scheduler.EnqueueBackgroundAsync(token =>
        {
            _ = token;
            _ = Interlocked.Increment(ref pendingInvocations);
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask();

        releaseFirst.TrySetResult();
        var firstException = await Record.ExceptionAsync(() => first);
        var inputException = await Record.ExceptionAsync(() => pendingInput);
        var backgroundException = await Record.ExceptionAsync(() => pendingBackground);
        var futureException = await Record.ExceptionAsync(() =>
            scheduler.EnqueueInputAsync(_ => ValueTask.CompletedTask, CancellationToken.None).AsTask());

        Assert.Same(failure, firstException);
        Assert.Same(failure, inputException);
        Assert.Same(failure, backgroundException);
        Assert.Same(failure, futureException);
        Assert.Equal(0, pendingInvocations);
        Assert.Equal(0, scheduler.PerformanceSnapshot.InputQueueDepth);
    }

    [Fact]
    public async Task Dispose_stops_accepting_cancels_pending_and_waits_for_active_write()
    {
        var started = NewCompletion();
        var release = NewCompletion();
        var pendingInvoked = false;
        var scheduler = new ClientMessageScheduler();
        using var releaseGuard = new CompletionRelease(release);
        var active = scheduler.EnqueueInputAsync(async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        }, CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TestTimeout);
        var pending = scheduler.EnqueueBackgroundAsync(_ =>
        {
            pendingInvoked = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask();

        var disposal = scheduler.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = scheduler.EnqueueInputAsync(_ => ValueTask.CompletedTask, CancellationToken.None).AsTask();
        });
        release.TrySetResult();
        await Task.WhenAll(active, disposal).WaitAsync(TestTimeout);
        Assert.False(pendingInvoked);
    }

    [Fact]
    public async Task Abort_active_writes_cancels_the_dedicated_shutdown_token()
    {
        var started = NewCompletion();
        var scheduler = new ClientMessageScheduler();
        var active = scheduler.EnqueueInputAsync(async token =>
        {
            started.TrySetResult();
            await NewCompletion().Task.WaitAsync(token);
        }, CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TestTimeout);

        scheduler.AbortActiveWrites();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active)
            .WaitAsync(TestTimeout);
        await scheduler.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Completion_continuation_can_enqueue_another_request()
    {
        var secondInvoked = false;
        await using var scheduler = new ClientMessageScheduler();
        var first = scheduler.EnqueueInputAsync(_ => ValueTask.CompletedTask, CancellationToken.None).AsTask();
        var continuation = first.ContinueWith(
            _ => scheduler.EnqueueInputAsync(_ =>
            {
                secondInvoked = true;
                return ValueTask.CompletedTask;
            }, CancellationToken.None).AsTask(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();

        await continuation.WaitAsync(TestTimeout);

        Assert.True(secondInvoked);
    }

    [Fact]
    public async Task Multiple_producers_never_execute_writes_concurrently()
    {
        var activeWrites = 0;
        var maximumActiveWrites = 0;
        var completed = new ConcurrentQueue<int>();
        await using var scheduler = new ClientMessageScheduler();

        var producers = Enumerable.Range(0, 8).Select(producer => Task.Run(async () =>
        {
            for (var item = 0; item < 20; item++)
            {
                var value = (producer * 20) + item;
                await scheduler.EnqueueInputAsync(async token =>
                {
                    _ = token;
                    var active = Interlocked.Increment(ref activeWrites);
                    UpdateMaximum(ref maximumActiveWrites, active);
                    await Task.Yield();
                    completed.Enqueue(value);
                    _ = Interlocked.Decrement(ref activeWrites);
                }, CancellationToken.None);
            }
        })).ToArray();

        await Task.WhenAll(producers).WaitAsync(TestTimeout);

        Assert.Equal(1, maximumActiveWrites);
        Assert.Equal(160, completed.Count);
    }

    [Fact]
    public async Task Input_queue_depth_returns_to_zero_after_blocked_writes_complete()
    {
        var started = NewCompletion();
        var release = NewCompletion();
        await using var scheduler = new ClientMessageScheduler();
        using var guard = new CompletionRelease(release);
        var active = scheduler.EnqueueInputAsync(async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        }, CancellationToken.None).AsTask();
        await started.Task;
        var second = scheduler.EnqueueInputAsync(_ => ValueTask.CompletedTask, CancellationToken.None).AsTask();
        var third = scheduler.EnqueueInputAsync(_ => ValueTask.CompletedTask, CancellationToken.None).AsTask();

        Assert.Equal(2, scheduler.PerformanceSnapshot.InputQueueDepth);
        release.TrySetResult();
        await Task.WhenAll(active, second, third);

        Assert.Equal(0, scheduler.PerformanceSnapshot.InputQueueDepth);
        Assert.True(scheduler.PerformanceSnapshot.SampleSequence > 0);
    }

    [Fact]
    public async Task Canceled_pending_input_publishes_zero_depth_without_a_later_write()
    {
        var started = NewCompletion();
        var release = NewCompletion();
        using var cancellation = new CancellationTokenSource();
        await using var scheduler = new ClientMessageScheduler();
        using var guard = new CompletionRelease(release);
        var active = scheduler.EnqueueInputAsync(async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        }, CancellationToken.None).AsTask();
        await started.Task;
        var pending = scheduler.EnqueueInputAsync(_ => ValueTask.CompletedTask, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(0, scheduler.PerformanceSnapshot.InputQueueDepth);
        release.TrySetResult();
        await active;
        Assert.Equal(0, scheduler.PerformanceSnapshot.InputQueueDepth);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int current;
        do
        {
            current = Volatile.Read(ref maximum);
            if (candidate <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, candidate, current) != current);
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class CompletionRelease(TaskCompletionSource completion) : IDisposable
    {
        public void Dispose() => completion.TrySetResult();
    }
}
