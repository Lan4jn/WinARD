using WinARD.Domain.Connections;

namespace WinARD.Desktop.ViewModels;

internal sealed class FramebufferRequestPacer
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private TaskCompletionSource _policyChanged = NewSignal();
    private int? _targetFramesPerSecond;
    private long? _lastRequestStarted;

    public FramebufferRequestPacer(
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _delay = delay ?? ((value, token) => Task.Delay(value, _timeProvider, token));
    }

    public void SetPolicy(
        FrameRefreshPolicy policy,
        int automaticFramesPerSecond,
        int? remoteMaximum = null)
    {
        int? targetFramesPerSecond = policy.Mode switch
        {
            FrameRefreshMode.Automatic => ValidateAutomaticFramesPerSecond(automaticFramesPerSecond),
            FrameRefreshMode.Fixed => ApplyMaximum(
                policy.FixedFramesPerSecond
                    ?? throw new ArgumentException("A fixed policy must specify a frame rate.", nameof(policy)),
                remoteMaximum),
            FrameRefreshMode.Unlimited => null,
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
        TaskCompletionSource changed;
        lock (_sync)
        {
            _targetFramesPerSecond = targetFramesPerSecond;
            changed = _policyChanged;
            _policyChanged = NewSignal();
        }

        changed.TrySetResult();
    }

    private static int ValidateAutomaticFramesPerSecond(int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        return framesPerSecond;
    }

    public void MarkRequestStarted()
    {
        lock (_sync)
        {
            _lastRequestStarted = _timeProvider.GetTimestamp();
        }
    }

    internal bool HasRequestBaseline
    {
        get
        {
            lock (_sync)
            {
                return _lastRequestStarted is not null;
            }
        }
    }

    public async Task WaitForNextRequestAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int? framesPerSecond;
            long? lastRequestStarted;
            long now;
            Task policyChanged;
            lock (_sync)
            {
                framesPerSecond = _targetFramesPerSecond;
                lastRequestStarted = _lastRequestStarted;
                if (framesPerSecond is null || lastRequestStarted is null)
                {
                    return;
                }

                now = _timeProvider.GetTimestamp();
                policyChanged = _policyChanged.Task;
            }

            var period = TimeSpan.FromSeconds(1d / framesPerSecond.Value);
            var elapsed = _timeProvider.GetElapsedTime(lastRequestStarted.Value, now);
            var remaining = period - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = _delay(remaining, linked.Token);
            if (await Task.WhenAny(delay, policyChanged).ConfigureAwait(false) == delay)
            {
                await delay.ConfigureAwait(false);
                lock (_sync)
                {
                    if (ReferenceEquals(policyChanged, _policyChanged.Task))
                    {
                        return;
                    }
                }

                continue;
            }

            linked.Cancel();
            try
            {
                await delay.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private static int ApplyMaximum(int framesPerSecond, int? remoteMaximum) =>
        remoteMaximum is { } maximum ? Math.Min(framesPerSecond, maximum) : framesPerSecond;

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
