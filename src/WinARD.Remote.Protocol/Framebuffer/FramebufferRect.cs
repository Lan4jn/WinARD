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
}
