using System.Collections.ObjectModel;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Desktop.Input;
using WinARD.Domain.Connections;
using WinARD.Remote.Protocol.Encodings;

namespace WinARD.Desktop.ViewModels;

public sealed record SessionPerformanceSnapshot(
    FrameRefreshMode Mode,
    int? TargetFramesPerSecond,
    int ActualFramesPerSecond,
    long ReceiveBytesPerSecond,
    int? PrimaryFramebufferEncoding,
    int ResponseMilliseconds,
    int InputWriteMilliseconds,
    int InputQueueDepth,
    long CoalescedPointerMoves,
    long SampleSequence);

public sealed record SessionPerformanceDiagnosticSnapshot
{
    public SessionPerformanceDiagnosticSnapshot(
        SessionPerformanceSnapshot Performance,
        int PresentationMilliseconds,
        long AutomaticTargetChanges,
        IReadOnlyDictionary<int, long> EncodingCounts,
        long OtherEncodingCount = 0)
    {
        this.Performance = Performance ?? throw new ArgumentNullException(nameof(Performance));
        ArgumentOutOfRangeException.ThrowIfNegative(PresentationMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(AutomaticTargetChanges);
        ArgumentOutOfRangeException.ThrowIfNegative(OtherEncodingCount);
        ArgumentNullException.ThrowIfNull(EncodingCounts);
        this.PresentationMilliseconds = PresentationMilliseconds;
        this.AutomaticTargetChanges = AutomaticTargetChanges;
        this.EncodingCounts = new ReadOnlyDictionary<int, long>(new Dictionary<int, long>(EncodingCounts));
        this.OtherEncodingCount = OtherEncodingCount;
    }

    public SessionPerformanceSnapshot Performance { get; }

    public int PresentationMilliseconds { get; }

    public long AutomaticTargetChanges { get; }

    public IReadOnlyDictionary<int, long> EncodingCounts { get; }

    public long OtherEncodingCount { get; }
}

internal sealed record SessionDiagnosticQualityMeasurements(
    SessionPerformanceDiagnosticSnapshot Performance,
    QualityObservation Observation);

internal sealed class SessionPerformanceTracker
{
    private const double NewSampleWeight = 0.25;
    private const int MaximumUnknownEncodingKeys = 32;
    private const int MaximumQualityIntervalCount = 6;
    private static readonly TimeSpan MinimumCompleteBucketDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QualityObservationDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScrollActivityDuration = TimeSpan.FromMilliseconds(600);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<int, long> _encodingCounts = [];
    private readonly Dictionary<int, long> _cumulativeEncodingCounts = [];
    private readonly Queue<QualityInterval> _qualityIntervals = new(MaximumQualityIntervalCount);
    private long _windowStarted;
    private long _latestTimestamp;
    private long _windowFrames;
    private long _windowBytes;
    private double _windowDirtyCoverage;
    private long _sampleSequence;
    private double? _responseMilliseconds;
    private double? _inputWriteMilliseconds;
    private double? _actualFramesPerSecond;
    private double? _receiveBytesPerSecond;
    private double? _presentationMilliseconds;
    private long _automaticTargetChanges;
    private long _otherEncodingCount;
    private int _unknownEncodingKeyCount;
    private int _windowUnknownEncodingKeyCount;
    private long? _lastInputTimestamp;
    private long? _lastScrollTimestamp;
    private DateTimeOffset? _lastInputUtc;
    private QualityInputActivityKind? _lastInputKind;
    private bool _pointerDragActive;
    private int _pendingInputCount;
    private SessionPerformanceSnapshot _current;

    public SessionPerformanceTracker(
        TimeProvider timeProvider,
        FrameRefreshPolicy policy,
        int? targetFramesPerSecond,
        long initialWindowFrames = 0)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _windowStarted = _timeProvider.GetTimestamp();
        _latestTimestamp = _windowStarted;
        ArgumentOutOfRangeException.ThrowIfNegative(initialWindowFrames);
        _windowFrames = initialWindowFrames;
        _current = Empty(policy.Mode, targetFramesPerSecond);
    }

