using WinARD.Desktop.Views;
using Xunit;

namespace WinARD.Desktop.Tests.Views;

public sealed class FullscreenToolbarControllerTests
{
    [Fact]
    public void WindowedModeKeepsToolbarVisibleAndDisablesHotZone()
    {
        var sut = new FullscreenToolbarController();

        Assert.True(sut.IsToolbarVisible);
        Assert.False(sut.IsHotZoneEnabled);
        Assert.Null(sut.PendingHideGeneration);
    }

    [Fact]
    public void EnteringFullscreenHidesToolbarUntilHotZoneShowsIt()
    {
        var sut = new FullscreenToolbarController();

        sut.SetFullscreen(true);
        Assert.False(sut.IsToolbarVisible);
        Assert.True(sut.IsHotZoneEnabled);

        sut.ShowFromHotZone();
        Assert.True(sut.IsToolbarVisible);
    }

    [Fact]
    public void LeavingToolbarSchedulesHideAndStaleGenerationCannotHideAgain()
    {
        var sut = new FullscreenToolbarController();
        sut.SetFullscreen(true);
        sut.ShowFromHotZone();
        var first = sut.ScheduleHide();
        sut.ShowFromHotZone();

        Assert.False(sut.TryHide(first));
        Assert.True(sut.IsToolbarVisible);

        var current = sut.ScheduleHide();
        Assert.True(sut.TryHide(current));
        Assert.False(sut.IsToolbarVisible);
    }

    [Fact]
    public void LeavingFullscreenInvalidatesPendingHideAndKeepsToolbarVisible()
    {
        var sut = new FullscreenToolbarController();
        sut.SetFullscreen(true);
        sut.ShowFromHotZone();
        var generation = sut.ScheduleHide();

        sut.SetFullscreen(false);

        Assert.False(sut.TryHide(generation));
        Assert.True(sut.IsToolbarVisible);
        Assert.False(sut.IsHotZoneEnabled);
    }

    [Theory]
    [InlineData(true, true, true, FullscreenEscapeAction.CloseDropDown)]
    [InlineData(false, true, true, FullscreenEscapeAction.CloseQualityOverlay)]
    [InlineData(false, false, true, FullscreenEscapeAction.ExitFullscreen)]
    [InlineData(false, false, false, FullscreenEscapeAction.None)]
    public void EscapePriorityIsDropDownThenOverlayThenFullscreen(
        bool dropDown,
        bool overlay,
        bool fullscreen,
        FullscreenEscapeAction expected)
    {
        Assert.Equal(expected, FullscreenToolbarController.EscapeAction(dropDown, overlay, fullscreen));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void RecallShortcutRequiresFullscreenControlAltT(
        bool fullscreen,
        bool control,
        bool alt,
        bool expected)
    {
        Assert.Equal(expected, FullscreenToolbarController.IsRecallShortcut(
            fullscreen, control, alt, isTKey: true));
    }
}
