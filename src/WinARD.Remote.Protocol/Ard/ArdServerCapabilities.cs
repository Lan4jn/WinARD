namespace WinARD.Remote.Protocol.Ard;

[Flags]
#pragma warning disable CA1711
public enum ArdServerFlags : uint
{
    Observe = 0x01,
    MayControl = 0x02,
    SessionSelect = 0x04,
    NoVirtualDisplay = 0x08,
}
#pragma warning restore CA1711

public sealed class ArdServerCapabilities : IEquatable<ArdServerCapabilities>
{
    private const int CommandBitmapLength = 16;
    private readonly byte[] _commandBitmap;

    public ArdServerCapabilities(uint rawFlags, ReadOnlySpan<byte> commandBitmap)
    {
        if (commandBitmap.Length != CommandBitmapLength)
        {
            throw new ArgumentException("The command bitmap must be exactly 16 bytes.", nameof(commandBitmap));
        }

        RawFlags = rawFlags;
        _commandBitmap = commandBitmap.ToArray();
    }

    public uint RawFlags { get; }

    public ReadOnlyMemory<byte> CommandBitmap => _commandBitmap.ToArray();

    public ArdServerFlags Flags => (ArdServerFlags)RawFlags;

    public bool Observe => (Flags & ArdServerFlags.Observe) != 0;

    public bool MayControl => (Flags & ArdServerFlags.MayControl) != 0;

    public bool RequiresSessionSelection => (Flags & ArdServerFlags.SessionSelect) != 0;

    public bool NoVirtualDisplay => (Flags & ArdServerFlags.NoVirtualDisplay) != 0;

    public bool Equals(ArdServerCapabilities? other) =>
        other is not null &&
        RawFlags == other.RawFlags &&
        _commandBitmap.AsSpan().SequenceEqual(other._commandBitmap);

    public override bool Equals(object? obj) => Equals(obj as ArdServerCapabilities);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RawFlags);
        foreach (var command in _commandBitmap)
        {
            hash.Add(command);
        }

        return hash.ToHashCode();
    }
}
