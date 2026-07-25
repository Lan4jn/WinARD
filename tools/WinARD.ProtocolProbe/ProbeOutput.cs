namespace WinARD.ProtocolProbe;

public static class ProbeOutput
{
    public static string FormatCapture(ProbeCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var dirtyRects = string.Join(
            ", ",
            capture.DirtyRects.Select(rectangle =>
                $"({rectangle.X},{rectangle.Y}) {rectangle.Width}x{rectangle.Height}"));
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
