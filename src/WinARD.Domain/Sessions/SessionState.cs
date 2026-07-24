namespace WinARD.Domain.Sessions;

public enum SessionState
{
    Idle,
    Resolving,
    Connecting,
    Negotiating,
    Authenticating,
    Initializing,
    Connected,
    Reconnecting,
    Disconnecting,
    Failed,
}
