using System.Buffers;
using System.Collections.ObjectModel;

namespace WinARD.Application.Ports;

public readonly record struct RemoteFramebufferSize(int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

public readonly record struct RemotePoint(int X, int Y);

public readonly record struct RemoteRectangle(int X, int Y, int Width, int Height);

public enum RemotePixelFormatKind
{
    Bgra32,
    Rgb565,
}

public sealed record RemoteQualitySettings
{
    public RemoteQualitySettings(
        RemotePixelFormatKind pixelFormat,
        IReadOnlyList<int> encodings,
        double scaleFactor)
    {
        if (!Enum.IsDefined(pixelFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));
        }

        ArgumentNullException.ThrowIfNull(encodings);
        if (encodings.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(encodings));
        }

        if (!double.IsFinite(scaleFactor) || scaleFactor <= 0 || scaleFactor > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleFactor));
        }

        PixelFormat = pixelFormat;
        Encodings = Array.AsReadOnly(encodings.ToArray());
        ScaleFactor = scaleFactor;
    }

    public RemotePixelFormatKind PixelFormat { get; }
    public IReadOnlyList<int> Encodings { get; }
    public double ScaleFactor { get; }
}

public enum QualityTransitionStatus
{
    Applied,
    NoChange,
    ReconnectRequired,
    CapabilityUnavailable,
    Faulted,
}

public sealed record RemoteDisplayCapabilities(int? MaximumRefreshRate)
{
    public static RemoteDisplayCapabilities Unknown { get; } = new((int?)null);
}

public sealed record RemoteRuntimePerformanceSnapshot(
    int InputWriteMilliseconds,
    int InputQueueDepth,
    long SampleSequence)
{
    public static RemoteRuntimePerformanceSnapshot Empty { get; } = new(0, 0, 0);
}

[Flags]
public enum RemotePointerButtons
{
    None = 0,
    Left = 1,
    Middle = 2,
    Right = 4,
}

public abstract record RemoteServerMessage;

public sealed record RemoteUpdateStatistics(
    long ReceivedSessionBytes,
    IReadOnlyDictionary<int, int> EncodingCounts)
{
    public static RemoteUpdateStatistics Empty { get; } = new(0, new Dictionary<int, int>());

    /// <summary>
    /// Gets bytes newly read from the underlying session stream since the previous statistics snapshot was emitted.
    /// Non-frame server messages are carried forward to the next framebuffer statistics snapshot. Buffering and ARD
    /// packet boundaries can shift attribution, so this is not a strict logical RFB-message wire length; totals across
    /// consecutive snapshots remain accurate without counting bytes twice.
    /// </summary>
    public long ReceivedSessionBytes { get; } = ValidateReceivedSessionBytes(ReceivedSessionBytes);
    public IReadOnlyDictionary<int, int> EncodingCounts { get; } = SnapshotEncodingCounts(EncodingCounts);

    private static long ValidateReceivedSessionBytes(long receivedSessionBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(receivedSessionBytes);
        return receivedSessionBytes;
    }

    private static ReadOnlyDictionary<int, int> SnapshotEncodingCounts(
        IReadOnlyDictionary<int, int> encodingCounts)
    {
        ArgumentNullException.ThrowIfNull(encodingCounts);
        return new ReadOnlyDictionary<int, int>(new Dictionary<int, int>(encodingCounts));
    }
}

public sealed class RemoteCursorUpdate : IDisposable
{
    private const int MaximumCursorBytes = 16 * 1024 * 1024;
    private IMemoryOwner<byte>? _pixels;

    public RemoteCursorUpdate(
        int hotspotX,
        int hotspotY,
        int width,
        int height,
        byte[] bgra32)
        : this(
            hotspotX,
            hotspotY,
            width,
            height,
            new ByteArrayMemoryOwner(bgra32 ?? throw new ArgumentNullException(nameof(bgra32))),
            bgra32.Length)
    {
    }

    public RemoteCursorUpdate(
        int hotspotX,
        int hotspotY,
        int width,
        int height,
        IMemoryOwner<byte> pixels,
        int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hotspotX);
        ArgumentOutOfRangeException.ThrowIfNegative(hotspotY);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentNullException.ThrowIfNull(pixels);
        var expectedLength = checked(width * height * 4);
        if (expectedLength > MaximumCursorBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                $"Cursor payload exceeds the {MaximumCursorBytes}-byte limit.");
        }

        if (length != expectedLength || pixels.Memory.Length < length)
        {
            throw new ArgumentException("Cursor pixel length does not match its dimensions.", nameof(length));
        }

        HotspotX = hotspotX;
        HotspotY = hotspotY;
        Width = width;
        Height = height;
        Length = length;
        _pixels = pixels;
    }

    public int HotspotX { get; }
    public int HotspotY { get; }
    public int Width { get; }
    public int Height { get; }
    public int Length { get; }
    public bool IsVisible => Width > 0 && Height > 0;
    public ReadOnlyMemory<byte> Bgra32 => PixelOwner.Memory[..Length];

    public void Dispose() => Interlocked.Exchange(ref _pixels, null)?.Dispose();

    private IMemoryOwner<byte> PixelOwner =>
        _pixels ?? throw new ObjectDisposedException(nameof(RemoteCursorUpdate));

    private sealed class ByteArrayMemoryOwner(byte[] pixels) : IMemoryOwner<byte>
    {
        private byte[]? _pixels = pixels;

        public Memory<byte> Memory =>
            _pixels ?? throw new ObjectDisposedException(nameof(ByteArrayMemoryOwner));

        public void Dispose() => _pixels = null;
    }
}

