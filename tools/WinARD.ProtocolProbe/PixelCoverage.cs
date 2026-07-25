using System.Numerics;
using WinARD.Remote.Protocol.Framebuffer;

namespace WinARD.ProtocolProbe;

internal sealed class PixelCoverage
{
    private int _width;
    private int _height;
    private ulong[] _bits = [];
    private long _coveredPixels;
    private long _totalPixels;

    public PixelCoverage(int width, int height)
    {
        Reset(width, height);
    }

    public bool IsComplete => _coveredPixels == _totalPixels;
    internal long CoveredPixels => _coveredPixels;
    internal long StorageByteLength => checked((long)_bits.Length * sizeof(ulong));

    public void Reset(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var totalPixels = checked((long)width * height);
        var wordCount = checked((totalPixels + 63) / 64);
        if (wordCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Pixel coverage storage is too large.");
        }

        _width = width;
        _height = height;
        _bits = new ulong[checked((int)wordCount)];
        _coveredPixels = 0;
        _totalPixels = totalPixels;
    }

    public void Add(FramebufferRect rectangle, CancellationToken cancellationToken)
    {
        rectangle.ValidateWithin(_width, _height);
        cancellationToken.ThrowIfCancellationRequested();

        var rectangleEndX = checked(rectangle.X + rectangle.Width);
        var rectangleEndY = checked(rectangle.Y + rectangle.Height);
        for (var y = rectangle.Y; y < rectangleEndY; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowStart = checked(((long)y * _width) + rectangle.X);
            SetRange(rowStart, checked(rowStart + (rectangleEndX - rectangle.X)), cancellationToken);
        }
    }

    private void SetRange(long start, long end, CancellationToken cancellationToken)
    {
        var firstWord = checked((int)(start / 64));
        var lastWord = checked((int)((end - 1) / 64));
        var firstOffset = checked((int)(start % 64));
        if (firstWord == lastWord)
        {
            SetBits(firstWord, LowBits(checked((int)(end - start))) << firstOffset);
            return;
        }

        SetBits(firstWord, ulong.MaxValue << firstOffset);
        for (var word = firstWord + 1; word < lastWord; word++)
        {
            if ((word & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            SetBits(word, ulong.MaxValue);
        }

        var lastBitCount = checked((int)(end - ((long)lastWord * 64)));
        SetBits(lastWord, LowBits(lastBitCount));
    }

    private void SetBits(int wordIndex, ulong mask)
    {
        var previous = _bits[wordIndex];
        var added = mask & ~previous;
        if (added == 0)
        {
            return;
        }

        _bits[wordIndex] = previous | mask;
        _coveredPixels += BitOperations.PopCount(added);
    }

    private static ulong LowBits(int count) =>
        count == 64 ? ulong.MaxValue : (1UL << count) - 1;
}
