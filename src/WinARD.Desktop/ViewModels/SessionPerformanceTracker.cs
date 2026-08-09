using System.Collections.ObjectModel;
using WinARD.Application.Ports;
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

internal sealed class SessionPerformanceTracker
{
    private const double NewSampleWeight = 0.25;
    private const int MaximumUnknownEncodingKeys = 32;
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<int, long> _encodingCounts = [];
    private readonly Dictionary<int, long> _cumulativeEncodingCounts = [];
    private long _windowStarted;
    private long _windowFrames;
    private long _windowBytes;
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
    private SessionPerformanceSnapshot _current;

    public SessionPerformanceTracker(
        TimeProvider timeProvider,
        FrameRefreshPolicy policy,
        int? targetFramesPerSecond,
        long initialWindowFrames = 0)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _windowStarted = _timeProvider.GetTimestamp();
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
                return new SessionPerformanceDiagnosticSnapshot(
                    _current,
                    _presentationMilliseconds is { } presentation
                        ? RoundMilliseconds(presentation)
                        : 0,
                    _automaticTargetChanges,
                    _cumulativeEncodingCounts,
                    _otherEncodingCount);
            }
        }
    }

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

        lock (_sync)
        {
            var window = ObserveUpdateNoLock(update, countFrame: true);
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
            var window = ObserveUpdateNoLock(update, countFrame: false);
            return PublishNoLock(window, runtime, pointer, responseMilliseconds: null);
        }
    }

    private WindowMetrics ObserveUpdateNoLock(RemoteUpdateStatistics update, bool countFrame)
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
        var now = _timeProvider.GetTimestamp();
        var elapsed = _timeProvider.GetElapsedTime(_windowStarted, now);
        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return metrics;
        }

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

    private readonly record struct WindowMetrics(
        int ActualFramesPerSecond,
        long ReceiveBytesPerSecond,
        int? PrimaryFramebufferEncoding);
}
