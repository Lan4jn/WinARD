namespace WinARD.Remote.Protocol.IO;

public sealed record ProtocolLimits
{
    private const int DefaultMaxCursorBytes = 16 * 1024 * 1024;

    public ProtocolLimits(int maxMessageBytes, int maxFramebufferBytes)
        : this(
            maxMessageBytes,
            maxFramebufferBytes,
            maxFramebufferBytes,
            DefaultWorkBytes(maxFramebufferBytes),
            Math.Min(maxFramebufferBytes, DefaultMaxCursorBytes))
    {
    }

    public ProtocolLimits(int maxMessageBytes, int maxFramebufferBytes, int maxFramebufferUpdateBytes)
        : this(
            maxMessageBytes,
            maxFramebufferBytes,
            maxFramebufferUpdateBytes,
            DefaultWorkBytes(maxFramebufferBytes),
            Math.Min(maxFramebufferBytes, DefaultMaxCursorBytes))
    {
    }

    public ProtocolLimits(
        int maxMessageBytes,
        int maxFramebufferBytes,
        int maxFramebufferUpdateBytes,
        int maxFramebufferUpdateWorkBytes,
        int maxCursorBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFramebufferBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFramebufferUpdateBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFramebufferUpdateWorkBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCursorBytes);
        if (maxFramebufferUpdateBytes > maxFramebufferBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFramebufferUpdateBytes),
                "The framebuffer update byte limit cannot exceed the framebuffer byte limit.");
        }

        if (maxCursorBytes > maxFramebufferBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCursorBytes),
                "The cursor byte limit cannot exceed the framebuffer byte limit.");
        }

        MaxMessageBytes = maxMessageBytes;
        MaxFramebufferBytes = maxFramebufferBytes;
        MaxFramebufferUpdateBytes = maxFramebufferUpdateBytes;
        MaxFramebufferUpdateWorkBytes = maxFramebufferUpdateWorkBytes;
        MaxCursorBytes = maxCursorBytes;
    }

    public static ProtocolLimits Default { get; } = new(
        16 * 1024 * 1024,
        256 * 1024 * 1024,
        256 * 1024 * 1024,
        768 * 1024 * 1024,
        DefaultMaxCursorBytes);

    public int MaxMessageBytes { get; }

    public int MaxFramebufferBytes { get; }

    /// <summary>Maximum decoder payload bytes consumed from one FramebufferUpdate message.</summary>
    public int MaxFramebufferUpdateBytes { get; }

    /// <summary>
    /// Maximum estimated decoding work for one FramebufferUpdate, including conversion,
    /// allocation initialization, and framebuffer/cursor copies. Payload bytes are budgeted separately.
    /// </summary>
    public int MaxFramebufferUpdateWorkBytes { get; }

    /// <summary>Maximum BGRA storage for one decoded cursor image.</summary>
    public int MaxCursorBytes { get; }

    private static int DefaultWorkBytes(int maxFramebufferBytes) =>
        checked((int)Math.Min(int.MaxValue, (long)maxFramebufferBytes * 3));
}
