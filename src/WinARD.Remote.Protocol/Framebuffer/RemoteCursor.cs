namespace WinARD.Remote.Protocol.Framebuffer;

public sealed class RemoteCursor
{
    private readonly byte[] _pixels;

    public RemoteCursor(int hotspotX, int hotspotY, int width, int height, ReadOnlySpan<byte> bgraPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hotspotX);
        ArgumentOutOfRangeException.ThrowIfNegative(hotspotY);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        var expectedLength = checked(width * height * 4);
        if (bgraPixels.Length != expectedLength)
        {
            throw new ArgumentException("Cursor pixel length does not match its dimensions.", nameof(bgraPixels));
        }

        HotspotX = hotspotX;
        HotspotY = hotspotY;
        Width = width;
        Height = height;
        _pixels = bgraPixels.ToArray();
    }

    public int HotspotX { get; }
    public int HotspotY { get; }
    public int Width { get; }
    public int Height { get; }

    public byte[] GetPixelsBgra32() => (byte[])_pixels.Clone();
}
