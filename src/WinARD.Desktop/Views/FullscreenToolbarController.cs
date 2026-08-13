namespace WinARD.Desktop.Views;

public enum FullscreenEscapeAction
{
    None,
    CloseDropDown,
    CloseQualityOverlay,
    ExitFullscreen,
}

public sealed class FullscreenToolbarController
{
    private long _generation;

    public bool IsFullscreen { get; private set; }
    public bool IsToolbarVisible { get; private set; } = true;
    public bool IsHotZoneEnabled => IsFullscreen;
    public long? PendingHideGeneration { get; private set; }

    public void SetFullscreen(bool fullscreen)
    {
        IsFullscreen = fullscreen;
        PendingHideGeneration = null;
        _generation++;
        IsToolbarVisible = !fullscreen;
    }

    public void ShowFromHotZone()
    {
        if (!IsFullscreen)
        {
            return;
        }

        PendingHideGeneration = null;
        _generation++;
        IsToolbarVisible = true;
    }

    public long ScheduleHide()
    {
        if (!IsFullscreen || !IsToolbarVisible)
        {
            PendingHideGeneration = null;
            return _generation;
        }

        PendingHideGeneration = ++_generation;
        return _generation;
    }

    public bool TryHide(long generation)
    {
        if (!IsFullscreen || PendingHideGeneration != generation)
        {
            return false;
        }

        PendingHideGeneration = null;
        IsToolbarVisible = false;
        return true;
    }

    public static FullscreenEscapeAction EscapeAction(
        bool isDropDownOpen,
        bool isQualityOverlayOpen,
        bool isFullscreen) =>
        isDropDownOpen
            ? FullscreenEscapeAction.CloseDropDown
            : isQualityOverlayOpen
                ? FullscreenEscapeAction.CloseQualityOverlay
                : isFullscreen
                    ? FullscreenEscapeAction.ExitFullscreen
                    : FullscreenEscapeAction.None;

    public static bool IsRecallShortcut(
        bool isFullscreen,
        bool isControlDown,
        bool isAltDown,
        bool isTKey) =>
        isFullscreen && isControlDown && isAltDown && isTKey;
}
