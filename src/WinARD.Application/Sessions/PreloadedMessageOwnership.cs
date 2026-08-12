using WinARD.Application.Ports;

namespace WinARD.Application.Sessions;

internal sealed class PreloadedMessageOwnership(
    IEnumerable<RemoteServerMessage> messages) : IDisposable
{
    private readonly object _sync = new();
    private IEnumerable<RemoteServerMessage>? _messages =
        messages ?? throw new ArgumentNullException(nameof(messages));
    private List<RemoteServerMessage>? _snapshot;
    private int _disposed;

    public RemoteServerMessage[] Snapshot()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_snapshot is not null)
            {
                return _snapshot.ToArray();
            }

            _snapshot = [];
            foreach (var message in _messages!)
            {
                _snapshot.Add(message);
            }

            return _snapshot.ToArray();
        }
    }

    public void Relinquish()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _messages = null;
            _snapshot = null;
            _disposed = 1;
        }
    }

    public void Dispose()
    {
        List<RemoteServerMessage> owned;
        lock (_sync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            owned = _snapshot ?? [];
            if (_snapshot is null && _messages is not null)
            {
                try
                {
                    owned.AddRange(_messages);
                }
                catch (Exception)
                {
                }
            }

            _messages = null;
            _snapshot = null;
        }

        foreach (var message in owned)
        {
            if (message is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
