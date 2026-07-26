namespace WinARD.Application.Sessions;

public sealed class ActiveSessionCoordinator
{
    private readonly object _sync = new();
    private bool _active;

    public ValueTask<ActiveSessionLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_active)
            {
                throw new SessionAlreadyActiveException();
            }

            _active = true;
            return ValueTask.FromResult(new ActiveSessionLease(this));
        }
    }

    private void Release()
    {
        lock (_sync)
        {
            _active = false;
        }
    }

    public sealed class ActiveSessionLease : IAsyncDisposable, IDisposable
    {
        private ActiveSessionCoordinator? _owner;

        internal ActiveSessionLease(ActiveSessionCoordinator owner)
        {
            _owner = owner;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class SessionAlreadyActiveException : InvalidOperationException
{
    public SessionAlreadyActiveException()
        : base("A remote session is already active.")
    {
    }
}
