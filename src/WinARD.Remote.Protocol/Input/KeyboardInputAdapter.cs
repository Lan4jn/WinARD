using System.Collections.ObjectModel;
using WinARD.Remote.Protocol.Errors;

namespace WinARD.Remote.Protocol.Input;

public sealed class KeyboardInputAdapter : IAsyncDisposable
{
    private readonly KeyEventWriter _writer;
    private readonly List<uint> _pressOrder = [];
    private readonly HashSet<uint> _pressed = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private AdapterState _state = AdapterState.Active;

    public KeyboardInputAdapter(KeyEventWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public IReadOnlyList<uint> PressedKeysyms
    {
        get
        {
            lock (_stateLock)
            {
                return new ReadOnlyCollection<uint>(_pressOrder.ToArray());
            }
        }
    }

    public async ValueTask KeyDownAsync(uint keysym, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            ThrowIfUnavailable();
            await _writer.WriteAsync(true, keysym, cancellationToken).ConfigureAwait(false);
            lock (_stateLock)
            {
                if (_pressed.Add(keysym))
                {
                    _pressOrder.Add(keysym);
                }
            }
        }
        catch
        {
            Fault();
            throw;
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    public async ValueTask KeyUpAsync(uint keysym, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            ThrowIfUnavailable();
            lock (_stateLock)
            {
                if (!_pressed.Contains(keysym))
                {
                    return;
                }
            }

            await _writer.WriteAsync(false, keysym, cancellationToken).ConfigureAwait(false);
            lock (_stateLock)
            {
                _pressed.Remove(keysym);
                _pressOrder.Remove(keysym);
            }
        }
        catch
        {
            Fault();
            throw;
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    public ValueTask OnWindowFocusLostAsync(CancellationToken cancellationToken) =>
        ReleaseAllAsync(cancellationToken);

    public ValueTask OnDisconnectedAsync(CancellationToken cancellationToken) =>
        ReleaseAllAsync(cancellationToken);

    public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            ThrowIfUnavailable();
            while (true)
            {
                uint keysym;
                lock (_stateLock)
                {
                    if (_pressOrder.Count == 0)
                    {
                        break;
                    }

                    keysym = _pressOrder[^1];
                }

                await _writer.WriteAsync(false, keysym, cancellationToken).ConfigureAwait(false);
                lock (_stateLock)
                {
                    _pressOrder.RemoveAt(_pressOrder.Count - 1);
                    _pressed.Remove(keysym);
                }
            }
        }
        catch
        {
            Fault();
            throw;
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_state is AdapterState.Disposing or AdapterState.Disposed)
            {
                return;
            }

            _state = AdapterState.Disposing;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                _pressOrder.Clear();
                _pressed.Clear();
                _state = AdapterState.Disposed;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Fault()
    {
        lock (_stateLock)
        {
            if (_state != AdapterState.Active)
            {
                return;
            }

            _pressOrder.Clear();
            _pressed.Clear();
            _state = AdapterState.Faulted;
        }
    }

    private void ThrowIfUnavailable()
    {
        lock (_stateLock)
        {
            switch (_state)
            {
                case AdapterState.Active:
                    return;
                case AdapterState.Faulted:
                    throw new RfbProtocolException(
                        "The keyboard input adapter is faulted; discard it and the connection.");
                case AdapterState.Disposing:
                case AdapterState.Disposed:
                    ObjectDisposedException.ThrowIf(true, this);
                    return;
                default:
                    throw new InvalidOperationException("Unknown keyboard adapter state.");
            }
        }
    }

    private enum AdapterState
    {
        Active,
        Faulted,
        Disposing,
        Disposed,
    }
}
