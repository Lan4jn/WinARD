using System.Buffers;
using WinARD.Application.Ports;

namespace WinARD.Desktop.Rendering;

public enum ViewportScaleMode
{
    Fit,
    ActualSize,
}

public readonly record struct ViewportTransform
{
    private ViewportTransform(
        int remoteWidth,
        int remoteHeight,
        double originX,
        double originY,
        double dipScale,
        bool isValid)
    {
        RemoteWidth = remoteWidth;
        RemoteHeight = remoteHeight;
        OriginX = originX;
        OriginY = originY;
        DipScale = dipScale;
        IsValid = isValid;
    }

    public int RemoteWidth { get; }
    public int RemoteHeight { get; }
    public double OriginX { get; }
    public double OriginY { get; }
    public double DipScale { get; }
    public bool IsValid { get; }

    public static ViewportTransform Create(
        int remoteWidth,
        int remoteHeight,
        double viewportWidth,
        double viewportHeight,
        double dpiScale,
        ViewportScaleMode mode)
    {
        if (remoteWidth <= 0 || remoteHeight <= 0 ||
            !double.IsFinite(viewportWidth) || viewportWidth <= 0 ||
            !double.IsFinite(viewportHeight) || viewportHeight <= 0 ||
            !double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            return default;
        }

        var scale = mode switch
        {
            ViewportScaleMode.Fit => Math.Min(viewportWidth / remoteWidth, viewportHeight / remoteHeight),
            ViewportScaleMode.ActualSize => 1 / dpiScale,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return default;
        }

        var displayWidth = remoteWidth * scale;
        var displayHeight = remoteHeight * scale;
        var originX = mode == ViewportScaleMode.Fit && displayWidth < viewportWidth
            ? (viewportWidth - displayWidth) / 2
            : 0;
        var originY = mode == ViewportScaleMode.Fit && displayHeight < viewportHeight
            ? (viewportHeight - displayHeight) / 2
            : 0;
        return new ViewportTransform(
            remoteWidth,
            remoteHeight,
            originX,
            originY,
            scale,
            isValid: true);
    }

    public bool TryMapToRemote(double viewportX, double viewportY, out RemotePoint point)
    {
        point = default;
        if (!IsValid || !double.IsFinite(viewportX) || !double.IsFinite(viewportY))
        {
            return false;
        }

        var remoteX = Math.Floor((viewportX - OriginX) / DipScale);
        var remoteY = Math.Floor((viewportY - OriginY) / DipScale);
        point = new RemotePoint(
            (int)Math.Clamp(remoteX, 0, RemoteWidth - 1d),
            (int)Math.Clamp(remoteY, 0, RemoteHeight - 1d));
        return true;
    }
}

public static class FrameValidation
{
    public static void ValidateBgra32(int width, int height, int stride, int bufferLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferLength);
        int minimumStride;
        long requiredLength;
        try
        {
            minimumStride = checked(width * 4);
            requiredLength = checked((long)stride * height);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("Framebuffer dimensions overflow.", exception);
        }

        if (stride < minimumStride || requiredLength > bufferLength)
        {
            throw new ArgumentException("BGRA32 stride or buffer length is invalid.");
        }
    }

    public static IReadOnlyList<RemoteRectangle> ClipDirtyRectangles(
        IEnumerable<RemoteRectangle> rectangles,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(rectangles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var clipped = new List<RemoteRectangle>();
        foreach (var rectangle in rectangles)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                continue;
            }

            var left = Math.Clamp((long)rectangle.X, 0, width);
            var top = Math.Clamp((long)rectangle.Y, 0, height);
            var right = Math.Clamp((long)rectangle.X + rectangle.Width, 0, width);
            var bottom = Math.Clamp((long)rectangle.Y + rectangle.Height, 0, height);
            if (right > left && bottom > top)
            {
                clipped.Add(new RemoteRectangle(
                    checked((int)left),
                    checked((int)top),
                    checked((int)(right - left)),
                    checked((int)(bottom - top))));
            }
        }

        return clipped;
    }
}

public sealed class FramePacket : IDisposable
{
    private IMemoryOwner<byte>? _owner;

    public FramePacket(
        long sequence,
        int width,
        int height,
        int stride,
        int length,
        IReadOnlyList<RemoteRectangle> dirtyRectangles,
        IMemoryOwner<byte> owner)
    {
        ArgumentNullException.ThrowIfNull(dirtyRectangles);
        ArgumentNullException.ThrowIfNull(owner);
        FrameValidation.ValidateBgra32(width, height, stride, length);
        if (owner.Memory.Length < length)
        {
            throw new ArgumentException("Frame owner is smaller than the declared length.", nameof(owner));
        }

        Sequence = sequence;
        Width = width;
        Height = height;
        Stride = stride;
        Length = length;
        DirtyRectangles = dirtyRectangles;
        _owner = owner;
    }

    public long Sequence { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int Length { get; }
    public IReadOnlyList<RemoteRectangle> DirtyRectangles { get; private set; }
    public ReadOnlyMemory<byte> Pixels =>
        (_owner ?? throw new ObjectDisposedException(nameof(FramePacket))).Memory[..Length];

    public static FramePacket CopyFrom(long sequence, RemoteFramebufferMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        FrameValidation.ValidateBgra32(
            message.Size.Width,
            message.Size.Height,
            message.Stride,
            message.Bgra32.Length);
        var owner = MemoryPool<byte>.Shared.Rent(message.Bgra32.Length);
        message.Bgra32.CopyTo(owner.Memory.Span);
        return new FramePacket(
            sequence,
            message.Size.Width,
            message.Size.Height,
            message.Stride,
            message.Bgra32.Length,
            FrameValidation.ClipDirtyRectangles(
                message.DirtyRectangles,
                message.Size.Width,
                message.Size.Height),
            owner);
    }

    internal void MarkEntireFrameDirty() =>
        DirtyRectangles = [new RemoteRectangle(0, 0, Width, Height)];

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
}

public sealed class LatestFrameMailbox : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _available = new(0, 1);
    private readonly CancellationTokenSource _disposed = new();
    private FramePacket? _latest;
    private bool _isDisposed;

    public void Publish(FramePacket frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        FramePacket? replaced;
        var signal = false;
        lock (_sync)
        {
            if (_isDisposed)
            {
                frame.Dispose();
                throw new ObjectDisposedException(nameof(LatestFrameMailbox));
            }

            replaced = _latest;
            if (replaced is not null)
            {
                frame.MarkEntireFrameDirty();
            }

            _latest = frame;
            signal = replaced is null;
        }

        replaced?.Dispose();
        if (signal)
        {
            _available.Release();
        }
    }

    public async ValueTask<FramePacket> ReadLatestAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposed.Token);
        await _available.WaitAsync(linked.Token).ConfigureAwait(false);
        lock (_sync)
        {
            if (_isDisposed)
            {
                throw new OperationCanceledException(_disposed.Token);
            }

            return Interlocked.Exchange(ref _latest, null) ??
                throw new InvalidOperationException("The frame signal did not have a frame.");
        }
    }

    public ValueTask DisposeAsync()
    {
        FramePacket? pending;
        lock (_sync)
        {
            if (_isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            _isDisposed = true;
            pending = Interlocked.Exchange(ref _latest, null);
        }

        pending?.Dispose();
        _disposed.Cancel();
        return ValueTask.CompletedTask;
    }
}
