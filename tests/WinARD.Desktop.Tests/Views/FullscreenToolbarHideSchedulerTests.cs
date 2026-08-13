using WinARD.Desktop.Views;
using Xunit;

namespace WinARD.Desktop.Tests.Views;

public sealed class FullscreenToolbarHideSchedulerTests
{
    [Fact]
    public async Task DisposeWaitsForAllActiveDispatchesIncludingSupersededOperation()
    {
        var dispatchA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var scheduler = new FullscreenToolbarHideScheduler(
            _ => Task.CompletedTask,
            _ => Interlocked.Increment(ref calls) == 1 ? dispatchA.Task : dispatchB.Task);
        scheduler.Schedule(1, _ => true);
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        scheduler.Schedule(2, _ => true);
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 2);

        var dispose = scheduler.DisposeAsync().AsTask();

        Assert.False(dispose.IsCompleted);
        dispatchB.SetResult();
        Assert.False(dispose.IsCompleted);
        dispatchA.SetResult();
        await dispose;
    }

    [Fact]
    public async Task DispatchExceptionIsObservedByWhenIdle()
    {
        var scheduler = new FullscreenToolbarHideScheduler(
            _ => Task.CompletedTask,
            _ => Task.FromException(new InvalidOperationException("dispatch failed")));
        scheduler.Schedule(1, _ => true);

        var exception = await Assert.ThrowsAsync<AggregateException>(scheduler.WhenIdleAsync);

        Assert.Contains(exception.InnerExceptions, error => error.Message == "dispatch failed");
        await scheduler.DisposeAsync();
    }

    [Fact]
    public async Task ScheduleAfterDisposeIsRejectedAndConcurrentScheduleIsEitherTrackedOrRejected()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new FullscreenToolbarHideScheduler(
            token => gate.Task.WaitAsync(token),
            _ => Task.CompletedTask);
        scheduler.Schedule(1, _ => true);

        var dispose = scheduler.DisposeAsync().AsTask();
        var race = Record.Exception(() => scheduler.Schedule(2, _ => true));
        gate.TrySetResult();
        await dispose;

        Assert.True(race is null or ObjectDisposedException);
        Assert.Throws<ObjectDisposedException>(() => scheduler.Schedule(3, _ => true));
        await scheduler.WhenIdleAsync();
    }

    [Fact]
    public async Task QueuedCompletionFromAUsesCapturedGenerationAndCannotHideScheduleB()
    {
        var delays = new Queue<TaskCompletionSource>([
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        ]);
        var dispatcher = new Queue<Action>();
        var delayCalls = 0;
        var controller = new FullscreenToolbarController();
        controller.SetFullscreen(true);
        controller.ShowFromHotZone();
        await using var scheduler = new FullscreenToolbarHideScheduler(
            token => delays.ElementAt(delayCalls++).Task.WaitAsync(token),
            action => { dispatcher.Enqueue(action); return Task.CompletedTask; });

        var generationA = controller.ScheduleHide();
        scheduler.Schedule(generationA, controller.TryHide);
        delays.ElementAt(0).SetResult();
        await scheduler.WhenIdleAsync();
        controller.ShowFromHotZone();
        var generationB = controller.ScheduleHide();
        scheduler.Schedule(generationB, controller.TryHide);

        dispatcher.Dequeue()();

        Assert.True(controller.IsToolbarVisible);
        Assert.Equal(generationB, controller.PendingHideGeneration);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }
}
