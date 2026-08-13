using WinARD.Desktop.Views;
using Xunit;

namespace WinARD.Desktop.Tests.Views;

#pragma warning disable CA1707

public sealed class QualityOverlayPlacementTests
{
    [Fact]
    public void Placement_is_below_anchor_and_clamped_inside_window()
    {
        var placement = QualityOverlayPlacement.Calculate(
            windowWidth: 800, windowHeight: 500,
            anchorLeft: 700, anchorBottom: 50,
            panelWidth: 360, desiredHeight: 620,
            margin: 8);

        Assert.Equal(432, placement.Left);
        Assert.Equal(50, placement.Top);
        Assert.Equal(442, placement.MaxHeight);
    }

    [Fact]
    public void Placement_never_returns_negative_geometry_for_small_windows()
    {
        var placement = QualityOverlayPlacement.Calculate(100, 40, 90, 35, 360, 620, 8);

        Assert.Equal(8, placement.Left);
        Assert.Equal(8, placement.Top);
        Assert.Equal(27, placement.MaxHeight);
        Assert.Equal(84, placement.Width);
        Assert.True(placement.Left + placement.Width <= 92);
    }
}
