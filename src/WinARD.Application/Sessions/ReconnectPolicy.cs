namespace WinARD.Application.Sessions;

public sealed class ReconnectPolicy
{
    private const double MinimumJitterMultiplier = 0.8;
    private const double JitterRange = 0.4;
    private static readonly TimeSpan MaximumTimerDelay =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maximumDelay;
    private readonly Random _random;
    private readonly object _randomSync = new();

    public ReconnectPolicy(TimeSpan baseDelay, TimeSpan maximumDelay, Random random)
    {
        _baseDelay = ValidDelay(baseDelay, nameof(baseDelay));
        _maximumDelay = ValidDelay(maximumDelay, nameof(maximumDelay));
        if (_maximumDelay < _baseDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDelay),
                "Maximum delay cannot be less than the base delay.");
        }

        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public TimeSpan DelayForAttempt(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        double randomValue;
        lock (_randomSync)
        {
            randomValue = _random.NextDouble();
        }

        if (!double.IsFinite(randomValue) || randomValue is < 0 or >= 1)
        {
            throw new InvalidOperationException("The random source returned a value outside [0, 1).");
        }

        var exponentialTicks = _baseDelay.Ticks * Math.Pow(2, attempt);
        var jitteredTicks = exponentialTicks * (MinimumJitterMultiplier + (JitterRange * randomValue));
        if (double.IsPositiveInfinity(jitteredTicks) || jitteredTicks >= _maximumDelay.Ticks)
        {
            return _maximumDelay;
        }

        return TimeSpan.FromTicks(Math.Max(1, (long)jitteredTicks));
    }

    public Task WaitAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(DelayForAttempt(attempt), cancellationToken);

    private static TimeSpan ValidDelay(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan || value > MaximumTimerDelay)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Delay must be positive and no greater than {MaximumTimerDelay}.");
        }

        return value;
    }
}
