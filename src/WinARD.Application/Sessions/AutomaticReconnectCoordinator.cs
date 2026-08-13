namespace WinARD.Application.Sessions;

public sealed record AutomaticReconnectProgress(long Generation, int Attempt, TimeSpan Remaining);

public sealed class AutomaticReconnectCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<Exception, bool> _isTransient;
    private readonly ReconnectPolicy _policy;
    private readonly Func<CancellationToken, Task> _connect;
    private readonly Action<AutomaticReconnectProgress>? _progress;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _countdownInterval;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private CancellationTokenSource? _loopCancellation;
    private Task<bool>? _loop;
    private Task? _disposeTask;
    private bool _disposed;
    private long _generation;

    public AutomaticReconnectCoordinator(
        Func<Exception, bool> isTransient,
        ReconnectPolicy policy,
        Func<CancellationToken, Task> connect,
        Action<AutomaticReconnectProgress>? progress = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? countdownInterval = null)
    {
        _isTransient = isTransient ?? throw new ArgumentNullException(nameof(isTransient));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _progress = progress;
        _delay = delay ?? Task.Delay;
        _countdownInterval = countdownInterval ?? TimeSpan.FromSeconds(1);
        if (_countdownInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(countdownInterval));
    }

    public bool IsRunning
    {
        get { lock (_sync) return _loop is { IsCompleted: false }; }
    }

    public long Generation
    {
        get { lock (_sync) return _generation; }
    }

    public Task<bool> StartAsync(Exception failure, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!_isTransient(failure)) return Task.FromResult(false);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is { IsCompleted: false }) return _loop;
            _loopCancellation?.Dispose();
            _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            return _loop = RunAsync(failure, ++_generation, _loopCancellation.Token);
        }
    }

    public void Cancel()
    {
        lock (_sync) _loopCancellation?.Cancel();
    }

    public async Task StopAsync()
    {
        Task<bool>? loop;
        lock (_sync)
        {
            _loopCancellation?.Cancel();
            loop = _loop;
        }

        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }
    }

    public async Task ReconnectNowAsync(CancellationToken token)
    {
        Task<bool>? automatic;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loopCancellation?.Cancel();
            automatic = _loop;
        }

        if (automatic is not null)
        {
            await automatic.ConfigureAwait(false);
        }

        await ConnectOnceAsync(token).ConfigureAwait(false);
    }

    private async Task<bool> RunAsync(Exception failure, long generation, CancellationToken token)
    {
        var current = failure;
        for (var index = 0; ; index++)
        {
            if (!_isTransient(current)) return false;
            try
            {
                var remaining = _policy.DelayForAttempt(index);
                while (remaining > TimeSpan.Zero)
                {
                    _progress?.Invoke(new(generation, index + 1, remaining));
                    var slice = remaining < _countdownInterval ? remaining : _countdownInterval;
                    await _delay(slice, token).ConfigureAwait(false);
                    remaining -= slice;
                }
                _progress?.Invoke(new(generation, index + 1, TimeSpan.Zero));
                await ConnectOnceAsync(token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                current = exception;
            }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken token)
    {
        await _connectGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _connect(token).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            _loopCancellation?.Cancel();
            return new ValueTask(_disposeTask = DisposeCoreAsync(_loop));
        }
    }

    private async Task DisposeCoreAsync(Task<bool>? loop)
    {
        if (loop is not null) await loop.ConfigureAwait(false);
        lock (_sync)
        {
            _loopCancellation?.Dispose();
            _loopCancellation = null;
        }
        _connectGate.Dispose();
    }
}
