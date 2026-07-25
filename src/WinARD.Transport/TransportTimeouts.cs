namespace WinARD.Transport;

public sealed record TransportTimeouts
{
    public static TransportTimeouts Default { get; } =
        new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));

    public TransportTimeouts(TimeSpan timeout)
        : this(timeout, timeout)
    {
    }

    public TransportTimeouts(TimeSpan dnsResolution, TimeSpan connection)
    {
        DnsResolution = ValidTimeout(dnsResolution, nameof(dnsResolution));
        Connection = ValidTimeout(connection, nameof(connection));
    }

    public TimeSpan DnsResolution { get; }

    public TimeSpan Connection { get; }

    private static TimeSpan ValidTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout must be finite and positive.");
        }

        return timeout;
    }
}

public enum TransportTimeoutStage
{
    DnsResolution,
    Connection,
}

public sealed class TransportTimeoutException : TimeoutException
{
    public TransportTimeoutException(TransportTimeoutStage stage)
        : base(stage == TransportTimeoutStage.DnsResolution
            ? "Remote host name resolution timed out."
            : "Remote transport connection timed out.")
    {
        Stage = stage;
    }

    public TransportTimeoutStage Stage { get; }
}
