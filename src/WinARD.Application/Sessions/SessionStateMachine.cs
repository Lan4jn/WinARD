using WinARD.Domain.Sessions;

namespace WinARD.Application.Sessions;

public sealed class SessionStateMachine
{
    public SessionState Current { get; private set; } = SessionState.Idle;

    public void MoveTo(SessionState next)
    {
        if (!IsAllowed(Current, next))
        {
            throw new InvalidOperationException($"Invalid session transition: {Current} -> {next}");
        }

        Current = next;
    }

    private static bool IsAllowed(SessionState current, SessionState next) => (current, next) switch
    {
        (SessionState.Idle, SessionState.Resolving) => true,
        (SessionState.Resolving, SessionState.Connecting or SessionState.Failed or SessionState.Disconnecting) => true,
        (SessionState.Connecting, SessionState.Negotiating or SessionState.Failed or SessionState.Disconnecting) => true,
        (SessionState.Negotiating, SessionState.Authenticating or SessionState.Failed or SessionState.Disconnecting) => true,
        (SessionState.Authenticating, SessionState.Initializing or SessionState.Failed or SessionState.Disconnecting) => true,
        (SessionState.Initializing, SessionState.Connected or SessionState.Failed or SessionState.Disconnecting) => true,
        (SessionState.Connected, SessionState.Reconnecting or SessionState.Disconnecting or SessionState.Failed) => true,
        (SessionState.Reconnecting, SessionState.Connecting or SessionState.Disconnecting or SessionState.Failed) => true,
        (SessionState.Disconnecting, SessionState.Idle) => true,
        (SessionState.Failed, SessionState.Resolving or SessionState.Idle) => true,
        _ => false,
    };
}
