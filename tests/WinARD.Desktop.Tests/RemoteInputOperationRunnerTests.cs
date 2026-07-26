using WinARD.Desktop.Input;
using WinARD.Desktop.Threading;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests;

public sealed class RemoteInputOperationRunnerTests
{
    [Fact]
    public async Task Input_failure_is_observed_reported_and_closes_session()
    {
        var reports = 0;
        var closes = 0;
        var dispatcher = new TrackingDispatcher();
        var runner = new RemoteInputOperationRunner(
            dispatcher,
            () => { reports++; return Task.CompletedTask; },
            () =>
            {
                Assert.True(dispatcher.IsDispatching);
                closes++;
                return Task.CompletedTask;
            },
            () => false);

        await runner.RunAsync(() => Task.FromException(new IOException("sensitive input failure")));

        Assert.Equal(1, reports);
        Assert.Equal(1, closes);
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
