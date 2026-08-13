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

internal readonly record struct QualityOverlayPlacement(double Left, double Top, double Width, double MaxHeight)
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
        var width = Math.Max(0, Math.Min(panelWidth, windowWidth - (2 * margin)));
        var left = Math.Max(margin, Math.Min(anchorLeft, windowWidth - width - margin));
        var availableBelow = Math.Max(0, windowHeight - anchorBottom - margin);
        var availableAbove = Math.Max(0, anchorBottom - margin);
        var useAbove = availableBelow < Math.Min(desiredHeight, 160) && availableAbove > availableBelow;
        var maxHeight = Math.Max(0, Math.Min(desiredHeight, useAbove ? availableAbove : availableBelow));
        var top = useAbove
            ? Math.Max(margin, anchorBottom - maxHeight)
            : Math.Max(margin, Math.Min(anchorBottom, windowHeight - margin));
        return new(left, top, width, maxHeight);
    }
}

internal sealed class QualityOverlayOpenCoordinator(
    Func<CancellationToken, Task> releaseInput,
    Action<bool> setVisible)
{
    private readonly object _sync = new();
    private Task? _openTask;
    private long _version;
    private bool _visible;
    private bool _opening;

    public bool ConsumesRemoteInput
    {
        get
        {
            lock (_sync)
            {
                return _opening || _visible;
            }
        }
    }

    public Task ToggleAsync(CancellationToken token)
    {
        lock (_sync)
        {
            if (_visible)
            {
                CloseNoLock();
                return Task.CompletedTask;
            }
            if (_openTask is null)
            {
                _opening = true;
                _openTask = OpenAsync(++_version, token);
            }
            return _openTask;
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            CloseNoLock();
        }
    }

    private void CloseNoLock()
    {
        _version++;
        _opening = false;
        _visible = false;
        setVisible(false);
    }

    private async Task OpenAsync(long version, CancellationToken token)
    {
        try
        {
            await Task.Yield();
            await releaseInput(token);
            await releaseInput(token);
            lock (_sync)
            {
                if (version == _version)
                {
                    _opening = false;
                    _visible = true;
                    setVisible(true);
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                if (version == _version)
                {
                    _opening = false;
                }
                _openTask = null;
            }
        }
    }
}
