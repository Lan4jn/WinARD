namespace WinARD.Remote.Protocol.Ard;

public sealed class ArdServerCapabilities
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

    public ReadOnlyMemory<byte> CommandBitmap => _commandBitmap;

    public bool MayControl => (RawFlags & 0x02) != 0;

    public bool RequiresSessionSelection => (RawFlags & 0x04) != 0;
}
