using System.Collections.ObjectModel;

namespace WinARD.Remote.Protocol.Input;

public sealed class KeyboardInputAdapter : IDisposable
{
    private readonly KeyEventWriter _writer;
    private readonly List<uint> _pressOrder = [];
    private readonly HashSet<uint> _pressed = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KeyboardInputAdapter(KeyEventWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public IReadOnlyList<uint> PressedKeysyms
    {
        get
        {
            _gate.Wait();
            try
            {
                return new ReadOnlyCollection<uint>(_pressOrder.ToArray());
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async ValueTask KeyDownAsync(uint keysym, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteAsync(true, keysym, cancellationToken);
            if (_pressed.Add(keysym))
            {
                _pressOrder.Add(keysym);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask KeyUpAsync(uint keysym, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_pressed.Contains(keysym))
            {
                return;
            }

            await _writer.WriteAsync(false, keysym, cancellationToken);
            _pressed.Remove(keysym);
            _pressOrder.Remove(keysym);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask OnWindowFocusLostAsync(CancellationToken cancellationToken) =>
        ReleaseAllAsync(cancellationToken);

    public ValueTask OnDisconnectedAsync(CancellationToken cancellationToken) =>
        ReleaseAllAsync(cancellationToken);

    public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            while (_pressOrder.Count > 0)
            {
                var lastIndex = _pressOrder.Count - 1;
                var keysym = _pressOrder[lastIndex];
                await _writer.WriteAsync(false, keysym, cancellationToken);
                _pressOrder.RemoveAt(lastIndex);
                _pressed.Remove(keysym);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
