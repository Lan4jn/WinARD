namespace WinARD.ProtocolProbe;

public static class ProbeOutput
{
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
