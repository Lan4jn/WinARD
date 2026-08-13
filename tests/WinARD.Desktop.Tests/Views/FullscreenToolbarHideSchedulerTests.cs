using WinARD.Desktop.Views;
using Xunit;

namespace WinARD.Desktop.Tests.Views;

public sealed class FullscreenToolbarHideSchedulerTests
{
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
}
