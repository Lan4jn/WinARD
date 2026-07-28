using WinARD.Remote.Protocol.Errors;

namespace WinARD.Remote.Protocol.Framebuffer;

public readonly record struct FramebufferRect
{
    public FramebufferRect(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    internal static FramebufferRect CreateCursorRectangle(int x, int y, int width, int height) =>
        width == 0 || height == 0
            ? new FramebufferRect(x, y, width, height, allowEmpty: true)
            : new FramebufferRect(x, y, width, height);

    internal static FramebufferRect CreateMetadataRectangle(int x, int y, int width, int height) =>
        new(x, y, width, height, allowEmpty: true);

    public void ValidateWithin(int framebufferWidth, int framebufferHeight)
    {
        try
        {
            if (checked(X + Width) > framebufferWidth || checked(Y + Height) > framebufferHeight)
            {
                throw new RfbProtocolException(
                    $"Framebuffer rectangle ({X},{Y}) {Width}x{Height} exceeds {framebufferWidth}x{framebufferHeight}.");
            }
        }
        catch (OverflowException exception)
        {
            throw new RfbProtocolException("Framebuffer rectangle coordinates overflowed.", exception);
        }
    }

    private FramebufferRect(int x, int y, int width, int height, bool allowEmpty)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (!allowEmpty && (width == 0 || height == 0))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}
