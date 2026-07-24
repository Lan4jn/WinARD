namespace WinARD.Remote.Protocol.IO;

public sealed record ProtocolLimits
{
    public ProtocolLimits(int maxMessageBytes, int maxFramebufferBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFramebufferBytes);

        MaxMessageBytes = maxMessageBytes;
        MaxFramebufferBytes = maxFramebufferBytes;
    }

    public static ProtocolLimits Default { get; } = new(16 * 1024 * 1024, 256 * 1024 * 1024);

    public int MaxMessageBytes { get; }

    public int MaxFramebufferBytes { get; }
}
