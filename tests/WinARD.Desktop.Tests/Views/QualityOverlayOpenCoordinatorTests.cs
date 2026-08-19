using WinARD.Desktop.Views;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Views;

public sealed class QualityOverlayOpenCoordinatorTests
{
    [Fact]
    public async Task Open_releases_remote_input_before_publishing_visible_and_coalesces_repeated_clicks()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var coordinator = new QualityOverlayOpenCoordinator(
            async token =>
            {
                events.Add("release-start");
                releaseStarted.TrySetResult();
                await release.Task.WaitAsync(token);
                events.Add("release-end");
            },
            visible => events.Add(visible ? "visible" : "closed"));

        var first = coordinator.ToggleAsync(default);
        var second = coordinator.ToggleAsync(default);
        await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["release-start"], events);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(
            ["release-start", "release-end", "release-start", "release-end", "visible"],
            events);
    }

    [Fact]
    public async Task Close_during_release_prevents_late_open()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visible = false;
        var coordinator = new QualityOverlayOpenCoordinator(
            token => release.Task.WaitAsync(token),
            value => visible = value);

        var opening = coordinator.ToggleAsync(default);
        coordinator.Close();
        release.SetResult();
        await opening;

        Assert.False(visible);
    }

    [Fact]
    public async Task Opening_is_local_input_mode_and_performs_final_release_before_visible()
    {
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCalls = 0;
        var visibleAfterCalls = 0;
        var coordinator = new QualityOverlayOpenCoordinator(
            async token =>
            {
                releaseCalls++;
                if (releaseCalls == 1)
                {
                    await firstRelease.Task.WaitAsync(token);
                }
            },
            visible =>
            {
                if (visible)
                {
                    visibleAfterCalls = releaseCalls;
                }
            });

        var opening = coordinator.ToggleAsync(default);
        Assert.True(coordinator.ConsumesRemoteInput);
        firstRelease.SetResult();
        await opening;

        Assert.Equal(2, releaseCalls);
        Assert.Equal(2, visibleAfterCalls);
    }

    [Fact]
    public async Task External_close_synchronizes_visibility_so_next_click_reopens_once()
    {
        var visibleChanges = new List<bool>();
        var coordinator = new QualityOverlayOpenCoordinator(
            _ => Task.CompletedTask,
            visibleChanges.Add);

        await coordinator.ToggleAsync(default);
        coordinator.Close();
        await coordinator.ToggleAsync(default);

        Assert.Equal([true, false, true], visibleChanges);
    }
}
