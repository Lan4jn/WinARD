using WinARD.Desktop.Input;
using WinARD.Desktop.Threading;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests;

public sealed class RemoteInputOperationRunnerTests
{
    [Fact]
    public async Task Input_failure_is_observed_reported_and_stops_failed_session()
    {
        var reports = 0;
        var stops = 0;
        var dispatcher = new TrackingDispatcher();
        var runner = new RemoteInputOperationRunner(
            dispatcher,
            () => { reports++; return Task.CompletedTask; },
            () =>
            {
                Assert.True(dispatcher.IsDispatching);
                stops++;
                return Task.CompletedTask;
            },
            () => false);

        await runner.RunAsync(() => Task.FromException(new IOException("sensitive input failure")));

        Assert.Equal(1, reports);
        Assert.Equal(1, stops);
    }

    [Fact]
    public void Cursor_visibility_controller_hides_host_cursor_only_while_remote_cursor_is_visible()
    {
        var hostCursorStates = new List<bool>();
        var overlayStates = new List<bool>();
        var controller = new RemoteCursorVisibilityController(
            hidden => hostCursorStates.Add(hidden),
            visible => overlayStates.Add(visible));

        controller.SetRemoteCursorVisible(true);
        controller.SetRemoteCursorVisible(false);
        controller.Reset();

        Assert.Equal([true, false, false], hostCursorStates);
        Assert.Equal([true, false, false], overlayStates);
    }

    [Fact]
    public async Task Pointer_dispatch_updates_local_overlay_before_waiting_for_blocked_sender()
    {
        var senderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var localUpdated = false;
        var runner = new RemoteInputOperationRunner(
            new TrackingDispatcher(),
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => false);

        var pending = RemotePointerDispatch.RunAsync(
            () => localUpdated = true,
            runner,
            async () =>
            {
                Assert.True(localUpdated);
                senderStarted.TrySetResult();
                await releaseSender.Task;
            });

        Assert.True(localUpdated);
        await senderStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(pending.IsCompleted);
        releaseSender.TrySetResult();
        await pending;
    }

    private sealed class TrackingDispatcher : IUiDispatcher
    {
        public bool IsDispatching { get; private set; }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsDispatching = true;
            try
            {
                action();
            }
            finally
            {
                IsDispatching = false;
            }

            return Task.CompletedTask;
        }
    }
}
