using WinARD.Application.Sessions;
using WinARD.Domain.Errors;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Application.Tests;

public sealed class AutomaticReconnectCoordinatorTests
{
    [Theory]
    [InlineData("REMOTE_SESSION_INTERRUPTED", true)]
    [InlineData("TCP_CONNECTION_FAILED", true)]
    [InlineData("TRANSPORT_TIMEOUT", true)]
    [InlineData("ARD_AUTH_REJECTED", false)]
    [InlineData("ARD_CONTROL_NOT_ALLOWED", false)]
    [InlineData("RFB_PROTOCOL_ERROR", false)]
    [InlineData("SSH_HOST_KEY_CHANGED", false)]
    public void Classifier_only_retries_allowlisted_transient_failures(string code, bool expected)
    {
        var failure = new ReconnectFailureException(WinArdError.Create(
            ConnectionStage.Connected, code, "safe", "correlation"));

        Assert.Equal(expected, ReconnectFailureClassifier.IsTransient(failure));
    }

    [Fact]
    public void Classifier_retries_io_but_not_protocol_or_user_cancellation()
    {
        Assert.True(ReconnectFailureClassifier.IsTransient(new IOException()));
        Assert.False(ReconnectFailureClassifier.IsTransient(new InvalidDataException()));
        Assert.False(ReconnectFailureClassifier.IsTransient(new OperationCanceledException()));
    }

    [Fact]
    public async Task Transient_failure_counts_down_and_retries_with_a_fresh_connection()
    {
        var attempts = 0;
        var progress = new List<AutomaticReconnectProgress>();
        var coordinator = Create(
            transient: _ => true,
            connect: _ =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException(new IOException("eof"))
                    : Task.CompletedTask;
            },
            progress.Add);

        Assert.True(await coordinator.StartAsync(new IOException("eof"), default));

        Assert.Equal(2, attempts);
        Assert.Contains(progress, item => item.Attempt == 1 && item.Remaining > TimeSpan.Zero);
        Assert.Contains(progress, item => item.Attempt == 2);
    }

    [Theory]
    [InlineData("AUTH")]
    [InlineData("PERMISSION")]
    [InlineData("PROTOCOL")]
    [InlineData("HOST_KEY")]
    public async Task Deterministic_failure_does_not_retry(string code)
    {
        var attempts = 0;
        var coordinator = Create(
            transient: exception => exception.Message == "TRANSIENT",
            connect: _ => { attempts++; return Task.CompletedTask; });

        Assert.False(await coordinator.StartAsync(new InvalidOperationException(code), default));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Cancel_stops_countdown_without_a_timer_or_connect_attempt()
    {
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var coordinator = Create(_ => true, _ => { attempts++; return Task.CompletedTask; },
            delay: (_, token) => delay.Task.WaitAsync(token));

        var reconnect = coordinator.StartAsync(new IOException(), default);
        coordinator.Cancel();

        Assert.False(await reconnect);
        Assert.Equal(0, attempts);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task Concurrent_start_calls_share_one_loop_and_one_connect_gate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var coordinator = Create(_ => true, async token =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            entered.SetResult();
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref active);
        });

        var first = coordinator.StartAsync(new IOException(), default);
        var second = coordinator.StartAsync(new IOException(), default);
        await entered.Task;
        release.SetResult();

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task Explicit_reconnect_cancels_countdown_and_uses_the_same_connect_gate()
    {
        var countdownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var coordinator = Create(
            _ => true,
            _ => { attempts++; return Task.CompletedTask; },
            delay: (_, token) =>
            {
                countdownEntered.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        var automatic = coordinator.StartAsync(new IOException(), default);
        await countdownEntered.Task;
        await coordinator.ReconnectNowAsync(default);

        Assert.False(await automatic);
        Assert.Equal(1, attempts);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task A_deterministic_attempt_failure_stops_the_loop()
    {
        var attempts = 0;
        var coordinator = Create(
            exception => exception is IOException,
            _ =>
            {
                attempts++;
                return Task.FromException(new UnauthorizedAccessException());
            });

        Assert.False(await coordinator.StartAsync(new IOException(), default));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Concurrent_disposal_is_idempotent_and_leaves_no_loop()
    {
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Create(_ => true, _ => Task.CompletedTask,
            delay: (_, token) =>
            {
                delayEntered.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        var reconnect = coordinator.StartAsync(new IOException(), default);
        await delayEntered.Task;

        await Task.WhenAll(
            coordinator.DisposeAsync().AsTask(),
            coordinator.DisposeAsync().AsTask());

        Assert.False(await reconnect);
        Assert.False(coordinator.IsRunning);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.StartAsync(new IOException(), default));
    }

    private static AutomaticReconnectCoordinator Create(
        Func<Exception, bool> transient,
        Func<CancellationToken, Task> connect,
        Action<AutomaticReconnectProgress>? progress = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) => new(
            transient,
            new ReconnectPolicy(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40), new FixedRandom()),
            connect,
            progress,
            delay ?? ((_, _) => Task.CompletedTask),
            TimeSpan.FromMilliseconds(5));

    private sealed class FixedRandom : Random
    {
        public override double NextDouble() => 0.5;
    }
}
