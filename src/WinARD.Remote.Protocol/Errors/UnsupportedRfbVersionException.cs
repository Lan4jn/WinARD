namespace WinARD.Remote.Protocol.Errors;

public sealed class UnsupportedRfbVersionException : Exception
{
    public UnsupportedRfbVersionException(string serverBanner)
        : base($"The server uses an unsupported RFB version banner: {serverBanner}.")
    {
        ArgumentNullException.ThrowIfNull(serverBanner);
        ServerBanner = serverBanner;
    }

    public string ServerBanner { get; }
}
