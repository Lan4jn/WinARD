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
            Math.Min(maxFramebufferBytes, DefaultMaxCursorBytes),
            maxFramebufferBytes,
            maxFramebufferBytes)
    {
    }

    public ProtocolLimits(int maxMessageBytes, int maxFramebufferBytes, int maxFramebufferUpdateBytes)
        : this(
            maxMessageBytes,
            maxFramebufferBytes,
            maxFramebufferUpdateBytes,
            DefaultWorkBytes(maxFramebufferBytes),
            Math.Min(maxFramebufferBytes, DefaultMaxCursorBytes),
            maxFramebufferUpdateBytes,
            maxFramebufferBytes)
    {
    }

    public ProtocolLimits(
        int maxMessageBytes,
        int maxFramebufferBytes,
        int maxFramebufferUpdateBytes,
        int maxFramebufferUpdateWorkBytes,
        int maxCursorBytes)
        : this(
            maxMessageBytes,
            maxFramebufferBytes,
            maxFramebufferUpdateBytes,
            maxFramebufferUpdateWorkBytes,
            maxCursorBytes,
            maxFramebufferUpdateBytes,
            Math.Min(maxFramebufferBytes, maxFramebufferUpdateWorkBytes))
    {
    }

    public ProtocolLimits(
        int maxMessageBytes,
        int maxFramebufferBytes,
        int maxFramebufferUpdateBytes,
        int maxFramebufferUpdateWorkBytes,
        int maxCursorBytes,
        int maxZrleCompressedBytes,
        int maxZrleDecompressedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFramebufferBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFramebufferUpdateBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFramebufferUpdateWorkBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCursorBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxZrleCompressedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxZrleDecompressedBytes);
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

        if (maxZrleCompressedBytes > maxFramebufferUpdateBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxZrleCompressedBytes),
                "The ZRLE compressed byte limit cannot exceed the framebuffer update byte limit.");
        }

        if (maxZrleDecompressedBytes > maxFramebufferUpdateWorkBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxZrleDecompressedBytes),
                "The ZRLE decompressed byte limit cannot exceed the framebuffer update work limit.");
        }

        MaxMessageBytes = maxMessageBytes;
        MaxFramebufferBytes = maxFramebufferBytes;
        MaxFramebufferUpdateBytes = maxFramebufferUpdateBytes;
        MaxFramebufferUpdateWorkBytes = maxFramebufferUpdateWorkBytes;
        MaxCursorBytes = maxCursorBytes;
        MaxZrleCompressedBytes = maxZrleCompressedBytes;
        MaxZrleDecompressedBytes = maxZrleDecompressedBytes;
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

    /// <summary>Maximum compressed bytes in one ZRLE rectangle.</summary>
    public int MaxZrleCompressedBytes { get; }

    /// <summary>Maximum cumulative decompressed bytes in one ZRLE rectangle.</summary>
    public int MaxZrleDecompressedBytes { get; }

    private static int DefaultWorkBytes(int maxFramebufferBytes) =>
        checked((int)Math.Min(int.MaxValue, (long)maxFramebufferBytes * 3));
}
