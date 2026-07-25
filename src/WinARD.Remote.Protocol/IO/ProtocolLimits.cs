namespace WinARD.Remote.Protocol.IO;

public sealed record ProtocolLimits
{
    public ProtocolLimits(int maxMessageBytes, int maxFramebufferBytes)
        : this(maxMessageBytes, maxFramebufferBytes, maxFramebufferBytes)
    {
    }

    public ProtocolLimits(int maxMessageBytes, int maxFramebufferBytes, int maxFramebufferUpdateBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFramebufferBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFramebufferUpdateBytes);
        if (maxFramebufferUpdateBytes > maxFramebufferBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFramebufferUpdateBytes),
                "The framebuffer update byte limit cannot exceed the framebuffer byte limit.");
        }

        MaxMessageBytes = maxMessageBytes;
        MaxFramebufferBytes = maxFramebufferBytes;
        MaxFramebufferUpdateBytes = maxFramebufferUpdateBytes;
    }

    public static ProtocolLimits Default { get; } = new(
        16 * 1024 * 1024,
        256 * 1024 * 1024,
        256 * 1024 * 1024);

    public int MaxMessageBytes { get; }

    public int MaxFramebufferBytes { get; }

    public int MaxFramebufferUpdateBytes { get; }
}
