namespace WinARD.Desktop.ViewModels;

internal readonly record struct FrameRateLoadSample(
    TimeSpan Response,
    TimeSpan Presentation,
    double ChangedAreaRatio,
    TimeSpan InputWriteLatency);

internal sealed class AutomaticFrameRateController
{
    private const double NewSampleWeight = 0.25;
    private static readonly int[] AllFrameRateTiers = [30, 45, 60, 75, 90, 105, 120];
    private static readonly TimeSpan MinimumDowngradeInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumUpgradeStability = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider;
    private readonly int[] _availableTiers;
    private int _currentTierIndex;
    private EmaState? _ema;
    private int _consecutiveBadSamples;
    private int _consecutiveGoodSamples;
    private long? _goodStreakStarted;
    private long? _lastDowngrade;

    public AutomaticFrameRateController(TimeProvider timeProvider, int? remoteMaximum)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (remoteMaximum is < 30)
        {
            throw new ArgumentOutOfRangeException(nameof(remoteMaximum));
        }

        _availableTiers = remoteMaximum is { } maximum
            ? AllFrameRateTiers.Where(tier => tier <= maximum).ToArray()
            : [.. AllFrameRateTiers];
        _currentTierIndex = Array.FindLastIndex(_availableTiers, tier => tier <= 60);
    }

    public int CurrentFramesPerSecond => _availableTiers[_currentTierIndex];

    public bool Observe(FrameRateLoadSample sample)
    {
        Validate(sample);
        _ema = _ema is { } previous
            ? previous.Update(sample)
            : EmaState.From(sample);

        var current = _ema.Value;
        var framePeriodMilliseconds = 1000d / CurrentFramesPerSecond;
        if (IsBad(current, framePeriodMilliseconds))
        {
            _consecutiveGoodSamples = 0;
            _goodStreakStarted = null;
            _consecutiveBadSamples++;
            return TryDowngrade();
        }

        if (IsGood(current, framePeriodMilliseconds))
        {
            _consecutiveBadSamples = 0;
            _goodStreakStarted ??= _timeProvider.GetTimestamp();
            _consecutiveGoodSamples++;
            return TryUpgrade();
        }

        ResetStreaks();
        return false;
    }

    private bool TryDowngrade()
    {
        if (_consecutiveBadSamples < 3 || _currentTierIndex == 0)
        {
            return false;
        }

        var now = _timeProvider.GetTimestamp();
        if (_lastDowngrade is { } previous &&
            _timeProvider.GetElapsedTime(previous, now) < MinimumDowngradeInterval)
        {
            return false;
        }

        _currentTierIndex--;
        _lastDowngrade = now;
        ResetStreaks();
        return true;
    }

    private bool TryUpgrade()
    {
        if (_consecutiveGoodSamples < 30 ||
            _currentTierIndex == _availableTiers.Length - 1 ||
            _goodStreakStarted is not { } started ||
            _timeProvider.GetElapsedTime(started, _timeProvider.GetTimestamp()) < MinimumUpgradeStability)
        {
            return false;
        }

        _currentTierIndex++;
        ResetStreaks();
        return true;
    }

    private void ResetStreaks()
    {
        _consecutiveBadSamples = 0;
        _consecutiveGoodSamples = 0;
        _goodStreakStarted = null;
    }

    private static bool IsBad(EmaState sample, double framePeriodMilliseconds) =>
        sample.InputWriteLatencyMilliseconds >= 50 ||
        sample.PresentationMilliseconds > framePeriodMilliseconds * 0.75 ||
        (sample.ChangedAreaRatio >= 0.50 &&
         sample.ResponseMilliseconds > framePeriodMilliseconds * 1.25);

    private static bool IsGood(EmaState sample, double framePeriodMilliseconds) =>
        sample.InputWriteLatencyMilliseconds < 20 &&
        sample.PresentationMilliseconds <= framePeriodMilliseconds * 0.50 &&
        sample.ResponseMilliseconds <= framePeriodMilliseconds * 0.75;

    private static void Validate(FrameRateLoadSample sample)
    {
        if (sample.Response < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), "Response cannot be negative.");
        }

        if (sample.Presentation < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), "Presentation cannot be negative.");
        }

        if (sample.InputWriteLatency < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), "Input write latency cannot be negative.");
        }

        if (!double.IsFinite(sample.ChangedAreaRatio) ||
            sample.ChangedAreaRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), "Changed area ratio must be between zero and one.");
        }
    }

    private readonly record struct EmaState(
        double ResponseMilliseconds,
        double PresentationMilliseconds,
        double ChangedAreaRatio,
        double InputWriteLatencyMilliseconds)
    {
        public static EmaState From(FrameRateLoadSample sample) => new(
            sample.Response.TotalMilliseconds,
            sample.Presentation.TotalMilliseconds,
            sample.ChangedAreaRatio,
            sample.InputWriteLatency.TotalMilliseconds);

        public EmaState Update(FrameRateLoadSample sample) => new(
            Average(ResponseMilliseconds, sample.Response.TotalMilliseconds),
            Average(PresentationMilliseconds, sample.Presentation.TotalMilliseconds),
            Average(ChangedAreaRatio, sample.ChangedAreaRatio),
            Average(InputWriteLatencyMilliseconds, sample.InputWriteLatency.TotalMilliseconds));

        private static double Average(double previous, double current) =>
            NewSampleWeight * current + (1 - NewSampleWeight) * previous;
    }
}