public sealed record RemoteCursorMessage : RemoteServerMessage, IDisposable
{
    private RemoteCursorUpdate? _cursor;

    public RemoteCursorMessage(RemoteCursorUpdate cursor)
        : this(cursor, statistics: null)
    {
    }

    public RemoteCursorMessage(
        RemoteCursorUpdate cursor,
        RemoteUpdateStatistics? statistics)
    {
        _cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
        Statistics = statistics ?? RemoteUpdateStatistics.Empty;
    }

    public RemoteCursorUpdate Cursor =>
        _cursor ?? throw new ObjectDisposedException(nameof(RemoteCursorMessage));
    public RemoteUpdateStatistics Statistics { get; }

    public RemoteCursorUpdate TakeCursorOwnership() =>
        Interlocked.Exchange(ref _cursor, null) ??
        throw new ObjectDisposedException(nameof(RemoteCursorMessage));

    public void Dispose() => Interlocked.Exchange(ref _cursor, null)?.Dispose();
}

public sealed record RemoteFramebufferMessage : RemoteServerMessage, IDisposable
{
    private IMemoryOwner<byte>? _pixels;
    private RemoteCursorUpdate? _cursor;

    public RemoteFramebufferMessage(
        RemoteFramebufferSize size,
        byte[] bgra32,
        int stride,
        IReadOnlyList<RemoteRectangle> dirtyRectangles,
        RemoteCursorUpdate? cursor = null)
        : this(size, bgra32, stride, dirtyRectangles, cursor, statistics: null)
    {
    }

    public RemoteFramebufferMessage(
        RemoteFramebufferSize size,
        byte[] bgra32,
        int stride,
        IReadOnlyList<RemoteRectangle> dirtyRectangles,
        RemoteCursorUpdate? cursor,
        RemoteUpdateStatistics? statistics)
        : this(
            size,
            new ByteArrayMemoryOwner(bgra32 ?? throw new ArgumentNullException(nameof(bgra32))),
            bgra32.Length,
            stride,
            dirtyRectangles,
            cursor,
            statistics)
    {
    }

    public RemoteFramebufferMessage(
        RemoteFramebufferSize size,
        IMemoryOwner<byte> pixels,
        int length,
        int stride,
        IReadOnlyList<RemoteRectangle> dirtyRectangles,
        RemoteCursorUpdate? cursor = null)
        : this(size, pixels, length, stride, dirtyRectangles, cursor, statistics: null)
    {
    }

    public RemoteFramebufferMessage(
        RemoteFramebufferSize size,
        IMemoryOwner<byte> pixels,
        int length,
        int stride,
        IReadOnlyList<RemoteRectangle> dirtyRectangles,
        RemoteCursorUpdate? cursor,
        RemoteUpdateStatistics? statistics)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(dirtyRectangles);
        if (!size.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (pixels.Memory.Length < length)
        {
            throw new ArgumentException("The pixel owner is smaller than the declared length.", nameof(pixels));
        }

        Size = size;
        _pixels = pixels;
        Length = length;
        Stride = stride;
        DirtyRectangles = dirtyRectangles;
        _cursor = cursor;
        Statistics = statistics ?? RemoteUpdateStatistics.Empty;
    }

    public RemoteFramebufferSize Size { get; }
    public int Length { get; }
    public int Stride { get; }
    public IReadOnlyList<RemoteRectangle> DirtyRectangles { get; }
    public RemoteUpdateStatistics Statistics { get; }
    public ReadOnlyMemory<byte> Bgra32 => PixelOwner.Memory[..Length];

    public IMemoryOwner<byte> TakePixelOwnership() =>
        Interlocked.Exchange(ref _pixels, null) ??
        throw new ObjectDisposedException(nameof(RemoteFramebufferMessage));

    public RemoteCursorUpdate? TakeCursorOwnership() =>
        Interlocked.Exchange(ref _cursor, null);

    public void Dispose()
    {
        Interlocked.Exchange(ref _pixels, null)?.Dispose();
        Interlocked.Exchange(ref _cursor, null)?.Dispose();
    }

    private IMemoryOwner<byte> PixelOwner =>
        _pixels ?? throw new ObjectDisposedException(nameof(RemoteFramebufferMessage));

    private sealed class ByteArrayMemoryOwner(byte[] pixels) : IMemoryOwner<byte>
    {
        private byte[]? _pixels = pixels;

        public Memory<byte> Memory =>
            _pixels ?? throw new ObjectDisposedException(nameof(ByteArrayMemoryOwner));

        public void Dispose() => _pixels = null;
    }
}

public sealed record RemoteClipboardMessage(string Text) : RemoteServerMessage;

public sealed record RemoteBellMessage : RemoteServerMessage;

public interface IRemoteSessionRuntime
{
    RemoteFramebufferSize FramebufferSize { get; }

    RemoteDisplayCapabilities DisplayCapabilities => RemoteDisplayCapabilities.Unknown;

    RemoteRuntimePerformanceSnapshot PerformanceSnapshot =>
        RemoteRuntimePerformanceSnapshot.Empty;

    /// <summary>
    /// Completes only after the framebuffer update request has been written to the protocol transport.
    /// Scheduling or client-message queue time occurs before completion.
    /// </summary>
    ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken);

    ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(
        RemoteQualitySettings settings,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(QualityTransitionStatus.CapabilityUnavailable);

    ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken);

    ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken);

    ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken);

    ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken);

    ValueTask DisconnectAsync();
}
