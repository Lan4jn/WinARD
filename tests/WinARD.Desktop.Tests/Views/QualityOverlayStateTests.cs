using WinARD.Desktop.Views;
using Xunit;

namespace WinARD.Desktop.Tests.Views;

#pragma warning disable CA1707

public sealed class QualityOverlayStateTests
{
    [Fact]
    public void Toggle_and_close_have_stable_visibility_semantics()
    {
        var state = new QualityOverlayState();

        Assert.True(state.Toggle());
        Assert.False(state.Toggle());
        Assert.True(state.Open());
        Assert.False(state.Close());
    }

    [Fact]
    public void Escape_closes_dropdown_before_overlay()
    {
        var state = new QualityOverlayState();
        state.Open();
        state.SetDropDownOpen(true);

        Assert.Equal(QualityOverlayEscapeAction.CloseDropDown, state.HandleEscape());
        Assert.True(state.IsOpen);
        Assert.Equal(QualityOverlayEscapeAction.CloseOverlay, state.HandleEscape());
        Assert.False(state.IsOpen);
    }

    [Fact]
    public void Dropdown_selection_does_not_close_overlay()
    {
        var state = new QualityOverlayState();
        state.Open();
        state.SetDropDownOpen(true);
        state.SetDropDownOpen(false);

        Assert.True(state.IsOpen);
    }
}
