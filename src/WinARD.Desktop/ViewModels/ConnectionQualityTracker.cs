namespace WinARD.Desktop.ViewModels;

public enum ConnectionQualityLevel
{
    Unmeasured,
    Good,
    Fair,
    Poor,
    Disconnected,
}

/// <summary>
/// Connection quality snapshot containing latency and qualitative status.
/// <paramref name="ResponseMilliseconds"/> measures total request-to-frame turnaround
/// (request transmission, remote capture/encoding turnaround, network RTT, and receive decryption);
/// it must not be conflated with pure network RTT.
/// </summary>
public sealed record ConnectionQualitySnapshot(
    ConnectionQualityLevel Level,
    int? ResponseMilliseconds,
    string DisplayText,
    long SampleSequence);

internal sealed class ConnectionQualityPublicationGate
{
    private long _lastAcceptedSequence;

    public bool TryAccept(ConnectionQualitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        while (true)
        {
            var current = Volatile.Read(ref _lastAcceptedSequence);
            if (snapshot.SampleSequence <= current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _lastAcceptedSequence,
                    snapshot.SampleSequence,
                    current) == current)
            {
                return true;
            }
        }
    }
}

internal sealed class ConnectionQualityTracker
{
    private const double NewSampleWeight = 0.25;
    private readonly TimeProvider _timeProvider;
    private long? _requestTimestamp;
    private double? _averageMilliseconds;
    private long _sampleSequence;

    public ConnectionQualityTracker(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Current = new ConnectionQualitySnapshot(
            ConnectionQualityLevel.Unmeasured,
            ResponseMilliseconds: null,
            "等待测量",
            SampleSequence: 0);
    }

    public ConnectionQualitySnapshot Current { get; private set; }

    public void BeginRequest() => _requestTimestamp = _timeProvider.GetTimestamp();

    public ConnectionQualitySnapshot? CompleteResponse()
    {
        if (_requestTimestamp is not { } started)
        {
            return null;
        }

        _requestTimestamp = null;
        var sampleMilliseconds = Math.Max(
            0,
            _timeProvider.GetElapsedTime(started, _timeProvider.GetTimestamp()).TotalMilliseconds);
        _averageMilliseconds = _averageMilliseconds is { } previous
            ? NewSampleWeight * sampleMilliseconds + (1 - NewSampleWeight) * previous
            : sampleMilliseconds;
        var displayMilliseconds = checked((int)Math.Min(
            int.MaxValue,
            Math.Round(_averageMilliseconds.Value, MidpointRounding.AwayFromZero)));
        var level = displayMilliseconds switch
        {
            < 150 => ConnectionQualityLevel.Good,
            <= 500 => ConnectionQualityLevel.Fair,
            _ => ConnectionQualityLevel.Poor,
        };
        var label = level switch
        {
            ConnectionQualityLevel.Good => "良好",
            ConnectionQualityLevel.Fair => "一般",
            _ => "较差",
        };
        Current = new ConnectionQualitySnapshot(
            level,
            displayMilliseconds,
            $"{label} · {displayMilliseconds} ms",
            checked(++_sampleSequence));
        return Current;
    }

    public ConnectionQualitySnapshot Disconnect()
    {
        _requestTimestamp = null;
        Current = new ConnectionQualitySnapshot(
            ConnectionQualityLevel.Disconnected,
            ResponseMilliseconds: null,
            "已断开",
            checked(++_sampleSequence));
        return Current;
    }
}
