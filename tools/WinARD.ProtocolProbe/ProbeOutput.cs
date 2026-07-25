namespace WinARD.ProtocolProbe;

public static class ProbeOutput
{
    private const int MaximumDisplayedDirtyRectangles = 8;

    public static string FormatCapture(ProbeCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var dirtyRects = string.Join(
            ", ",
            capture.DirtyRects.Take(MaximumDisplayedDirtyRectangles).Select(rectangle =>
                $"({rectangle.X},{rectangle.Y}) {rectangle.Width}x{rectangle.Height}"));
        var omittedCount = capture.DirtyRects.Count - MaximumDisplayedDirtyRectangles;
        if (omittedCount > 0)
        {
            dirtyRects += $", ... ({omittedCount} omitted)";
        }

        return $"Captured: {capture.Width}x{capture.Height} -> {capture.Path}{Environment.NewLine}Dirty: {dirtyRects}";
    }

    public static string FormatFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            ProbeTimeoutException => "Probe timed out.",
            OperationCanceledException => "Cancelled.",
            _ => $"Probe failed: {exception.Message}",
        };
    }
}
