using System.Collections.ObjectModel;
using System.Text;
using WinARD.Application.Ports;
using WinARD.Infrastructure.Devices;

namespace WinARD.Infrastructure.Discovery;

public sealed class BonjourDeviceDiscovery : IDeviceDiscovery
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly IBonjourServiceWatcher _watcher;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<ServiceKey, ServiceEntry> _services = [];
    private readonly Dictionary<EndpointKey, DiscoveredDevice> _devices = [];
    private ITimer? _timer;
    private EventHandler<BonjourServiceChange>? _watcherHandler;
    private LifecycleState _state;
    private long _generation;
    private bool _disposeRequested;
    private Task? _disposeTask;

    public BonjourDeviceDiscovery(IBonjourServiceWatcher watcher, TimeProvider? timeProvider = null)
    {
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<DiscoveryChange>? Changed;

    public IReadOnlyList<DiscoveredDevice> Current
    {
        get
        {
            lock (_gate)
            {
                return _devices.Values.OrderBy(static device => device.Identity, StringComparer.Ordinal).ToArray();
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long generation;
            EventHandler<BonjourServiceChange> handler;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeRequested || _state == LifecycleState.Disposed, this);
                if (_state == LifecycleState.Started)
                {
                    return;
                }

                _state = LifecycleState.Starting;
                generation = ++_generation;
                handler = (_, change) => OnWatcherChanged(generation, change);
                _watcherHandler = handler;
                _watcher.Changed += handler;
                _timer = _timeProvider.CreateTimer(OnSweep, generation, SweepInterval, SweepInterval);
            }

            try
            {
                await _watcher.StartAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_generation == generation && _state == LifecycleState.Starting)
                    {
                        _state = LifecycleState.Started;
                    }
                }
            }
            catch
            {
                lock (_gate)
                {
                    InvalidateGeneration(handler);
                    _state = LifecycleState.Idle;
                }

                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_state != LifecycleState.Started)
                {
                    return;
                }

                _state = LifecycleState.Stopping;
                InvalidateGeneration(_watcherHandler);
                _services.Clear();
                _devices.Clear();
            }

            try
            {
                await _watcher.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _state = LifecycleState.Idle;
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        Task disposeTask;
        lock (_gate)
        {
            _disposeRequested = true;
            if (_disposeTask is null)
            {
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
            }

            disposeTask = _disposeTask;
        }

        if (completion is not null)
        {
            _ = CompleteDisposeAsync(completion);
        }

        return new ValueTask(disposeTask);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var stop = false;
            lock (_gate)
            {
                stop = _state == LifecycleState.Started;
                _state = LifecycleState.Stopping;
                InvalidateGeneration(_watcherHandler);
                _services.Clear();
                _devices.Clear();
            }

            try
            {
                if (stop)
                {
                    await _watcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                await _watcher.DisposeAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _state = LifecycleState.Disposed;
                    Changed = null;
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void OnWatcherChanged(long generation, BonjourServiceChange change)
    {
        lock (_gate)
        {
            if (_state != LifecycleState.Started || generation != _generation)
            {
                return;
            }

            Publish(ApplyChange(change));
        }
    }

    private List<DiscoveryChange> ApplyChange(BonjourServiceChange change)
    {
        var affected = new HashSet<EndpointKey>();
        var serviceKey = new ServiceKey(change.ServiceIdentity, change.InterfaceIndex);
        if (_services.Remove(serviceKey, out var previous))
        {
            affected.Add(previous.Endpoint);
        }

        if (change.Kind != BonjourServiceChangeKind.Removed && TryNormalize(change.Service, out var entry))
        {
            _services[serviceKey] = entry;
            affected.Add(entry.Endpoint);
        }

        return Rebuild(affected);
    }

    private List<DiscoveryChange> Rebuild(IEnumerable<EndpointKey> affected)
    {
        var notifications = new List<DiscoveryChange>();
        foreach (var endpoint in affected)
        {
            var source = _services
                .Where(pair => pair.Value.Endpoint == endpoint)
                .OrderBy(pair => pair.Key.Identity, StringComparer.Ordinal)
                .ThenBy(static pair => pair.Key.InterfaceIndex)
                .Select(static pair => pair.Value)
                .FirstOrDefault();
            if (source is null)
            {
                if (_devices.Remove(endpoint, out var removed))
                {
                    notifications.Add(new DiscoveryChange(DiscoveryChangeKind.Removed, removed));
                }

                continue;
            }

            var device = new DiscoveredDevice(
                endpoint.ToString(),
                source.DisplayName,
                endpoint.Host,
                endpoint.Port,
                source.TxtRecords,
                source.ExpiresUtc);
            if (!_devices.TryGetValue(endpoint, out var existing))
            {
                _devices[endpoint] = device;
                notifications.Add(new DiscoveryChange(DiscoveryChangeKind.Added, device));
            }
            else if (existing != device)
            {
                _devices[endpoint] = device;
                notifications.Add(new DiscoveryChange(DiscoveryChangeKind.Updated, device));
            }
        }

        return notifications;
    }

    private bool TryNormalize(BonjourService? service, out ServiceEntry entry)
    {
        entry = null!;
        if (service is null ||
            string.IsNullOrWhiteSpace(service.ServiceIdentity) || service.ServiceIdentity.Length > 255 ||
            string.IsNullOrWhiteSpace(service.DisplayName) || service.DisplayName.Length > 255 ||
            service.Port is < 1 or > 65535 ||
            service.TimeToLive <= TimeSpan.Zero || service.TimeToLive > TimeSpan.FromDays(1) ||
            !ValidTxt(service.TxtRecords))
        {
            return false;
        }

        try
        {
            var endpoint = new EndpointKey(HostCanonicalizer.Canonicalize(service.Host), service.Port);
            entry = new ServiceEntry(
                endpoint,
                service.DisplayName.Trim(),
                new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(service.TxtRecords, StringComparer.Ordinal)),
                _timeProvider.GetUtcNow() + service.TimeToLive);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool ValidTxt(IReadOnlyDictionary<string, string> records)
    {
        if (records.Count > 64)
        {
            return false;
        }

        var total = 0;
        foreach (var pair in records)
        {
            if (string.IsNullOrEmpty(pair.Key))
            {
                return false;
            }

            var entryBytes = Encoding.UTF8.GetByteCount(pair.Key) + 1 + Encoding.UTF8.GetByteCount(pair.Value);
            if (entryBytes > 255)
            {
                return false;
            }

            total += entryBytes;
            if (total > 1300)
            {
                return false;
            }
        }

        return true;
    }

    private void OnSweep(object? state)
    {
        var generation = (long)state!;
        lock (_gate)
        {
            if (_state != LifecycleState.Started || generation != _generation)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            var expired = _services.Where(pair => pair.Value.ExpiresUtc <= now).Select(static pair => pair.Key).ToArray();
            var affected = new HashSet<EndpointKey>();
            foreach (var key in expired)
            {
                affected.Add(_services[key].Endpoint);
                _services.Remove(key);
            }

            Publish(Rebuild(affected));
        }
    }

    private void InvalidateGeneration(EventHandler<BonjourServiceChange>? handler)
    {
        _generation++;
        if (handler is not null)
        {
            _watcher.Changed -= handler;
        }

        _watcherHandler = null;
        _timer?.Dispose();
        _timer = null;
    }

    private void Publish(IEnumerable<DiscoveryChange> notifications)
    {
        foreach (var notification in notifications)
        {
            Changed?.Invoke(this, notification);
        }
    }

    private readonly record struct ServiceKey(string Identity, uint InterfaceIndex);

    private readonly record struct EndpointKey(string Host, int Port)
    {
        public override string ToString() => $"{Host}:{Port}";
    }

    private sealed record ServiceEntry(
        EndpointKey Endpoint,
        string DisplayName,
        IReadOnlyDictionary<string, string> TxtRecords,
        DateTimeOffset ExpiresUtc);

    private enum LifecycleState
    {
        Idle,
        Starting,
        Started,
        Stopping,
        Disposed,
    }
}
