namespace WinARD.Application.Quality;

public enum CapabilitySupport
{
    Unknown,
    Unsupported,

    /// <summary>
    /// The peer advertised the capability. Advertisement is neither observation nor approval to enable it.
    /// </summary>
    Advertised,

    Observed,
}

public static class CapabilitySupportExtensions
{
    public static bool IsObserved(this CapabilitySupport support)
    {
        Validate(support);
        return support is CapabilitySupport.Observed;
    }

    internal static CapabilitySupport Validate(CapabilitySupport support)
    {
        if (!Enum.IsDefined(support))
        {
            throw new ArgumentOutOfRangeException(nameof(support));
        }

        return support;
    }
}

/// <summary>
/// Records connection-specific protocol evidence. These values do not by themselves authorize
/// negotiating or decoding an Apple-private encoding.
/// </summary>
public sealed record ArdDisplayCapabilities(
    CapabilitySupport Zlib,
    CapabilitySupport Rgb565,
    CapabilitySupport ServerScaling,
    CapabilitySupport AppleColor1002,
    CapabilitySupport AppleGrayscale1001,
    bool SafeOnlinePixelFormatSwitch,
    bool SafeOnlineScaleSwitch,
    int? MaximumRefreshRate)
{
    public static ArdDisplayCapabilities Unknown { get; } = new(
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        false,
        false,
        null);

    public CapabilitySupport Zlib { get; } = CapabilitySupportExtensions.Validate(Zlib);
    public CapabilitySupport Rgb565 { get; } = CapabilitySupportExtensions.Validate(Rgb565);
    public CapabilitySupport ServerScaling { get; } = CapabilitySupportExtensions.Validate(ServerScaling);

    /// <summary>
    /// Gets evidence about encoding 1002, not permission to enable it. Formal availability requires
    /// a separate, explicit decoder evidence gate; no such gate exists in this model.
    /// </summary>
    public CapabilitySupport AppleColor1002 { get; } = CapabilitySupportExtensions.Validate(AppleColor1002);

    /// <summary>
    /// Gets evidence about encoding 1001, not permission to enable it. Formal availability requires
    /// a separate, explicit decoder evidence gate; no such gate exists in this model.
    /// </summary>
    public CapabilitySupport AppleGrayscale1001 { get; } =
        CapabilitySupportExtensions.Validate(AppleGrayscale1001);

    public int? MaximumRefreshRate { get; } = ValidateMaximumRefreshRate(MaximumRefreshRate);

    private static int? ValidateMaximumRefreshRate(int? maximumRefreshRate)
    {
        if (maximumRefreshRate is < 30 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRefreshRate));
        }

        return maximumRefreshRate;
    }
}

public enum QualityContentState
{
    Idle,
    Interactive,
    Motion,
    Recovery,
}

public enum QualityLevel
{
    Q0,
    Q1,
    Q2,
    Q3,
    Q4,
}

public enum QualityDecisionReason
{
    Initial,
    MotionDetected,
    SustainedOverTarget,
    SevereOverTarget,
    StableRecovery,
    UserConstraint,
    CapabilityLimited,
    TargetUnsatisfied,
}

/// <summary>Explicit decoder approvals, kept separate from protocol capability evidence.</summary>
public sealed record QualityDecoderGates(
    bool AppleColor1002Approved = false,
    bool AppleGrayscale1001Approved = false);

public sealed record QualityObservation
{
    public QualityObservation(
        DateTimeOffset timestamp,
        double averageBytesPerSecond5s,
        double peakBytesPerSecond5s,
        double actualFramesPerSecond,
        TimeSpan responseTime,
        TimeSpan decodeTime,
        TimeSpan presentationTime,
        double dirtyCoverage,
        TimeSpan sinceLastInput,
        bool pointerDragActive,
        bool scrollActive,
        int pendingInputCount)
    {
        Timestamp = timestamp;
        AverageBytesPerSecond5s = ValidateFiniteNonNegative(
            averageBytesPerSecond5s,
            nameof(averageBytesPerSecond5s));
        PeakBytesPerSecond5s = ValidateFiniteNonNegative(
            peakBytesPerSecond5s,
            nameof(peakBytesPerSecond5s));
        if (PeakBytesPerSecond5s < AverageBytesPerSecond5s)
        {
            throw new ArgumentOutOfRangeException(
                nameof(peakBytesPerSecond5s),
                "Peak throughput cannot be lower than average throughput.");
        }
        ActualFramesPerSecond = ValidateFiniteNonNegative(actualFramesPerSecond, nameof(actualFramesPerSecond));
        ResponseTime = ValidateDuration(responseTime, nameof(responseTime));
        DecodeTime = ValidateDuration(decodeTime, nameof(decodeTime));
        PresentationTime = ValidateDuration(presentationTime, nameof(presentationTime));
        DirtyCoverage = ValidateCoverage(dirtyCoverage);
        SinceLastInput = ValidateDuration(sinceLastInput, nameof(sinceLastInput));
        PointerDragActive = pointerDragActive;
        ScrollActive = scrollActive;
        ArgumentOutOfRangeException.ThrowIfNegative(pendingInputCount);

        PendingInputCount = pendingInputCount;
    }

