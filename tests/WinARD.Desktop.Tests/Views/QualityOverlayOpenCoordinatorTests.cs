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
        var events = new List<string>();
        var coordinator = new QualityOverlayOpenCoordinator(
            async token =>
            {
                events.Add("release-start");
                await release.Task.WaitAsync(token);
                events.Add("release-end");
            },
            visible => events.Add(visible ? "visible" : "closed"));

        var first = coordinator.ToggleAsync(default);
        var second = coordinator.ToggleAsync(default);
        Assert.Equal(["release-start"], events);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["release-start", "release-end", "visible"], events);
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
}
