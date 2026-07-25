using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Framebuffer;

public sealed class Framebuffer : IDisposable
{
    private readonly ProtocolLimits _limits;
    private byte[] _pixels;
    private bool _disposed;

    public Framebuffer(int width, int height, ProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
        _pixels = Allocate(width, height, limits);
        Width = width;
        Height = height;
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => checked(Width * 4);

    internal ProtocolLimits Limits => _limits;

    public byte[] GetPixelsBgra32()
    {
        ThrowIfDisposed();
        return (byte[])_pixels.Clone();
    }

    public uint GetBgra32(int x, int y)
    {
        ThrowIfDisposed();
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(_pixels.AsSpan((y * Stride) + (x * 4), 4));
    }

    internal void ApplyRaw(FramebufferRect rectangle, ReadOnlySpan<byte> bgraPixels)
    {
        ThrowIfDisposed();
        rectangle.ValidateWithin(Width, Height);
        var expectedLength = CheckedByteLength(rectangle.Width, rectangle.Height, _limits.MaxFramebufferBytes);
        if (bgraPixels.Length != expectedLength)
        {
            throw new RfbProtocolException(
                $"Raw rectangle contains {bgraPixels.Length} BGRA bytes; expected {expectedLength}.");
        }

        var sourceStride = checked(rectangle.Width * 4);
        for (var row = 0; row < rectangle.Height; row++)
        {
            var destination = _pixels.AsSpan(
                ((rectangle.Y + row) * Stride) + (rectangle.X * 4),
                sourceStride);
            bgraPixels.Slice(row * sourceStride, sourceStride).CopyTo(destination);
            for (var alphaIndex = 3; alphaIndex < destination.Length; alphaIndex += 4)
            {
                destination[alphaIndex] = 255;
            }
        }
    }

    internal void CopyRect(FramebufferRect destination, int sourceX, int sourceY)
    {
        ThrowIfDisposed();
        destination.ValidateWithin(Width, Height);
        var source = new FramebufferRect(sourceX, sourceY, destination.Width, destination.Height);
        source.ValidateWithin(Width, Height);

        var rowBytes = checked(destination.Width * 4);
        if (destination.Y > sourceY && destination.Y < sourceY + destination.Height)
        {
            for (var row = destination.Height - 1; row >= 0; row--)
            {
                CopyRow(sourceX, sourceY + row, destination.X, destination.Y + row, rowBytes);
            }
        }
        else
        {
            for (var row = 0; row < destination.Height; row++)
            {
                CopyRow(sourceX, sourceY + row, destination.X, destination.Y + row, rowBytes);
            }
        }
    }

    internal void Resize(int width, int height)
    {
        ThrowIfDisposed();
        var replacement = Allocate(width, height, _limits);
        var previous = _pixels;
        _pixels = replacement;
        Width = width;
        Height = height;
        CryptographicOperations.ZeroMemory(previous);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_pixels);
        _disposed = true;
    }

    private static byte[] Allocate(int width, int height, ProtocolLimits limits)
    {
        if (width <= 0 || height <= 0)
        {
            throw new RfbProtocolException("Framebuffer dimensions must be positive.");
        }

        var byteLength = CheckedByteLength(width, height, limits.MaxFramebufferBytes);
        var pixels = new byte[byteLength];
        for (var index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
        }

        return pixels;
    }

    private static int CheckedByteLength(int width, int height, int limit)
    {
        long byteLength;
        try
        {
            byteLength = checked((long)width * height * 4);
        }
        catch (OverflowException exception)
        {
            throw new RfbProtocolException("Framebuffer byte length overflowed.", exception);
        }

        if (byteLength > limit || byteLength > int.MaxValue)
        {
            throw new RfbProtocolException(
                $"Framebuffer requires {byteLength} bytes, exceeding the configured limit of {limit} bytes.");
        }

        return checked((int)byteLength);
    }

    private void CopyRow(int sourceX, int sourceY, int destinationX, int destinationY, int byteCount) =>
        Buffer.BlockCopy(
            _pixels,
            (sourceY * Stride) + (sourceX * 4),
            _pixels,
            (destinationY * Stride) + (destinationX * 4),
            byteCount);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