    public DateTimeOffset Timestamp { get; }
    public double AverageBytesPerSecond5s { get; }
    public double PeakBytesPerSecond5s { get; }
    public double ActualFramesPerSecond { get; }
    public TimeSpan ResponseTime { get; }
    public TimeSpan DecodeTime { get; }
    public TimeSpan PresentationTime { get; }
    public double DirtyCoverage { get; }
    public TimeSpan SinceLastInput { get; }
    public bool PointerDragActive { get; }
    public bool ScrollActive { get; }
    public int PendingInputCount { get; }

    private static double ValidateFiniteNonNegative(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    private static TimeSpan ValidateDuration(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    private static double ValidateCoverage(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }
}

public sealed record QualityDecision
{
    public QualityDecision(
        long generation,
        QualityContentState contentState,
        QualityLevel level,
        WinARD.Domain.Connections.QualityColor color,
        WinARD.Domain.Connections.QualityScale scale,
        int targetFramesPerSecond,
        QualityDecisionReason reason,
        bool targetSatisfied,
        bool levelChanged,
        bool contentStateChanged,
        QualityLevel? previousLevel,
        QualityContentState? previousContentState)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);

        if (!Enum.IsDefined(contentState))
        {
            throw new ArgumentOutOfRangeException(nameof(contentState));
        }

        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        if (!Enum.IsDefined(color))
        {
            throw new ArgumentOutOfRangeException(nameof(color));
        }

        if (!Enum.IsDefined(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetFramesPerSecond);
        var approved = level switch
        {
            QualityLevel.Q0 => (WinARD.Domain.Connections.QualityColor.Full32,
                WinARD.Domain.Connections.QualityScale.Percent100, 60),
            QualityLevel.Q1 => (WinARD.Domain.Connections.QualityColor.Color16,
                WinARD.Domain.Connections.QualityScale.Percent100, 60),
            QualityLevel.Q2 => (WinARD.Domain.Connections.QualityColor.Color16,
                WinARD.Domain.Connections.QualityScale.Percent75, 60),
            QualityLevel.Q3 => (WinARD.Domain.Connections.QualityColor.Color16,
                WinARD.Domain.Connections.QualityScale.Percent50, 45),
            _ => (WinARD.Domain.Connections.QualityColor.Grayscale,
                WinARD.Domain.Connections.QualityScale.Percent50, 30),
        };
        if (color != approved.Item1 || scale != approved.Item2 || targetFramesPerSecond > approved.Item3)
        {
            throw new ArgumentException("The decision must use its quality level's approved table entry.");
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (previousLevel is { } priorLevel && !Enum.IsDefined(priorLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(previousLevel));
        }

        if (previousContentState is { } priorState && !Enum.IsDefined(priorState))
        {
            throw new ArgumentOutOfRangeException(nameof(previousContentState));
        }

        Generation = generation;
        ContentState = contentState;
        Level = level;
        Color = color;
        Scale = scale;
        TargetFramesPerSecond = targetFramesPerSecond;
        Reason = reason;
        TargetSatisfied = targetSatisfied;
        LevelChanged = levelChanged;
        ContentStateChanged = contentStateChanged;
        PreviousLevel = previousLevel;
        PreviousContentState = previousContentState;
    }

    public long Generation { get; }
    public QualityContentState ContentState { get; }
    public QualityLevel Level { get; }
    public WinARD.Domain.Connections.QualityColor Color { get; }
    public WinARD.Domain.Connections.QualityScale Scale { get; }
    public int TargetFramesPerSecond { get; }
    public QualityDecisionReason Reason { get; }
    public bool TargetSatisfied { get; }
    public bool LevelChanged { get; }
    public bool ContentStateChanged { get; }
    public QualityLevel? PreviousLevel { get; }
    public QualityContentState? PreviousContentState { get; }
}
