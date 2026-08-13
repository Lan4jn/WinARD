using WinARD.Application.Sessions;
using WinARD.Desktop.Services;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Services;

public sealed class ReconnectTransitionCoordinatorTests
{
    [Fact]
    public async Task Manual_retry_waits_for_blocked_automatic_attempt_then_releases_and_reconnects_once()
    {
        var automaticEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var automaticExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservation = new TrackingDisposable();
        var events = new List<string>();
        var automatic = new AutomaticReconnectCoordinator(
            _ => true,
            new ReconnectPolicy(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), new Random(1)),
            async token =>
            {
                automaticEntered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { automaticExited.TrySetResult(); }
            },
            delay: (_, _) => Task.CompletedTask);
        var running = automatic.StartAsync(new IOException(), default);
        await automaticEntered.Task;
        var transition = new ReconnectTransitionCoordinator(
            automatic,
            reservation,
            () => { events.Add("close"); return Task.CompletedTask; },
            _ => { events.Add("reconnect"); return Task.CompletedTask; });

        await Task.WhenAll(transition.RunAsync(default), transition.RunAsync(default));

        Assert.True(automaticExited.Task.IsCompleted);
        Assert.False(await running);
        Assert.True(reservation.Disposed);
        Assert.Equal(["close", "reconnect"], events);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => automatic.StartAsync(new IOException(), default));
    }

    private sealed class TrackingDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
