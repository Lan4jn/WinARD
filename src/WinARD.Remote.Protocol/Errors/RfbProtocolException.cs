namespace WinARD.Remote.Protocol.Errors;

public sealed class RfbProtocolException : Exception
{
    public RfbProtocolException(string message)
        : base(message)
    {
    }

    public RfbProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private RfbProtocolException(string message, RfbProtocolFailureInfo failure)
        : base(RequireMessage(message))
    {
        Failure = RequireFailure(failure);
    }

    public static RfbProtocolException Create(string message, RfbProtocolFailureInfo failure) =>
        new(message, failure);

    internal RfbProtocolException(
        string message,
        Exception innerException,
        RfbProtocolFailureInfo failure)
        : base(RequireMessage(message), RequireInnerException(innerException))
    {
        Failure = RequireFailure(failure);
    }

    public RfbProtocolFailureInfo? Failure { get; }

    public RfbProtocolException WithContext(RfbProtocolFailureInfo context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new RfbProtocolException(
            Message,
            this,
            Failure?.FillMissingFrom(context) ?? context);
    }

    private static string RequireMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message;
    }

    private static Exception RequireInnerException(Exception innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        return innerException;
    }

    private static RfbProtocolFailureInfo RequireFailure(RfbProtocolFailureInfo failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure;
    }
}
