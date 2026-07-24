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
}
