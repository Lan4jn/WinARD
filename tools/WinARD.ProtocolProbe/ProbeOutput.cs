namespace WinARD.ProtocolProbe;

using WinARD.ProtocolProbe.EncodingResearch;

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

    public static string FormatPointerSmoke(ProbePointerSmoke pointerSmoke)
    {
        ArgumentNullException.ThrowIfNull(pointerSmoke);
        return $"Pointer smoke: sent buttonless move to ({pointerSmoke.X},{pointerSmoke.Y}) within {pointerSmoke.Width}x{pointerSmoke.Height}. RFB does not confirm server execution.";
    }

    public static string FormatRdmListening(int port, string profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return $"RDM capture listening on 127.0.0.1:{port} for profile {profile}.";
    }

    public static string FormatRdmSaved(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return $"RDM capture saved: {path}";
    }

    public static string FormatRdmComparisonCandidate(int encodingId) =>
        $"RDM comparison candidate signed encoding ID: {encodingId}.";

    public static string FormatEncodingPrefixCaptured(EncodingPrefixCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return $"Encoding prefix captured: signed encoding ID {capture.EncodingId}, "
            + $"rectangle ({capture.Rectangle.X},{capture.Rectangle.Y}) "
            + $"{capture.Rectangle.Width}x{capture.Rectangle.Height}, {capture.PrefixLength} bytes. "
            + "Saved as manifest.json and payload-prefix.bin.";
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
