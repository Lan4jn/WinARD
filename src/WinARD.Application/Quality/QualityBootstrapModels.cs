using WinARD.Application.Ports;

namespace WinARD.Application.Quality;

public enum QualityBootstrapAttempt
{
    Preferred,
    Fallback,
}

public enum QualityBootstrapReason
{
    UserFull32,
    UserColor16,
    AutomaticBandwidth,
    CapabilityLimited,
    SafeFallback,
}

public sealed record QualityBootstrapSettings
{
    private static readonly HashSet<int> AllowedEncodings = [16, 6, 0, 1, -239, -223];

    public QualityBootstrapSettings(
        RemotePixelFormatKind pixelFormat,
        IReadOnlyList<int> encodings,
        QualityBootstrapReason reason)
    {
        if (!Enum.IsDefined(pixelFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));
        }

        ArgumentNullException.ThrowIfNull(encodings);
        if (encodings.Count == 0)
        {
            throw new ArgumentException("At least one encoding is required.", nameof(encodings));
        }

        var snapshot = encodings.ToArray();
        if (snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException("Duplicate encodings are not allowed.", nameof(encodings));
        }

        if (snapshot.Any(encoding => !AllowedEncodings.Contains(encoding)))
        {
            throw new ArgumentOutOfRangeException(nameof(encodings));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        PixelFormat = pixelFormat;
        Encodings = Array.AsReadOnly(snapshot);
        Reason = reason;
    }

    public RemotePixelFormatKind PixelFormat { get; }

    public IReadOnlyList<int> Encodings { get; }

    public QualityBootstrapReason Reason { get; }

    public bool Equals(QualityBootstrapSettings? other) =>
        ReferenceEquals(this, other) ||
        (other is not null &&
            PixelFormat == other.PixelFormat &&
            Reason == other.Reason &&
            Encodings.SequenceEqual(other.Encodings));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PixelFormat);
        hash.Add(Reason);
        foreach (var encoding in Encodings)
        {
            hash.Add(encoding);
        }

        return hash.ToHashCode();
    }
}

public sealed record QualityBootstrapPlan
{
    public QualityBootstrapPlan(
        QualityBootstrapSettings preferred,
        QualityBootstrapSettings fallback)
    {
        Preferred = preferred ?? throw new ArgumentNullException(nameof(preferred));
        Fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public QualityBootstrapSettings Preferred { get; }

    public QualityBootstrapSettings Fallback { get; }
}

public sealed record QualityBootstrapState
{
    public QualityBootstrapState(
        QualityBootstrapAttempt attempt,
        QualityBootstrapSettings actualQuality)
        : this(attempt, actualQuality, preferredFailureReason: null)
    {
    }

    public QualityBootstrapState(
        QualityBootstrapAttempt attempt,
        QualityBootstrapSettings actualQuality,
        QualityBootstrapFailureReason? preferredFailureReason)
        : this((QualityBootstrapAttempt?)attempt, actualQuality, preferredFailureReason)
    {
    }

    private QualityBootstrapState(
        QualityBootstrapAttempt? attempt,
        QualityBootstrapSettings actualQuality,
        QualityBootstrapFailureReason? preferredFailureReason = null)
    {
        if (attempt is { } value && !Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        if (preferredFailureReason is { } failureReason && !Enum.IsDefined(failureReason))
        {
            throw new ArgumentOutOfRangeException(nameof(preferredFailureReason));
        }

        if (preferredFailureReason is not null && attempt != QualityBootstrapAttempt.Fallback)
        {
            throw new ArgumentException(
                "A preferred failure reason is only valid for a fallback attempt.",
                nameof(preferredFailureReason));
        }

        Attempt = attempt;
        ActualQuality = actualQuality ?? throw new ArgumentNullException(nameof(actualQuality));
        PreferredFailureReason = preferredFailureReason;
    }

    public static QualityBootstrapState LegacyBgra32 { get; } = new(
        attempt: null,
        new QualityBootstrapSettings(
            RemotePixelFormatKind.Bgra32,
            [6, 16, 0, 1, -239, -223],
            QualityBootstrapReason.SafeFallback));

    public QualityBootstrapAttempt? Attempt { get; }

    public QualityBootstrapSettings ActualQuality { get; }

    public bool FallbackUsed => Attempt == QualityBootstrapAttempt.Fallback;

    public QualityBootstrapFailureReason? PreferredFailureReason { get; }
}