    public SessionPerformanceSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public SessionPerformanceDiagnosticSnapshot CurrentDiagnostics
    {
        get
        {
            lock (_sync)
            {
                return CreateCurrentDiagnosticsNoLock();
            }
        }
    }

    public SessionDiagnosticQualityMeasurements CreateDiagnosticQualityMeasurements()
    {
        lock (_sync)
        {
            var observation = CreateQualityObservationNoLock(decodeTime: null);
            return new SessionDiagnosticQualityMeasurements(
                CreateCurrentDiagnosticsNoLock(),
                observation);
        }
    }

    public QualityActivitySnapshot CurrentActivity
    {
        get
        {
            lock (_sync)
            {
                var now = _timeProvider.GetTimestamp();
                PrepareTimestampNoLock(now);
                return CreateActivitySnapshotNoLock(now);
            }
        }
    }

    /// <summary>Records a real input event; callers use <see cref="SetInputActiveState"/> for state-only changes.</summary>
    public void RecordInputOccurred(
        QualityInputActivityKind kind,
        bool pointerDragActive,
        int pendingInputCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pendingInputCount);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        lock (_sync)
        {
            var now = _timeProvider.GetTimestamp();
            PrepareTimestampNoLock(now);
            _lastInputTimestamp = now;
            _lastInputUtc = _timeProvider.GetUtcNow();
            _lastInputKind = kind;
            _pointerDragActive = pointerDragActive;
            _pendingInputCount = pendingInputCount;
            if (kind == QualityInputActivityKind.Scroll)
            {
                _lastScrollTimestamp = now;
            }
        }
    }

    /// <summary>Updates queue and drag state without manufacturing a new input event.</summary>
    public void SetInputActiveState(bool pointerDragActive, int pendingInputCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pendingInputCount);

        lock (_sync)
        {
            _pointerDragActive = pointerDragActive;
            _pendingInputCount = pendingInputCount;
        }
    }

    /// <summary>
    /// Creates a content-free quality observation. A null decode time is unmeasured and is published as zero.
    /// </summary>
    public QualityObservation CreateQualityObservation(TimeSpan? decodeTime = null)
    {
        if (decodeTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(decodeTime));
        }

        lock (_sync)
        {
            return CreateQualityObservationNoLock(decodeTime);
        }
    }

    private QualityObservation CreateQualityObservationNoLock(TimeSpan? decodeTime)
    {
        var now = _timeProvider.GetTimestamp();
        PrepareTimestampNoLock(now);
        PruneQualityIntervalsNoLock(now);
        double totalBytesPerSecond = 0;
        double totalFramesPerSecond = 0;
        double peakBytesPerSecond = 0;
        double dirtyCoverage = 0;
        double totalOverlapSeconds = 0;
        foreach (var interval in _qualityIntervals)
        {
            var ageAtEnd = SafeElapsed(interval.EndTimestamp, now).TotalSeconds;
            var overlapSeconds = Math.Min(
                interval.DurationSeconds,
                QualityObservationDuration.TotalSeconds - ageAtEnd);
            if (overlapSeconds <= 0)
            {
                continue;
            }

            totalOverlapSeconds += overlapSeconds;
            totalBytesPerSecond += interval.BytesPerSecond * overlapSeconds;
            totalFramesPerSecond += interval.FramesPerSecond * overlapSeconds;
            peakBytesPerSecond = Math.Max(peakBytesPerSecond, interval.BytesPerSecond);
            dirtyCoverage = Math.Max(dirtyCoverage, interval.DirtyCoverage);
        }

        var averageBytesPerSecond = totalOverlapSeconds == 0
            ? 0
            : Math.Min(peakBytesPerSecond, totalBytesPerSecond / totalOverlapSeconds);
        var averageFramesPerSecond = totalOverlapSeconds == 0
            ? 0
            : totalFramesPerSecond / totalOverlapSeconds;
        var sinceLastInput = _lastInputTimestamp is { } lastInput
            ? SafeElapsed(lastInput, now)
            : TimeSpan.Zero;
        var activity = CreateActivitySnapshotNoLock(now);

        return new QualityObservation(
                _timeProvider.GetUtcNow(),
                ClampFiniteNonNegative(averageBytesPerSecond),
                ClampFiniteNonNegative(peakBytesPerSecond),
                ClampFiniteNonNegative(averageFramesPerSecond),
                TimeSpan.FromMilliseconds(_current.ResponseMilliseconds),
                decodeTime ?? TimeSpan.Zero,
                TimeSpan.FromMilliseconds(
                    _presentationMilliseconds is { } presentation
                        ? RoundMilliseconds(presentation)
                        : 0),
                Math.Clamp(dirtyCoverage, 0, 1),
                sinceLastInput,
                activity.PointerDragActive,
                activity.ScrollActive,
                activity.PendingInputCount);
    }

    private SessionPerformanceDiagnosticSnapshot CreateCurrentDiagnosticsNoLock() => new(
        _current,
        _presentationMilliseconds is { } presentation ? RoundMilliseconds(presentation) : 0,
        _automaticTargetChanges,
        _cumulativeEncodingCounts,
        _otherEncodingCount);

    public void RecordAutomaticTargetChange()
    {
        lock (_sync)
        {
            _automaticTargetChanges = SaturatingAdd(_automaticTargetChanges, 1);
        }
    }

    public void SetRefreshPolicy(FrameRefreshPolicy policy, int? targetFramesPerSecond)
    {
        lock (_sync)
        {
            _current = _current with
            {
                Mode = policy.Mode,
                TargetFramesPerSecond = targetFramesPerSecond,
            };
        }
    }

    public SessionPerformanceSnapshot ObserveFrame(
        RemoteUpdateStatistics update,
        IReadOnlyList<RemoteRectangle> dirty,
        RemoteFramebufferSize size,
        TimeSpan response,
        TimeSpan presentation,
        RemoteRuntimePerformanceSnapshot runtime,
        RemotePointerCoalescerSnapshot pointer)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(dirty);
        if (!size.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(response, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(presentation, TimeSpan.Zero);

        var dirtyCoverage = CalculateDirtyCoverage(dirty, size);

        lock (_sync)
        {
            var now = _timeProvider.GetTimestamp();
            PrepareTimestampNoLock(now);
            _windowDirtyCoverage = Math.Max(_windowDirtyCoverage, dirtyCoverage);
            var window = ObserveUpdateNoLock(update, countFrame: true, now);
            _responseMilliseconds = Smooth(_responseMilliseconds, response.TotalMilliseconds);
            _presentationMilliseconds = Smooth(
                _presentationMilliseconds,
                presentation.TotalMilliseconds);
            return PublishNoLock(
                window,
                runtime,
                pointer,
                RoundMilliseconds(_responseMilliseconds.Value));
        }
    }

    public SessionPerformanceSnapshot ObserveNonFrameUpdate(
        RemoteUpdateStatistics update,
        RemoteRuntimePerformanceSnapshot runtime,
        RemotePointerCoalescerSnapshot pointer)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_sync)
        {
            var now = _timeProvider.GetTimestamp();
            PrepareTimestampNoLock(now);
            var window = ObserveUpdateNoLock(update, countFrame: false, now);
            return PublishNoLock(window, runtime, pointer, responseMilliseconds: null);
        }
    }

    private WindowMetrics ObserveUpdateNoLock(
        RemoteUpdateStatistics update,
        bool countFrame,
        long now)
    {
        if (countFrame)
        {
            _windowFrames = SaturatingAdd(_windowFrames, 1);
        }

        _windowBytes = SaturatingAdd(_windowBytes, update.ReceivedSessionBytes);
        foreach (var (encoding, count) in update.EncodingCounts)
        {
            if (count <= 0)
            {
                continue;
            }

            if (_cumulativeEncodingCounts.TryGetValue(encoding, out var cumulative))
            {
                _cumulativeEncodingCounts[encoding] = SaturatingAdd(cumulative, count);
            }
            else if (IsKnownEncoding(encoding) || _unknownEncodingKeyCount < MaximumUnknownEncodingKeys)
            {
                _cumulativeEncodingCounts[encoding] = count;
                if (!IsKnownEncoding(encoding))
                {
                    _unknownEncodingKeyCount++;
                }
            }
            else
            {
                _otherEncodingCount = SaturatingAdd(_otherEncodingCount, count);
            }

            if (!IsPixelEncoding(encoding))
            {
                continue;
            }

            if (_encodingCounts.TryGetValue(encoding, out var previous))
            {
                _encodingCounts[encoding] = SaturatingAdd(previous, count);
            }
            else if (IsKnownEncoding(encoding) || _windowUnknownEncodingKeyCount < MaximumUnknownEncodingKeys)
            {
                _encodingCounts[encoding] = count;
                if (!IsKnownEncoding(encoding))
                {
                    _windowUnknownEncodingKeyCount++;
                }
            }
        }

        _sampleSequence = SaturatingAdd(_sampleSequence, 1);
        var metrics = new WindowMetrics(
            _current.ActualFramesPerSecond,
            _current.ReceiveBytesPerSecond,
            _current.PrimaryFramebufferEncoding);
        var elapsed = SafeElapsed(_windowStarted, now);
        if (elapsed < MinimumCompleteBucketDuration)
        {
            return metrics;
        }

        AddQualityIntervalNoLock(now, elapsed);

        _actualFramesPerSecond = Smooth(
            _actualFramesPerSecond,
            _windowFrames / elapsed.TotalSeconds);
        _receiveBytesPerSecond = Smooth(
            _receiveBytesPerSecond,
            _windowBytes / elapsed.TotalSeconds);
        metrics = new WindowMetrics(
            RoundMilliseconds(_actualFramesPerSecond.Value),
            RoundLong(_receiveBytesPerSecond.Value),
            SelectPrimaryEncoding(_encodingCounts));
        _windowStarted = now;
        _windowFrames = 0;
        _windowBytes = 0;
        _windowDirtyCoverage = 0;
        _encodingCounts.Clear();
        _windowUnknownEncodingKeyCount = 0;
        return metrics;
    }

    private SessionPerformanceSnapshot PublishNoLock(
        WindowMetrics window,
        RemoteRuntimePerformanceSnapshot runtime,
        RemotePointerCoalescerSnapshot pointer,
        int? responseMilliseconds)
    {
        _inputWriteMilliseconds = Smooth(
            _inputWriteMilliseconds,
            Math.Max(0, runtime.InputWriteMilliseconds));
        _current = _current with
        {
            ActualFramesPerSecond = window.ActualFramesPerSecond,
            ReceiveBytesPerSecond = window.ReceiveBytesPerSecond,
            PrimaryFramebufferEncoding = window.PrimaryFramebufferEncoding,
            ResponseMilliseconds = responseMilliseconds ?? _current.ResponseMilliseconds,
            InputWriteMilliseconds = RoundMilliseconds(_inputWriteMilliseconds.Value),
            InputQueueDepth = Math.Max(0, runtime.InputQueueDepth),
            CoalescedPointerMoves = Math.Max(0, pointer.CoalescedMoves),
            SampleSequence = _sampleSequence,
        };
        return _current;
    }

    private static SessionPerformanceSnapshot Empty(FrameRefreshMode mode, int? target) =>
        new(mode, target, 0, 0, null, 0, 0, 0, 0, 0);

    private static bool IsPixelEncoding(int encoding) =>
        encoding >= 0 && encoding is not
            (int)RfbEncodingType.CopyRect and not
            (int)RfbEncodingType.ArdDisplayInfo and not
            (int)RfbEncodingType.ArdSessionEncryption and not
            (int)RfbEncodingType.ArdDisplayInfo2;

    private static bool IsKnownEncoding(int encoding) => encoding is
        (int)RfbEncodingType.Raw or
        (int)RfbEncodingType.CopyRect or
        (int)RfbEncodingType.Zlib or
        (int)RfbEncodingType.Zrle or
        (int)RfbEncodingType.ArdDisplayInfo or
        (int)RfbEncodingType.ArdSessionEncryption or
        (int)RfbEncodingType.ArdDisplayInfo2 or
        (int)RfbEncodingType.DesktopSize or
        (int)RfbEncodingType.Cursor;

    private static int? SelectPrimaryEncoding(Dictionary<int, long> counts) =>
        counts.Count == 0
            ? null
            : counts.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .First().Key;

    private static long RoundLong(double value) =>
        (long)Math.Clamp(
            Math.Round(value, MidpointRounding.AwayFromZero),
            0,
            long.MaxValue);

    private static int RoundMilliseconds(double milliseconds) =>
        (int)Math.Clamp(
            Math.Round(milliseconds, MidpointRounding.AwayFromZero),
            0,
            int.MaxValue);

    private static double Smooth(double? previous, double current) =>
        previous is { } value
            ? NewSampleWeight * current + (1 - NewSampleWeight) * value
            : current;

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private void AddQualityIntervalNoLock(long now, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;
        var interval = new QualityInterval(
            _windowStarted,
            now,
            seconds,
            ClampFiniteNonNegative(_windowBytes / seconds),
            ClampFiniteNonNegative(_windowFrames / seconds),
            Math.Clamp(_windowDirtyCoverage, 0, 1));
        PruneQualityIntervalsNoLock(now);
        while (_qualityIntervals.Count >= MaximumQualityIntervalCount)
        {
            _ = _qualityIntervals.Dequeue();
        }

        _qualityIntervals.Enqueue(interval);
    }

    private void PruneQualityIntervalsNoLock(long now)
    {
        while (_qualityIntervals.TryPeek(out var oldest) &&
               SafeElapsed(oldest.EndTimestamp, now) >= QualityObservationDuration)
        {
            _ = _qualityIntervals.Dequeue();
        }
    }

    private void PrepareTimestampNoLock(long now)
    {
        if (now < _latestTimestamp)
        {
            _qualityIntervals.Clear();
            _windowStarted = now;
            _windowFrames = 0;
            _windowBytes = 0;
            _windowDirtyCoverage = 0;
            _encodingCounts.Clear();
            _windowUnknownEncodingKeyCount = 0;
            _lastInputTimestamp = null;
            _lastScrollTimestamp = null;
            _lastInputUtc = null;
            _lastInputKind = null;
        }

        _latestTimestamp = now;
    }

    private TimeSpan SafeElapsed(long start, long end)
    {
        if (end < start)
        {
            return TimeSpan.Zero;
        }

        try
        {
            return _timeProvider.GetElapsedTime(start, end);
        }
        catch (ArgumentOutOfRangeException)
        {
            return TimeSpan.MaxValue;
        }
        catch (OverflowException)
        {
            return TimeSpan.MaxValue;
        }
    }

    private static double CalculateDirtyCoverage(
        IReadOnlyList<RemoteRectangle> dirty,
        RemoteFramebufferSize size)
    {
        var frameArea = checked((long)size.Width * size.Height);
        long dirtyArea = 0;
        foreach (var rectangle in dirty)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                continue;
            }

            var left = Math.Clamp((long)rectangle.X, 0, size.Width);
            var top = Math.Clamp((long)rectangle.Y, 0, size.Height);
            var right = Math.Clamp((long)rectangle.X + rectangle.Width, 0, size.Width);
            var bottom = Math.Clamp((long)rectangle.Y + rectangle.Height, 0, size.Height);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            var area = checked((right - left) * (bottom - top));
            dirtyArea = SaturatingAdd(dirtyArea, area);
        }

        return Math.Clamp((double)dirtyArea / frameArea, 0, 1);
    }

    private static double ClampFiniteNonNegative(double value) =>
        !double.IsFinite(value) ? double.MaxValue : Math.Max(0, value);

    private QualityActivitySnapshot CreateActivitySnapshotNoLock(long now) =>
        new(
            _lastInputUtc,
            _lastInputKind,
            _pointerDragActive,
            _lastScrollTimestamp is { } lastScroll && SafeElapsed(lastScroll, now) < ScrollActivityDuration,
            _pendingInputCount);

    private readonly record struct WindowMetrics(
        int ActualFramesPerSecond,
        long ReceiveBytesPerSecond,
        int? PrimaryFramebufferEncoding);

    private readonly record struct QualityInterval(
        long StartTimestamp,
        long EndTimestamp,
        double DurationSeconds,
        double BytesPerSecond,
        double FramesPerSecond,
        double DirtyCoverage);
}
