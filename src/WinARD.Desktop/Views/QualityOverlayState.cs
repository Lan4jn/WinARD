namespace WinARD.Desktop.Views;

internal enum QualityOverlayEscapeAction
{
    None,
    CloseDropDown,
    CloseOverlay,
}

internal sealed class QualityOverlayState
{
    public bool IsOpen { get; private set; }

    public bool IsDropDownOpen { get; private set; }

    public bool Open() => IsOpen = true;

    public bool Close()
    {
        IsDropDownOpen = false;
        return IsOpen = false;
    }

    public bool Toggle() => IsOpen ? Close() : Open();

    public void SetDropDownOpen(bool value) => IsDropDownOpen = IsOpen && value;

    public QualityOverlayEscapeAction HandleEscape()
    {
        if (IsDropDownOpen)
        {
            IsDropDownOpen = false;
            return QualityOverlayEscapeAction.CloseDropDown;
        }

        if (IsOpen)
        {
            Close();
            return QualityOverlayEscapeAction.CloseOverlay;
        }

        return QualityOverlayEscapeAction.None;
    }
}

internal readonly record struct QualityOverlayPlacement(double Left, double Top, double MaxHeight)
{
    public static QualityOverlayPlacement Calculate(
        double windowWidth,
        double windowHeight,
        double anchorLeft,
        double anchorBottom,
        double panelWidth,
        double desiredHeight,
        double margin)
    {
        var left = Math.Max(margin, Math.Min(anchorLeft, windowWidth - panelWidth - margin));
        var availableBelow = Math.Max(0, windowHeight - anchorBottom - margin);
        var availableAbove = Math.Max(0, anchorBottom - margin);
        var useAbove = availableBelow < Math.Min(desiredHeight, 160) && availableAbove > availableBelow;
        var maxHeight = Math.Max(0, Math.Min(desiredHeight, useAbove ? availableAbove : availableBelow));
        var top = useAbove
            ? Math.Max(margin, anchorBottom - maxHeight)
            : Math.Max(margin, Math.Min(anchorBottom, windowHeight - margin));
        return new(left, top, maxHeight);
    }
}
