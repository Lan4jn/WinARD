using WinARD.Domain.Connections;
using System.Collections.ObjectModel;

namespace WinARD.Application.Quality;

/// <summary>
/// Pure, connection-scoped controller. Its only clock is the timestamp carried by each observation.
/// </summary>
public sealed class AdaptiveQualityController
{
    private static readonly TimeSpan InputRecentThreshold = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan BandwidthWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DegradeCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UpgradeCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StableUpgradeDuration = TimeSpan.FromSeconds(15);
    private const int SeverePendingInputCount = 3;

    private static readonly (QualityLevel Level, QualityColor Color, QualityScale Scale, int FramesPerSecond)[]
        FixedQualityTable =
        [
            (QualityLevel.Q0, QualityColor.Full32, QualityScale.Native, 60),
            (QualityLevel.Q1, QualityColor.Color16, QualityScale.Native, 60),
            (QualityLevel.Q2, QualityColor.Color16, QualityScale.Percent75, 60),
            (QualityLevel.Q3, QualityColor.Color16, QualityScale.Percent50, 45),
            (QualityLevel.Q4, QualityColor.Grayscale, QualityScale.Percent50, 30),
        ];

    private readonly QualityProfile profile;
    private readonly ArdDisplayCapabilities capabilities;
    private readonly ReadOnlyCollection<QualityLevel> availableLevels;
    private DateTimeOffset? lastTimestamp;
    private DateTimeOffset? nextLevelChangeAllowedAt;
    private DateTimeOffset? lastCountedOverTargetWindow;
    private DateTimeOffset? stableUnderTargetSince;
    private DateTimeOffset? contentStableSince;
    private int consecutiveOverTargetWindows;
    private int consecutiveHighDirtySamples;
    private QualityContentState? state;
    private long generation;

