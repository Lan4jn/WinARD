namespace WinARD.Domain.Errors;

public enum ConnectionStage
{
    Resolving,
    Connecting,
    Negotiating,
    Authenticating,
    Initializing,
    Connected,
    Reconnecting,
    Disconnecting,
}
