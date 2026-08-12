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

public sealed record SessionTransferDiagnosticSnapshot
{
    public SessionTransferDiagnosticSnapshot(
        long rectangleCount,
        long pixelArea,
        long wirePayloadBytes,
        long? bytesPerPixelMilli,
        int dirtyCoveragePermille,
        IReadOnlyDictionary<int, long> wirePayloadBytesByEncoding,
        long otherEncodingWirePayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rectangleCount);
        ArgumentOutOfRangeException.ThrowIfNegative(pixelArea);
        ArgumentOutOfRangeException.ThrowIfNegative(wirePayloadBytes);
        if (bytesPerPixelMilli is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerPixelMilli));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(dirtyCoveragePermille, 1000);
        ArgumentOutOfRangeException.ThrowIfNegative(dirtyCoveragePermille);
        ArgumentNullException.ThrowIfNull(wirePayloadBytesByEncoding);
        if (wirePayloadBytesByEncoding.Values.Any(value => value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(wirePayloadBytesByEncoding));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(otherEncodingWirePayloadBytes);
        RectangleCount = rectangleCount;
        PixelArea = pixelArea;
        WirePayloadBytes = wirePayloadBytes;
        BytesPerPixelMilli = bytesPerPixelMilli;
        DirtyCoveragePermille = dirtyCoveragePermille;
        WirePayloadBytesByEncoding = new ReadOnlyDictionary<int, long>(
            new Dictionary<int, long>(wirePayloadBytesByEncoding));
        OtherEncodingWirePayloadBytes = otherEncodingWirePayloadBytes;
    }

    public static SessionTransferDiagnosticSnapshot Empty { get; } = new(
        0, 0, 0, null, 0, new Dictionary<int, long>(), 0);

    public long RectangleCount { get; }
    public long PixelArea { get; }
    public long WirePayloadBytes { get; }
    public long? BytesPerPixelMilli { get; }
    public int DirtyCoveragePermille { get; }
    public IReadOnlyDictionary<int, long> WirePayloadBytesByEncoding { get; }
    public long OtherEncodingWirePayloadBytes { get; }
}

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

    public SessionTransferDiagnosticSnapshot Transfer { get; init; } =
        SessionTransferDiagnosticSnapshot.Empty;
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
    private long _transferWindowStarted;
    private long _inputBaselineTimestamp;
    private long _observationClockStarted;
    private DateTimeOffset _observationClockStartedUtc;
    private DateTimeOffset _latestObservationUtc;
    private long _latestTimestamp;
    private long _windowFrames;
    private long _windowBytes;
    private double _windowDirtyCoverage;
    private long _windowTransferRectangleCount;
    private long _windowTransferPixelArea;
    private long _windowTransferWirePayloadBytes;
    private long _windowTransferFramebufferArea;
    private long _windowTransferCoveredPixelArea;
    private long _windowOtherEncodingWirePayloadBytes;
    private readonly Dictionary<int, long> _windowWirePayloadBytesByEncoding = [];
    private SessionTransferDiagnosticSnapshot _lastCompletedTransfer =
        SessionTransferDiagnosticSnapshot.Empty;
    private long _lastCompletedTransferTimestamp;
    private long _lastCompletedTransferSampleTimestamp;
    private long _lastTransferSampleTimestamp;
    private long _transferVersion;
    private bool _windowTransferHasSamples;
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
        _transferWindowStarted = _windowStarted;
        _inputBaselineTimestamp = _windowStarted;
        _observationClockStarted = _windowStarted;
        _observationClockStartedUtc = _timeProvider.GetUtcNow();
        _latestObservationUtc = _observationClockStartedUtc;
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
                AdvanceTransferWindowNoLock(_timeProvider.GetTimestamp());
                return CreateCurrentDiagnosticsNoLock();
            }
        }
    }

    internal long TransferVersion => Volatile.Read(ref _transferVersion);

    public SessionDiagnosticQualityMeasurements CreateDiagnosticQualityMeasurements()
    {
        lock (_sync)
        {
            AdvanceTransferWindowNoLock(_timeProvider.GetTimestamp());
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
            : SafeElapsed(_inputBaselineTimestamp, now);
        var activity = CreateActivitySnapshotNoLock(now);

        return new QualityObservation(
                CreateObservationTimestampNoLock(now),
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
        _otherEncodingCount)
    {
        Transfer = CurrentTransferSnapshotNoLock(),
    };

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

        var dirtyCapacity = checked((long)size.Width * size.Height);
        var dirtyArea = CalculateDirtyArea(dirty, size);
        return ObserveFrame(
            update, dirtyArea, dirtyCapacity, size, response, presentation, runtime, pointer);
    }

    internal SessionPerformanceSnapshot ObserveFrame(
        RemoteUpdateStatistics update,
        long dirtyArea,
        long dirtyCapacity,
        RemoteFramebufferSize size,
        TimeSpan response,
        TimeSpan presentation,
        RemoteRuntimePerformanceSnapshot runtime,
        RemotePointerCoalescerSnapshot pointer)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentOutOfRangeException.ThrowIfNegative(dirtyArea);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dirtyCapacity);
        var dirtyCoverage = Math.Clamp((double)dirtyArea / dirtyCapacity, 0, 1);

        lock (_sync)
        {
            var now = _timeProvider.GetTimestamp();
            PrepareTimestampNoLock(now);
            AdvanceTransferWindowNoLock(now);
            _windowDirtyCoverage = Math.Max(_windowDirtyCoverage, dirtyCoverage);
            ObserveTransferNoLock(update.TransferStatistics, dirtyArea, dirtyCapacity);
            _lastTransferSampleTimestamp = now;
            _ = Interlocked.Increment(ref _transferVersion);
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

    private void ObserveTransferNoLock(
        RemoteFramebufferTransferStatistics transfer,
        long dirtyArea,
        long dirtyCapacity)
    {
        _windowTransferHasSamples = true;
        _windowTransferRectangleCount = SaturatingAdd(
            _windowTransferRectangleCount,
            transfer.RectangleCount);
        _windowTransferPixelArea = SaturatingAdd(_windowTransferPixelArea, transfer.PixelArea);
        _windowTransferWirePayloadBytes = SaturatingAdd(
            _windowTransferWirePayloadBytes,
            transfer.WirePayloadBytes);
        _windowTransferFramebufferArea = SaturatingAdd(
            _windowTransferFramebufferArea,
            dirtyCapacity);
        _windowTransferCoveredPixelArea = SaturatingAdd(
            _windowTransferCoveredPixelArea,
            Math.Min(dirtyArea, dirtyCapacity));

        foreach (var (encoding, byteCount) in transfer.WirePayloadBytesByEncoding)
        {
            if (byteCount <= 0)
            {
                continue;
            }

            if (!IsKnownEncoding(encoding))
            {
                _windowOtherEncodingWirePayloadBytes = SaturatingAdd(
                    _windowOtherEncodingWirePayloadBytes,
                    byteCount);
                continue;
            }

            _windowWirePayloadBytesByEncoding.TryGetValue(encoding, out var previous);
            _windowWirePayloadBytesByEncoding[encoding] = SaturatingAdd(previous, byteCount);
        }
    }

    private SessionTransferDiagnosticSnapshot CreateTransferSnapshotNoLock()
    {
        var bytesPerPixelMilli = CalculateRatioPermille(
            _windowTransferWirePayloadBytes,
            _windowTransferPixelArea);
        var dirtyCoveragePermille = CalculateRatioPermille(
            _windowTransferCoveredPixelArea,
            _windowTransferFramebufferArea) is { } coverage
                ? (int)Math.Clamp(coverage, 0, 1000)
                : 0;
        return new SessionTransferDiagnosticSnapshot(
            _windowTransferRectangleCount,
            _windowTransferPixelArea,
            _windowTransferWirePayloadBytes,
            bytesPerPixelMilli,
            dirtyCoveragePermille,
            _windowWirePayloadBytesByEncoding,
            _windowOtherEncodingWirePayloadBytes);
    }

    private SessionTransferDiagnosticSnapshot CurrentTransferSnapshotNoLock()
    {
        if (_windowTransferHasSamples &&
            SafeElapsed(_lastTransferSampleTimestamp, _latestTimestamp) < QualityObservationDuration)
        {
            return CreateTransferSnapshotNoLock();
        }

        return _lastCompletedTransfer != SessionTransferDiagnosticSnapshot.Empty &&
               SafeElapsed(_lastCompletedTransferSampleTimestamp, _latestTimestamp) <
               QualityObservationDuration
            ? _lastCompletedTransfer
            : SessionTransferDiagnosticSnapshot.Empty;
    }

    private void AdvanceTransferWindowNoLock(long now)
    {
        PrepareTimestampNoLock(now);
        var elapsed = SafeElapsed(_transferWindowStarted, now);
        if (elapsed >= QualityObservationDuration)
        {
            ResetTransferWindowNoLock();
            _lastCompletedTransfer = SessionTransferDiagnosticSnapshot.Empty;
            _lastCompletedTransferTimestamp = now;
            _transferWindowStarted = now;
            return;
        }

        if (elapsed < MinimumCompleteBucketDuration)
        {
            return;
        }

        if (_windowTransferHasSamples)
        {
            _lastCompletedTransfer = CreateTransferSnapshotNoLock();
            _lastCompletedTransferTimestamp = now;
            _lastCompletedTransferSampleTimestamp = _lastTransferSampleTimestamp;
        }

        ResetTransferWindowNoLock();
        _transferWindowStarted = now;
    }

    private void ResetTransferWindowNoLock()
    {
        _windowTransferRectangleCount = 0;
        _windowTransferPixelArea = 0;
        _windowTransferWirePayloadBytes = 0;
        _windowTransferFramebufferArea = 0;
        _windowTransferCoveredPixelArea = 0;
        _windowOtherEncodingWirePayloadBytes = 0;
        _windowTransferHasSamples = false;
        _windowWirePayloadBytesByEncoding.Clear();
    }

    private static long? CalculateRatioPermille(long numerator, long denominator) =>
        denominator == 0
            ? null
            : (long)Math.Min(
                long.MaxValue,
                decimal.Round(
                    (decimal)numerator * 1000 / denominator,
                    0,
                    MidpointRounding.AwayFromZero));

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
            _transferWindowStarted = now;
            _inputBaselineTimestamp = now;
            _observationClockStarted = now;
            _observationClockStartedUtc = _latestObservationUtc;
            _windowFrames = 0;
            _windowBytes = 0;
            _windowDirtyCoverage = 0;
            ResetTransferWindowNoLock();
            _lastCompletedTransfer = SessionTransferDiagnosticSnapshot.Empty;
            _lastCompletedTransferTimestamp = now;
            _encodingCounts.Clear();
            _windowUnknownEncodingKeyCount = 0;
            _lastInputTimestamp = null;
            _lastScrollTimestamp = null;
            _lastInputUtc = null;
            _lastInputKind = null;
        }

        _latestTimestamp = now;
    }

    private DateTimeOffset CreateObservationTimestampNoLock(long now)
    {
        DateTimeOffset candidate;
        try
        {
            candidate = _observationClockStartedUtc.Add(SafeElapsed(_observationClockStarted, now));
        }
        catch (ArgumentOutOfRangeException)
        {
            candidate = DateTimeOffset.MaxValue;
        }

        if (candidate < _latestObservationUtc)
        {
            return _latestObservationUtc;
        }

        _latestObservationUtc = candidate;
        return candidate;
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

    internal static long CalculateDirtyArea(
        IReadOnlyList<RemoteRectangle> dirty,
        RemoteFramebufferSize size)
    {
        var rectangles = new List<(long Left, long Top, long Right, long Bottom)>();
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

            rectangles.Add((left, top, right, bottom));
        }

        return CalculateRectangleUnionArea(rectangles);
    }

    private static long CalculateRectangleUnionArea(
        List<(long Left, long Top, long Right, long Bottom)> rectangles)
    {
        if (rectangles.Count == 0)
        {
            return 0;
        }

        var yCoordinates = rectangles
            .SelectMany(rectangle => new[] { rectangle.Top, rectangle.Bottom })
            .Distinct()
            .Order()
            .ToArray();
        var yIndexes = yCoordinates
            .Select((value, index) => (value, index))
            .ToDictionary(pair => pair.value, pair => pair.index);
        var events = rectangles
            .SelectMany(rectangle => new[]
            {
                new CoverageEvent(rectangle.Left, 1, yIndexes[rectangle.Top], yIndexes[rectangle.Bottom]),
                new CoverageEvent(rectangle.Right, -1, yIndexes[rectangle.Top], yIndexes[rectangle.Bottom]),
            })
            .OrderBy(item => item.X)
            .ToArray();
        var tree = new CoverageSegmentTree(yCoordinates);
        long area = 0;
        var previousX = events[0].X;
        var eventIndex = 0;
        while (eventIndex < events.Length)
        {
            var x = events[eventIndex].X;
            area = SaturatingAdd(area, checked((x - previousX) * tree.CoveredLength));
            while (eventIndex < events.Length && events[eventIndex].X == x)
            {
                var item = events[eventIndex++];
                tree.Update(item.TopIndex, item.BottomIndex, item.Delta);
            }

            previousX = x;
        }

        return area;
    }

    private readonly record struct CoverageEvent(long X, int Delta, int TopIndex, int BottomIndex);

    private sealed class CoverageSegmentTree(long[] coordinates)
    {
        private readonly int[] _covers = new int[Math.Max(1, coordinates.Length * 4)];
        private readonly long[] _lengths = new long[Math.Max(1, coordinates.Length * 4)];

        public long CoveredLength => _lengths[1];

        public void Update(int start, int end, int delta) =>
            Update(1, 0, coordinates.Length - 1, start, end, delta);

        private void Update(int node, int left, int right, int start, int end, int delta)
        {
            if (start >= right || end <= left)
            {
                return;
            }

            if (start <= left && right <= end)
            {
                _covers[node] += delta;
            }
            else
            {
                var middle = left + (right - left) / 2;
                Update(node * 2, left, middle, start, end, delta);
                Update(node * 2 + 1, middle, right, start, end, delta);
            }

            _lengths[node] = _covers[node] > 0
                ? coordinates[right] - coordinates[left]
                : right - left == 1
                    ? 0
                    : SaturatingAdd(_lengths[node * 2], _lengths[node * 2 + 1]);
        }
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
