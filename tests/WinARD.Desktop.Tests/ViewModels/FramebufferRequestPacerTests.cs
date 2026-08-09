using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class FramebufferRequestPacerTests
{
    [Fact]
    public async Task Fixed_sixty_waits_for_remaining_frame_period()
    {
        var time = new ManualTimestampProvider();
        TimeSpan? waited = null;
        var pacer = new FramebufferRequestPacer(
            time,
            (delay, _) =>
            {
                waited = delay;
                return Task.CompletedTask;
            });
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(60), automaticFramesPerSecond: 60);
        pacer.MarkRequestStarted();
        time.Advance(TimeSpan.FromMilliseconds(5));

        await pacer.WaitForNextRequestAsync(CancellationToken.None);

        Assert.InRange(waited!.Value.TotalMilliseconds, 11.5, 12.0);
    }

    [Fact]
    public async Task Unlimited_does_not_delay()
    {
        var time = new ManualTimestampProvider();
        var delayed = false;
        var pacer = new FramebufferRequestPacer(
            time,
            (_, _) =>
            {
                delayed = true;
                return Task.CompletedTask;
            });
        pacer.SetPolicy(FrameRefreshPolicy.Unlimited, 60);
        pacer.MarkRequestStarted();
        time.Advance(TimeSpan.FromMilliseconds(1));

        await pacer.WaitForNextRequestAsync(CancellationToken.None);

        Assert.False(delayed);
    }

    [Fact]
    public async Task First_request_does_not_wait_before_a_request_is_marked()
    {
        var delayed = false;
        var pacer = new FramebufferRequestPacer(
            new ManualTimestampProvider(),
            (_, _) =>
            {
                delayed = true;
                return Task.CompletedTask;
            });
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(30), 60);

        await pacer.WaitForNextRequestAsync(CancellationToken.None);

        Assert.False(delayed);
    }

    [Fact]
    public async Task Exhausted_period_does_not_wait_or_compensate_for_missed_periods()
    {
        var time = new ManualTimestampProvider();
        var delays = new List<TimeSpan>();
        var pacer = new FramebufferRequestPacer(
            time,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(60), 60);
        pacer.MarkRequestStarted();
        time.Advance(TimeSpan.FromMilliseconds(40));

        await pacer.WaitForNextRequestAsync(CancellationToken.None);

        Assert.Empty(delays);

        pacer.MarkRequestStarted();
        time.Advance(TimeSpan.FromMilliseconds(5));
        await pacer.WaitForNextRequestAsync(CancellationToken.None);

        Assert.Single(delays);
        Assert.InRange(delays[0].TotalMilliseconds, 11.5, 12.0);
    }

    [Fact]
    public async Task Automatic_uses_the_supplied_frame_rate_tier()
    {
        var time = new ManualTimestampProvider();
        TimeSpan? waited = null;
        var pacer = new FramebufferRequestPacer(
            time,
            (delay, _) =>
            {
                waited = delay;
                return Task.CompletedTask;
            });
        pacer.SetPolicy(FrameRefreshPolicy.Automatic, automaticFramesPerSecond: 45);
        pacer.MarkRequestStarted();
        time.Advance(TimeSpan.FromMilliseconds(2));

        await pacer.WaitForNextRequestAsync(CancellationToken.None);

        Assert.InRange(waited!.Value.TotalMilliseconds, 20.0, 20.5);
    }

    [Fact]
    public async Task Fixed_rate_uses_non_tier_remote_maximum_as_effective_target()
    {
        var time = new ManualTimestampProvider();
        TimeSpan? waited = null;
        var pacer = new FramebufferRequestPacer(time, (delay, _) =>
        {
            waited = delay;
            return Task.CompletedTask;
        });
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(120), 60, remoteMaximum: 59);
        pacer.MarkRequestStarted();

        await pacer.WaitForNextRequestAsync(CancellationToken.None);

        Assert.InRange(waited!.Value.TotalMilliseconds, 16.9, 17.0);
    }

    [Fact]
    public async Task Elapsed_time_uses_the_time_provider_timestamp_frequency()
    {
        var time = new ManualTimestampProvider(timestampFrequency: 1_000);
        TimeSpan? waited = null;
        var pacer = new FramebufferRequestPacer(
            time,
            (delay, _) =>
            {
                waited = delay;
                return Task.CompletedTask;
            });
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(60), 0);
        pacer.MarkRequestStarted();
        time.Advance(TimeSpan.FromMilliseconds(5));

        await pacer.WaitForNextRequestAsync(CancellationToken.None);

        Assert.InRange(waited!.Value.TotalMilliseconds, 11.5, 12.0);
    }

    [Fact]
    public async Task Switching_to_unlimited_immediately_releases_the_next_request()
    {
        var time = new ManualTimestampProvider();
        var delayed = false;
        var pacer = new FramebufferRequestPacer(
            time,
            (_, _) =>
            {
                delayed = true;
                return Task.CompletedTask;
            });
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(30), 0);
        pacer.MarkRequestStarted();
        time.Advance(TimeSpan.FromMilliseconds(5));

        pacer.SetPolicy(FrameRefreshPolicy.Unlimited, 0);
        await pacer.WaitForNextRequestAsync(CancellationToken.None);

        Assert.False(delayed);
    }

    [Fact]
    public async Task Switching_to_unlimited_interrupts_an_already_started_limited_wait()
    {
        var time = new ManualTimestampProvider();
        var delayStarted = NewCompletion();
        var pacer = new FramebufferRequestPacer(
            time,
            async (_, token) =>
            {
                delayStarted.TrySetResult();
                await NewCompletion().Task.WaitAsync(token);
            });
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(30), 60);
        pacer.MarkRequestStarted();
        var wait = pacer.WaitForNextRequestAsync(CancellationToken.None);
        await delayStarted.Task;

        pacer.SetPolicy(FrameRefreshPolicy.Unlimited, 60);

        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Switching_to_higher_rate_recalculates_an_already_started_wait()
    {
        var time = new ManualTimestampProvider();
        var delays = new List<TimeSpan>();
        var firstStarted = NewCompletion();
        var secondStarted = NewCompletion();
        var pacer = new FramebufferRequestPacer(
            time,
            async (delay, token) =>
            {
                delays.Add(delay);
                (delays.Count == 1 ? firstStarted : secondStarted).TrySetResult();
                await NewCompletion().Task.WaitAsync(token);
            });
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(30), 60);
        pacer.MarkRequestStarted();
        var wait = pacer.WaitForNextRequestAsync(CancellationToken.None);
        await firstStarted.Task;

        pacer.SetPolicy(FrameRefreshPolicy.Fixed(120), 60);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.InRange(delays[0].TotalMilliseconds, 33.2, 33.4);
        Assert.InRange(delays[1].TotalMilliseconds, 8.2, 8.4);
        pacer.SetPolicy(FrameRefreshPolicy.Unlimited, 60);
        await wait;
    }

    [Fact]
    public async Task Completed_old_delay_rechecks_policy_generation_before_releasing_request()
    {
        var time = new ManualTimestampProvider();
        var delays = new List<TimeSpan>();
        var firstDelay = NewCompletion();
        var secondStarted = NewCompletion();
        var pacer = new FramebufferRequestPacer(time, (delay, _) =>
        {
            delays.Add(delay);
            if (delays.Count == 1)
            {
                return firstDelay.Task;
            }

            secondStarted.TrySetResult();
            return Task.CompletedTask;
        });
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(120), 60);
        pacer.MarkRequestStarted();
        var wait = pacer.WaitForNextRequestAsync(CancellationToken.None);

        firstDelay.TrySetResult();
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(30), 60);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await wait;

        Assert.InRange(delays[0].TotalMilliseconds, 8.2, 8.4);
        Assert.InRange(delays[1].TotalMilliseconds, 33.2, 33.4);
    }

    [Fact]
    public async Task Switching_limited_policies_uses_the_last_actual_request_start()
    {
        var time = new ManualTimestampProvider();
        var delays = new List<TimeSpan>();
        var pacer = new FramebufferRequestPacer(
            time,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        pacer.SetPolicy(FrameRefreshPolicy.Unlimited, 0);
        pacer.MarkRequestStarted();
        time.Advance(TimeSpan.FromMilliseconds(5));

        pacer.SetPolicy(FrameRefreshPolicy.Automatic, 45);
        await pacer.WaitForNextRequestAsync(CancellationToken.None);
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(30), 0);
        await pacer.WaitForNextRequestAsync(CancellationToken.None);

        Assert.Collection(
            delays,
            automatic => Assert.InRange(automatic.TotalMilliseconds, 17.0, 17.5),
            fixedRate => Assert.InRange(fixedRate.TotalMilliseconds, 28.0, 28.5));
    }

    [Fact]
    public async Task Wait_passes_the_cancellation_token_to_the_delay()
    {
        var time = new ManualTimestampProvider();
        using var cancellation = new CancellationTokenSource();
        CancellationToken received = default;
        var pacer = new FramebufferRequestPacer(
            time,
            (_, token) =>
            {
                received = token;
                return Task.CompletedTask;
            });
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(60), 60);
        pacer.MarkRequestStarted();

        await pacer.WaitForNextRequestAsync(cancellation.Token);

        Assert.True(received.CanBeCanceled);
    }

    [Fact]
    public async Task Concurrent_mark_waits_until_the_policy_snapshot_is_complete()
    {
        using var time = new BlockingTimestampProvider();
        var pacer = new FramebufferRequestPacer(time, (_, _) => Task.CompletedTask);
        pacer.SetPolicy(FrameRefreshPolicy.Fixed(60), automaticFramesPerSecond: 60);
        pacer.MarkRequestStarted();
        time.BlockNextTimestamp();

        var wait = Task.Run(() => pacer.WaitForNextRequestAsync(CancellationToken.None));
        await time.TimestampEntered.Task;
        var markStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mark = Task.Run(() =>
        {
            markStarted.TrySetResult();
            pacer.MarkRequestStarted();
        });
        await markStarted.Task;
        for (var index = 0; index < 10; index++)
        {
            await Task.Yield();
        }

        var wasSerialized = !mark.IsCompleted;
        time.ReleaseTimestamp();
        await wait;
        await AssertNoFailureAsync(mark);
        Assert.True(wasSerialized);
    }

    private static async Task AssertNoFailureAsync(Task operation) => await operation;

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_non_positive_automatic_frame_rate(int framesPerSecond)
    {
        var pacer = new FramebufferRequestPacer(new ManualTimestampProvider());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => pacer.SetPolicy(FrameRefreshPolicy.Automatic, framesPerSecond));
    }

    [Fact]
    public void Fixed_ignores_a_non_positive_automatic_frame_rate()
    {
        var pacer = new FramebufferRequestPacer(new ManualTimestampProvider());

        pacer.SetPolicy(FrameRefreshPolicy.Fixed(60), automaticFramesPerSecond: 0);
    }

    [Fact]
    public void Unlimited_ignores_a_non_positive_automatic_frame_rate()
    {
        var pacer = new FramebufferRequestPacer(new ManualTimestampProvider());

        pacer.SetPolicy(FrameRefreshPolicy.Unlimited, automaticFramesPerSecond: -1);
    }

    private sealed class ManualTimestampProvider : TimeProvider
    {
        private readonly long _timestampFrequency;
        private long _timestamp;

        public ManualTimestampProvider(long timestampFrequency = TimeSpan.TicksPerSecond)
        {
            _timestampFrequency = timestampFrequency;
        }

        public override long TimestampFrequency => _timestampFrequency;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan value) =>
            _timestamp += checked((long)Math.Round(value.TotalSeconds * TimestampFrequency));
    }

    private sealed class BlockingTimestampProvider : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _activeTimestampCall;
        private int _blockNext;

        public TaskCompletionSource TimestampEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp()
        {
            if (Interlocked.Exchange(ref _activeTimestampCall, 1) != 0)
            {
                throw new InvalidOperationException("Timestamp provider was called concurrently.");
            }

            try
            {
                if (Interlocked.Exchange(ref _blockNext, 0) != 0)
                {
                    TimestampEntered.TrySetResult();
                    _release.Wait();
                }

                return 0;
            }
            finally
            {
                Volatile.Write(ref _activeTimestampCall, 0);
            }
        }

        public void BlockNextTimestamp() => Volatile.Write(ref _blockNext, 1);

        public void ReleaseTimestamp() => _release.Set();

        public void Dispose() => _release.Dispose();
    }
}
