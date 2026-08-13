using WinARD.Desktop.Services;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Services;

public sealed class RemoteSessionWindowCloseCoordinatorTests
{
    [Fact]
    public async Task Scheduler_failure_does_not_skip_lifetime_or_window_close()
    {
        var calls = new List<string>();
        var schedulerFailure = new AggregateException(new InvalidOperationException("dispatch"));
        var coordinator = new RemoteSessionWindowCloseCoordinator(
            () => calls.Add("scheduler-cancel"),
            () =>
            {
                calls.Add("scheduler-dispose");
                return Task.FromException(schedulerFailure);
            },
            () => calls.Add("lifetime-cancel"),
            () =>
            {
                calls.Add("window-close");
                return Task.CompletedTask;
            });

        var failure = await Assert.ThrowsAsync<AggregateException>(coordinator.CloseAsync);

        Assert.Same(schedulerFailure, failure);
        Assert.Equal(
            ["scheduler-cancel", "scheduler-dispose", "lifetime-cancel", "window-close"],
            calls);
    }

    [Fact]
    public async Task Concurrent_close_calls_share_one_best_effort_sequence()
    {
        var schedulerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScheduler = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCalls = 0;
        var lifetimeCalls = 0;
        var closeCalls = 0;
        var coordinator = new RemoteSessionWindowCloseCoordinator(
            () => cancelCalls++,
            async () =>
            {
                schedulerEntered.TrySetResult();
                await releaseScheduler.Task;
            },
            () => lifetimeCalls++,
            () =>
            {
                closeCalls++;
                return Task.CompletedTask;
            });

        var first = coordinator.CloseAsync();
        await schedulerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = coordinator.CloseAsync();

        Assert.Same(first, second);
        releaseScheduler.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, cancelCalls);
        Assert.Equal(1, lifetimeCalls);
        Assert.Equal(1, closeCalls);
    }

    [Fact]
    public async Task Synchronous_closed_event_reentry_does_not_start_another_sequence()
    {
        RemoteSessionWindowCloseCoordinator? coordinator = null;
        Task? reentrant = null;
        var cancelCalls = 0;
        var disposeCalls = 0;
        var lifetimeCalls = 0;
        var closeCalls = 0;
        coordinator = new RemoteSessionWindowCloseCoordinator(
            () => cancelCalls++,
            () =>
            {
                disposeCalls++;
                return Task.CompletedTask;
            },
            () => lifetimeCalls++,
            () =>
            {
                closeCalls++;
                reentrant = coordinator!.CloseAsync();
                return Task.CompletedTask;
            });

        var first = coordinator.CloseAsync();
        await first;

        Assert.Same(first, reentrant);
        Assert.Equal(1, cancelCalls);
        Assert.Equal(1, disposeCalls);
        Assert.Equal(1, lifetimeCalls);
        Assert.Equal(1, closeCalls);
    }
}
