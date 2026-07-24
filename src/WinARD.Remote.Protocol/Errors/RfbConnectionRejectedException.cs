using WinARD.Remote.Protocol.Handshake;

namespace WinARD.Remote.Protocol.Errors;

public sealed class RfbConnectionRejectedException : Exception
{
    public RfbConnectionRejectedException(RfbVersion version, string reason, bool isReasonTruncated)
        : base(CreateMessage(reason, isReasonTruncated))
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(reason);

        Version = version;
        Reason = reason;
        IsReasonTruncated = isReasonTruncated;
    }

    public RfbVersion Version { get; }

    public string Reason { get; }

    public bool IsReasonTruncated { get; }

    private static string CreateMessage(string reason, bool isReasonTruncated)
    {
        ArgumentNullException.ThrowIfNull(reason);

        if (reason.Length == 0)
        {
            return "The server rejected the connection.";
        }

        var suffix = isReasonTruncated ? " (reason truncated)" : string.Empty;
        return $"The server rejected the connection: {reason}{suffix}";
    }
}
