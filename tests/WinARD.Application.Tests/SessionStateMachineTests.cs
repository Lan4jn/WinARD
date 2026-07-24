using WinARD.Application.Sessions;
using WinARD.Domain.Sessions;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Application.Tests;

public sealed class SessionStateMachineTests
{
    [Fact]
    public void Current_starts_idle()
    {
        Assert.Equal(SessionState.Idle, new SessionStateMachine().Current);
    }

    [Fact]
    public void MoveTo_allows_complete_success_path()
    {
        var machine = new SessionStateMachine();

        foreach (var state in new[]
                 {
                     SessionState.Resolving, SessionState.Connecting, SessionState.Negotiating,
                     SessionState.Authenticating, SessionState.Initializing, SessionState.Connected,
                 })
        {
            machine.MoveTo(state);
        }

        Assert.Equal(SessionState.Connected, machine.Current);
    }

    [Theory]
    [InlineData(SessionState.Resolving)]
    [InlineData(SessionState.Connecting)]
    [InlineData(SessionState.Negotiating)]
    [InlineData(SessionState.Authenticating)]
    [InlineData(SessionState.Initializing)]
    [InlineData(SessionState.Connected)]
    [InlineData(SessionState.Reconnecting)]
    public void Active_states_allow_failure_and_cancellation(SessionState state)
    {
        var failureMachine = MoveTo(state);
        failureMachine.MoveTo(SessionState.Failed);
        Assert.Equal(SessionState.Failed, failureMachine.Current);

        var cancellationMachine = MoveTo(state);
        cancellationMachine.MoveTo(SessionState.Disconnecting);
        cancellationMachine.MoveTo(SessionState.Idle);
        Assert.Equal(SessionState.Idle, cancellationMachine.Current);
    }

    [Fact]
    public void Connected_cannot_transition_directly_to_authenticating()
    {
        var machine = MoveTo(SessionState.Connected);

        var exception = Assert.Throws<InvalidOperationException>(() => machine.MoveTo(SessionState.Authenticating));

        Assert.Equal("Invalid session transition: Connected -> Authenticating", exception.Message);
    }

    [Fact]
    public void Idle_cannot_transition_directly_to_connected()
    {
        var machine = new SessionStateMachine();

        var exception = Assert.Throws<InvalidOperationException>(() => machine.MoveTo(SessionState.Connected));

        Assert.Equal("Invalid session transition: Idle -> Connected", exception.Message);
    }

    [Fact]
    public void MoveTo_rejects_reentering_current_state()
    {
        var machine = new SessionStateMachine();

        var exception = Assert.Throws<InvalidOperationException>(() => machine.MoveTo(SessionState.Idle));

        Assert.Equal("Invalid session transition: Idle -> Idle", exception.Message);
    }

    [Theory]
    [InlineData(SessionState.Resolving)]
    [InlineData(SessionState.Idle)]
    public void Failed_can_retry_or_reset(SessionState next)
    {
        var machine = MoveTo(SessionState.Resolving);
        machine.MoveTo(SessionState.Failed);

        machine.MoveTo(next);

        Assert.Equal(next, machine.Current);
    }

    [Fact]
    public void MoveTo_all_state_combinations_match_transition_contract()
    {
        foreach (var current in Enum.GetValues<SessionState>())
        {
            foreach (var next in Enum.GetValues<SessionState>())
            {
                var machine = MoveTo(current);

                if (IsAllowed(current, next))
                {
                    machine.MoveTo(next);
                    Assert.Equal(next, machine.Current);
                }
                else
                {
                    Assert.Throws<InvalidOperationException>(() => machine.MoveTo(next));
                    Assert.Equal(current, machine.Current);
                }
            }
        }
    }

    private static SessionStateMachine MoveTo(SessionState target)
    {
        var machine = new SessionStateMachine();
        if (target == SessionState.Idle)
        {
            return machine;
        }

        if (target == SessionState.Disconnecting)
        {
            machine.MoveTo(SessionState.Resolving);
            machine.MoveTo(SessionState.Disconnecting);
            return machine;
        }

        if (target == SessionState.Failed)
        {
            machine.MoveTo(SessionState.Resolving);
            machine.MoveTo(SessionState.Failed);
            return machine;
        }

        var path = new[]
        {
            SessionState.Resolving, SessionState.Connecting, SessionState.Negotiating,
            SessionState.Authenticating, SessionState.Initializing, SessionState.Connected,
            SessionState.Reconnecting,
        };

        foreach (var state in path)
        {
            machine.MoveTo(state);
            if (state == target)
            {
                return machine;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(target));
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
