using System.Text;
using Windows.System;
using WinARD.Application.Ports;

namespace WinARD.Desktop.Input;

public sealed class WindowsInputMapper : IAsyncDisposable
{
    private static readonly TimeSpan DisposeReleaseTimeout = TimeSpan.FromMilliseconds(250);
    private const uint LeftShift = 0xffe1;
    private const uint RightShift = 0xffe2;
    private const uint LeftControl = 0xffe3;
    private const uint RightControl = 0xffe4;
    private const uint LeftAlt = 0xffe9;
    private const uint RightAlt = 0xffea;
    private readonly Func<uint, bool, CancellationToken, ValueTask> _sender;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<KeyIdentity, uint> _pressed = [];
    private readonly List<KeyIdentity> _pressOrder = [];
    private volatile bool _disposed;

    public WindowsInputMapper(Func<uint, bool, CancellationToken, ValueTask> sender) =>
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));

    public async ValueTask KeyDownAsync(
        VirtualKey key,
        int scanCode,
        bool isExtended,
        string? text,
        CancellationToken cancellationToken)
    {
        var identity = new KeyIdentity(key, scanCode, isExtended);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var keysym = ToKeysym(key, scanCode, isExtended, text);
        if (keysym == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pressed.ContainsKey(identity))
            {
                if (!SuppressRepeat(keysym))
                {
                    await _sender(keysym, true, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await _sender(keysym, true, cancellationToken).ConfigureAwait(false);
            _pressed.Add(identity, keysym);
            _pressOrder.Add(identity);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask KeyUpAsync(
        VirtualKey key,
        int scanCode,
        bool isExtended,
        string? text,
        CancellationToken cancellationToken)
    {
        _ = text;
        ObjectDisposedException.ThrowIf(_disposed, this);
        var identity = new KeyIdentity(key, scanCode, isExtended);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_pressed.TryGetValue(identity, out var keysym))
            {
                return;
            }

            await _sender(keysym, false, cancellationToken).ConfigureAwait(false);
            _pressed.Remove(identity);
            _pressOrder.Remove(identity);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask TextInputAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var keysym = ToUnicodeKeysym(text);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _sender(keysym, true, cancellationToken).ConfigureAwait(false);
            await _sender(keysym, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var index = _pressOrder.Count - 1; index >= 0; index--)
            {
                var identity = _pressOrder[index];
                if (_pressed.TryGetValue(identity, out var keysym))
                {
                    await _sender(keysym, false, cancellationToken).ConfigureAwait(false);
                    _pressed.Remove(identity);
                }
            }

            _pressOrder.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SendSecureAttentionSequenceAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pressedBySequence = new List<uint>(3);
        try
        {
            if (!_pressed.ContainsValue(LeftControl) && !_pressed.ContainsValue(RightControl))
            {
                await _sender(LeftControl, true, cancellationToken).ConfigureAwait(false);
                pressedBySequence.Add(LeftControl);
            }

            if (!_pressed.ContainsValue(LeftAlt) && !_pressed.ContainsValue(RightAlt))
            {
                await _sender(LeftAlt, true, cancellationToken).ConfigureAwait(false);
                pressedBySequence.Add(LeftAlt);
            }

            if (!_pressed.ContainsValue(0xffff))
            {
                await _sender(0xffff, true, cancellationToken).ConfigureAwait(false);
                pressedBySequence.Add(0xffff);
            }
        }
        finally
        {
            try
            {
                using var timeout = new CancellationTokenSource(DisposeReleaseTimeout);
                for (var index = pressedBySequence.Count - 1; index >= 0; index--)
                {
                    if (!await ReleaseBestEffortAsync(pressedBySequence[index], timeout)
                        .ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public static byte ToPointerMask(RemotePointerButtons buttons)
    {
        byte mask = 0;
        if ((buttons & RemotePointerButtons.Left) != 0) mask |= 1;
        if ((buttons & RemotePointerButtons.Middle) != 0) mask |= 2;
        if ((buttons & RemotePointerButtons.Right) != 0) mask |= 4;
        return mask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        using var timeout = new CancellationTokenSource(DisposeReleaseTimeout);
        var acquired = false;
        try
        {
            try
            {
                await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
                acquired = true;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return;
            }

            try
            {
                for (var index = _pressOrder.Count - 1; index >= 0; index--)
                {
                    var identity = _pressOrder[index];
                    if (!_pressed.TryGetValue(identity, out var keysym))
                    {
                        continue;
                    }

                    if (!await ReleaseBestEffortAsync(keysym, timeout).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            finally
            {
                _pressed.Clear();
                _pressOrder.Clear();
            }
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
        }
    }

    public static byte WithWheel(byte currentMask, int wheelDelta) => wheelDelta switch
    {
        > 0 => checked((byte)(currentMask | 8)),
        < 0 => checked((byte)(currentMask | 16)),
        _ => currentMask,
    };

    public static uint ToUnicodeKeysym(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var enumerator = text.EnumerateRunes().GetEnumerator();
        if (!enumerator.MoveNext())
        {
            throw new ArgumentException("Text must contain one Unicode scalar.", nameof(text));
        }

        var value = checked((uint)enumerator.Current.Value);
        if (enumerator.MoveNext())
        {
            throw new ArgumentException("Text must contain one Unicode scalar.", nameof(text));
        }

        return value <= 0xff ? value : 0x01000000 | value;
    }

    private async ValueTask<bool> ReleaseBestEffortAsync(
        uint keysym,
        CancellationTokenSource timeout)
    {
        try
        {
            await _sender(keysym, false, timeout.Token).AsTask()
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            // Input release during shutdown or sequence cleanup is best effort.
            return true;
        }
    }

    private static uint ToKeysym(VirtualKey key, int scanCode, bool isExtended, string? text)
    {
        var value = (int)key;
        if (value is >= 0x41 and <= 0x5a)
        {
            return string.IsNullOrEmpty(text)
                ? checked((uint)(value + 0x20))
                : ToUnicodeKeysym(text);
        }

        if (value is >= 0x30 and <= 0x39)
        {
            return checked((uint)value);
        }

        if (value is >= 0x70 and <= 0x87)
        {
            return checked((uint)(0xffbe + value - 0x70));
        }

        return value switch
        {
            0x08 => 0xff08,
            0x09 => 0xff09,
            0x0d => 0xff0d,
            0x10 => scanCode == 0x36 ? RightShift : LeftShift,
            0x11 => isExtended ? RightControl : LeftControl,
            0x12 => isExtended ? RightAlt : LeftAlt,
            0x14 => 0xffe5,
            0x1b => 0xff1b,
            0x20 => 0x20,
            0x21 => 0xff55,
            0x22 => 0xff56,
            0x23 => 0xff57,
            0x24 => 0xff50,
            0x25 => 0xff51,
            0x26 => 0xff52,
            0x27 => 0xff53,
            0x28 => 0xff54,
            0x2d => 0xff63,
            0x2e => 0xffff,
            0x90 => 0xff7f,
            0x91 => 0xff14,
            _ when !string.IsNullOrEmpty(text) => ToUnicodeKeysym(text),
            _ => 0,
        };
    }

    private static bool SuppressRepeat(uint keysym) => keysym is
        LeftShift or RightShift or
        LeftControl or RightControl or
        LeftAlt or RightAlt or
        0xffe5 or 0xff7f or 0xff14;

    private readonly record struct KeyIdentity(VirtualKey Key, int ScanCode, bool IsExtended);
}