    public AdaptiveQualityController(
        QualityProfile profile,
        ArdDisplayCapabilities capabilities,
        QualityDecoderGates? decoderGates = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capabilities);
        this.profile = profile;
        this.capabilities = capabilities;
        decoderGates ??= new QualityDecoderGates();
        availableLevels = BuildAvailableLevels(profile, capabilities, decoderGates);
        CurrentLevel = SelectInitialLevel(profile, availableLevels);
    }

    public static IReadOnlyList<(QualityLevel Level, QualityColor Color, QualityScale Scale, int FramesPerSecond)>
        QualityTable { get; } = Array.AsReadOnly(FixedQualityTable);

    public QualityLevel CurrentLevel { get; private set; }

    public QualityDecision Observe(QualityObservation observation) => Decide(observation);

    public QualityDecision Decide(QualityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (lastTimestamp is { } priorTimestamp && observation.Timestamp < priorTimestamp)
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "Observation timestamps cannot move backwards.");
        }

        var isInitial = lastTimestamp is null;
        lastTimestamp = observation.Timestamp;
        var previousLevel = CurrentLevel;
        var previousState = state;
        var nextState = DetermineState(observation);
        var reason = isInitial
            ? HasUserConstraints(profile) ? QualityDecisionReason.UserConstraint : QualityDecisionReason.Initial
            : nextState == QualityContentState.Motion && previousState != QualityContentState.Motion
                ? QualityDecisionReason.MotionDetected
                : QualityDecisionReason.Initial;

        var target = profile.TargetBytesPerSecond;
        var bandwidthAdaptationEnabled = target is not null &&
            profile.Preset != QualityPreset.Original &&
            !(profile.Preset == QualityPreset.Custom && profile.BandwidthLocked);
        var targetSatisfied = target is null ||
            observation.AverageBytesPerSecond5Seconds <= target.Value &&
            observation.PeakBytesPerSecond5Seconds < target.Value * 1.5;

        if (bandwidthAdaptationEnabled)
        {
            var severe = IsSeverelyOverTarget(observation, target!.Value, TargetFramesPerSecond(nextState, CurrentLevel));
            if (severe && CanDegrade(observation.Timestamp))
            {
                if (MoveOneLevel(down: true))
                {
                    MarkLevelChange(observation.Timestamp, down: true);
                    reason = QualityDecisionReason.SevereOverTarget;
                }
            }
            else if (!severe && IsNewBandwidthWindow(observation.Timestamp))
            {
                if (observation.AverageBytesPerSecond5Seconds > target.Value * 1.10)
                {
                    consecutiveOverTargetWindows++;
                    stableUnderTargetSince = null;
                    if (consecutiveOverTargetWindows >= 2 && CanDegrade(observation.Timestamp) && MoveOneLevel(down: true))
                    {
                        MarkLevelChange(observation.Timestamp, down: true);
                        consecutiveOverTargetWindows = 0;
                        reason = QualityDecisionReason.SustainedOverTarget;
                    }
                }
                else
                {
                    consecutiveOverTargetWindows = 0;
                }
            }

            if (IsStableUnderTarget(observation, target.Value))
            {
                stableUnderTargetSince ??= observation.Timestamp;
                if (observation.Timestamp - stableUnderTargetSince >= StableUpgradeDuration &&
                    CanUpgrade(observation.Timestamp) &&
                    MoveOneLevel(down: false))
                {
                    MarkLevelChange(observation.Timestamp, down: false);
                    stableUnderTargetSince = observation.Timestamp;
                    reason = QualityDecisionReason.StableRecovery;
                }
            }
            else
            {
                stableUnderTargetSince = null;
            }
        }

        var cannotMeetTarget = target is not null &&
            (observation.AverageBytesPerSecond5Seconds > target.Value ||
             observation.PeakBytesPerSecond5Seconds >= target.Value * 1.5) &&
            !CanMove(down: true);
        if (cannotMeetTarget)
        {
            targetSatisfied = false;
            reason = QualityDecisionReason.TargetUnsatisfied;
        }
        else if (!isInitial && previousLevel == CurrentLevel && reason == QualityDecisionReason.Initial &&
            availableLevels.Count < FixedQualityTable.Length)
        {
            reason = HasUserConstraints(profile)
                ? QualityDecisionReason.UserConstraint
                : QualityDecisionReason.CapabilityLimited;
        }

        state = nextState;
        var specification = FixedQualityTable[(int)CurrentLevel];
        var targetFramesPerSecond = TargetFramesPerSecond(nextState, CurrentLevel);
        if (cannotMeetTarget && !HasLockedFixedRefresh(profile))
        {
            targetFramesPerSecond = Math.Min(targetFramesPerSecond, 30);
        }

        return new QualityDecision(
            ++generation,
            nextState,
            CurrentLevel,
            specification.Color,
            specification.Scale,
            targetFramesPerSecond,
            reason,
            targetSatisfied,
            previousLevel != CurrentLevel,
            previousState != nextState,
            isInitial ? null : previousLevel,
            previousState);
    }

    private QualityContentState DetermineState(QualityObservation observation)
    {
        if (observation.IsDragging || observation.IsScrolling)
        {
            consecutiveHighDirtySamples = 0;
            contentStableSince = null;
            return QualityContentState.Motion;
        }

        if (observation.DirtyCoverage >= .25)
        {
            consecutiveHighDirtySamples++;
            if (consecutiveHighDirtySamples >= 2)
            {
                contentStableSince = null;
                return QualityContentState.Motion;
            }
        }
        else
        {
            consecutiveHighDirtySamples = 0;
        }

        if (observation.DirtyCoverage >= .02)
        {
            contentStableSince = null;
            return QualityContentState.Interactive;
        }

        contentStableSince ??= observation.Timestamp;
        if (observation.SinceLastInput < InputRecentThreshold)
        {
            return QualityContentState.Interactive;
        }

        if (state is QualityContentState.Motion or QualityContentState.Interactive)
        {
            return observation.Timestamp - contentStableSince.Value >= InputRecentThreshold
                ? QualityContentState.Recovery
                : state.Value;
        }

        return QualityContentState.Idle;
    }

    private bool IsNewBandwidthWindow(DateTimeOffset timestamp)
    {
        if (lastCountedOverTargetWindow is { } previous && timestamp - previous < BandwidthWindow)
        {
            return false;
        }

        lastCountedOverTargetWindow = timestamp;
        return true;
    }

    private static bool IsSeverelyOverTarget(
        QualityObservation observation,
        long target,
        int targetFramesPerSecond)
    {
        var framePeriod = TimeSpan.FromSeconds(1d / targetFramesPerSecond);
        return observation.AverageBytesPerSecond5Seconds >= target * 1.5 ||
            observation.PeakBytesPerSecond5Seconds >= target * 1.5 ||
            observation.ResponseTime >= framePeriod * 2 ||
            observation.PendingInputCount >= SeverePendingInputCount;
    }

    private static bool IsStableUnderTarget(QualityObservation observation, long target) =>
        observation.AverageBytesPerSecond5Seconds < target * .75 &&
        observation.PeakBytesPerSecond5Seconds < target &&
        observation.PendingInputCount == 0 &&
        observation.ResponseTime < TimeSpan.FromMilliseconds(50);

    private bool CanDegrade(DateTimeOffset timestamp) =>
        nextLevelChangeAllowedAt is null || timestamp >= nextLevelChangeAllowedAt;

    private bool CanUpgrade(DateTimeOffset timestamp) =>
        nextLevelChangeAllowedAt is null || timestamp >= nextLevelChangeAllowedAt;

    private bool MoveOneLevel(bool down)
    {
        var currentIndex = CurrentAvailableIndex();
        var nextIndex = currentIndex + (down ? 1 : -1);
        if (nextIndex < 0 || nextIndex >= availableLevels.Count)
        {
            return false;
        }


        if (Math.Abs((int)availableLevels[nextIndex] - (int)CurrentLevel) != 1)
        {
            return false;
        }

        CurrentLevel = availableLevels[nextIndex];
        return true;
    }

    private bool CanMove(bool down)
    {
        var currentIndex = CurrentAvailableIndex();
        var nextIndex = currentIndex + (down ? 1 : -1);
        return nextIndex >= 0 &&
            nextIndex < availableLevels.Count &&
            Math.Abs((int)availableLevels[nextIndex] - (int)CurrentLevel) == 1;
    }

    private int CurrentAvailableIndex()
    {
        for (var index = 0; index < availableLevels.Count; index++)
        {
            if (availableLevels[index] == CurrentLevel)
            {
                return index;
            }
        }

        throw new InvalidOperationException("The current quality level is not available.");
    }

    private void MarkLevelChange(DateTimeOffset timestamp, bool down)
    {
        nextLevelChangeAllowedAt = timestamp + (down ? DegradeCooldown : UpgradeCooldown);
        consecutiveOverTargetWindows = 0;
    }

    private int TargetFramesPerSecond(QualityContentState contentState, QualityLevel level)
    {
        if (HasLockedFixedRefresh(profile))
        {
            var lockedResult = Math.Min(
                profile.Refresh.FixedFramesPerSecond!.Value,
                FixedQualityTable[(int)level].FramesPerSecond);
            return capabilities.MaximumRefreshRate is { } lockedRemoteMaximum
                ? Math.Min(lockedResult, lockedRemoteMaximum)
                : lockedResult;
        }

        var levelFrames = FixedQualityTable[(int)level].FramesPerSecond;
        var stateMaximum = contentState switch
        {
            QualityContentState.Motion => 60,
            QualityContentState.Interactive => levelFrames >= 45 ? 45 : 30,
            QualityContentState.Recovery => 45,
            _ => 30,
        };
        var result = Math.Min(levelFrames, stateMaximum);
        if (profile.Refresh.Mode == FrameRefreshMode.Fixed)
        {
            result = Math.Min(result, profile.Refresh.FixedFramesPerSecond!.Value);
        }

        if (capabilities.MaximumRefreshRate is { } remoteMaximum)
        {
            result = Math.Min(result, remoteMaximum);
        }

        if (contentState == QualityContentState.Interactive && result is > 30 and < 45)
        {
            result = 30;
        }

        return Math.Max(1, result);
    }

    private static ReadOnlyCollection<QualityLevel> BuildAvailableLevels(
        QualityProfile profile,
        ArdDisplayCapabilities capabilities,
        QualityDecoderGates gates)
    {
        var result = new List<QualityLevel>();
        foreach (var candidate in FixedQualityTable)
        {
            if (profile.Preset == QualityPreset.Original && candidate.Level != QualityLevel.Q0 ||
                profile.Preset == QualityPreset.Balanced && candidate.Level > QualityLevel.Q2 ||
                profile.Preset == QualityPreset.Smooth && candidate.Level > QualityLevel.Q3)
            {
                continue;
            }

            if (candidate.Level is QualityLevel.Q1 or QualityLevel.Q2 or QualityLevel.Q3 &&
                !capabilities.Rgb565.IsObserved())
            {
                continue;
            }

            if (candidate.Level is QualityLevel.Q2 or QualityLevel.Q3 or QualityLevel.Q4 &&
                !capabilities.ServerScaling.IsObserved())
            {
                continue;
            }

            if (candidate.Level == QualityLevel.Q4 &&
                (!capabilities.AppleGrayscale1001.IsObserved() ||
                 !gates.AppleGrayscale1001Approved ||
                 !profile.AllowAutomaticGrayscale))
            {
                continue;
            }

            if (profile.Preset == QualityPreset.Custom &&
                (profile.ColorLocked && profile.Color != candidate.Color ||
                 profile.ScaleLocked && profile.Scale != candidate.Scale))
            {
                continue;
            }

            if (HasLockedFixedRefresh(profile) &&
                candidate.FramesPerSecond < Math.Min(profile.Refresh.FixedFramesPerSecond!.Value, 60))
            {
                continue;
            }

            result.Add(candidate.Level);
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("The profile locks and observed capabilities leave no safe quality level.", nameof(profile));
        }

        return result.AsReadOnly();
    }

    private static QualityLevel SelectInitialLevel(QualityProfile profile, ReadOnlyCollection<QualityLevel> levels)
    {
        var preferred = profile.Preset switch
        {
            QualityPreset.Balanced => QualityLevel.Q2,
            QualityPreset.Smooth => QualityLevel.Q3,
            _ => QualityLevel.Q0,
        };
        return levels.Contains(preferred) ? preferred : levels[0];
    }

    private static bool HasUserConstraints(QualityProfile profile) =>
        profile.Preset == QualityPreset.Original ||
        profile.Preset == QualityPreset.Custom &&
        (profile.BandwidthLocked || profile.ColorLocked || profile.ScaleLocked || profile.RefreshLocked);

    private static bool HasLockedFixedRefresh(QualityProfile profile) =>
        profile.Preset == QualityPreset.Custom &&
        profile.RefreshLocked &&
        profile.Refresh.Mode == FrameRefreshMode.Fixed;
}
