namespace WinARD.Transport;

public sealed record TransportTimeouts
{
    public static TransportTimeouts Default { get; } =
        new(TimeSpan.FromSeconds(30));

    public TransportTimeouts(TimeSpan timeout)
    {
        Connection = ValidTimeout(timeout, nameof(timeout));
    }

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
    Connection,
}

public sealed class TransportTimeoutException : TimeoutException
{
    public TransportTimeoutException(TransportTimeoutStage stage)
        : base("Remote transport connection timed out.")
    {
        Stage = stage;
    }

    public TransportTimeoutStage Stage { get; }
}
