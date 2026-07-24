namespace WinARD.ProtocolProbe;

public sealed class ProbeTimeoutException : TimeoutException
{
    public ProbeTimeoutException(Exception innerException)
        : base("The protocol probe timed out.", innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
    }
}
