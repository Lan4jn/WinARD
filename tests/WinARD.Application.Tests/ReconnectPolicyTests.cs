using WinARD.Application.Sessions;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Application.Tests;

public sealed class ReconnectPolicyTests
{
    [Fact]
    public void DelayForAttempt_caps_exponential_backoff_at_maximum()
    {
        var policy = new ReconnectPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            new FixedRandom(0.5));

        var delay = policy.DelayForAttempt(10);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void DelayForAttempt_uses_base_delay_for_first_attempt_with_centered_jitter()
    {
        var policy = new ReconnectPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            new FixedRandom(0.5));

        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayForAttempt(0));
    }

    [Fact]
    public void DelayForAttempt_handles_maximum_attempt_without_overflow()
    {
        var policy = new ReconnectPolicy(
            TimeSpan.FromTicks(1),
            TimeSpan.FromSeconds(30),
            new FixedRandom(0.5));

        Assert.Equal(TimeSpan.FromSeconds(30), policy.DelayForAttempt(int.MaxValue));
    }

    [Fact]
    public void DelayForAttempt_rejects_negative_attempt()
    {
        var policy = new ReconnectPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            new FixedRandom(0.5));

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayForAttempt(-1));
    }

    [Fact]
    public void Constructor_rejects_invalid_delays_and_dependencies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            new FixedRandom(0.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1),
            new FixedRandom(0.5)));
        Assert.Throws<ArgumentNullException>(() => new ReconnectPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromDays(50),
            new FixedRandom(0.5)));
    }

    [Fact]
    public void DelayForAttempt_rejects_non_finite_random_value()
    {
        var policy = new ReconnectPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            new FixedRandom(double.NaN));

        Assert.Throws<InvalidOperationException>(() => policy.DelayForAttempt(0));
    }

    [Fact]
    public async Task WaitAsync_is_cancellable_during_backoff()
    {
        var policy = new ReconnectPolicy(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            new FixedRandom(0.5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => policy.WaitAsync(0, cancellation.Token));
    }

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}
