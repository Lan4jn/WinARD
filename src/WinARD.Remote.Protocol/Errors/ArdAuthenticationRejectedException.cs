namespace WinARD.Remote.Protocol.Errors;

public sealed class ArdAuthenticationRejectedException : Exception
{
    public ArdAuthenticationRejectedException(uint resultCode, string? reason, bool isReasonTruncated)
        : this(resultCode, reason, isReasonTruncated, null)
    {
    }

    public ArdAuthenticationRejectedException(
        uint resultCode,
        string? reason,
        bool isReasonTruncated,
        Exception? innerException)
        : base(CreateMessage(resultCode, reason, isReasonTruncated), innerException)
    {
        ResultCode = resultCode;
        Reason = reason;
        IsReasonTruncated = isReasonTruncated;
    }

    public uint ResultCode { get; }

    public string? Reason { get; }

    public bool IsReasonTruncated { get; }

    private static string CreateMessage(uint resultCode, string? reason, bool isReasonTruncated)
    {
        var suffix = isReasonTruncated ? " (reason truncated)" : string.Empty;
        return string.IsNullOrEmpty(reason)
            ? $"Apple Remote Desktop authentication was rejected (result {resultCode})."
            : $"Apple Remote Desktop authentication was rejected (result {resultCode}): {reason}{suffix}";
    }
}
