using System.Collections.ObjectModel;
using WinARD.Application.Ports;
using WinARD.Infrastructure.Devices;

namespace WinARD.Infrastructure.Discovery;

public sealed class BonjourDeviceDiscovery : IDeviceDiscovery
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly IBonjourServiceWatcher _watcher;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<ServiceKey, ServiceEntry> _services = [];
    private readonly Dictionary<EndpointKey, DiscoveredDevice> _devices = [];
    private ITimer? _timer;
    private bool _started;
    private bool _disposed;

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
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _watcher.Changed += OnWatcherChanged;
            _timer = _timeProvider.CreateTimer(OnSweep, null, SweepInterval, SweepInterval);
        }

        try
        {
            await _watcher.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _started = false;
                _watcher.Changed -= OnWatcherChanged;
                _timer?.Dispose();
                _timer = null;
            }

            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _watcher.Changed -= OnWatcherChanged;
            _timer?.Dispose();
            _timer = null;
            _services.Clear();
            _devices.Clear();
        }

        await _watcher.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _watcher.DisposeAsync().ConfigureAwait(false);
        Changed = null;
    }

    private void OnWatcherChanged(object? sender, BonjourServiceChange change)
    {
        List<DiscoveryChange> notifications;
        lock (_gate)
        {
            if (!_started || _disposed)
            {
                return;
            }

            notifications = ApplyChange(change);
        }

        Publish(notifications);
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
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.Length > 255 || pair.Value.Length > 255)
            {
                return false;
            }

            total += pair.Key.Length + pair.Value.Length;
            if (total > 1300)
            {
                return false;
            }
        }

        return true;
    }

    private void OnSweep(object? state)
    {
        List<DiscoveryChange> notifications;
        lock (_gate)
        {
            if (!_started || _disposed)
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

            notifications = Rebuild(affected);
        }

        Publish(notifications);
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
}
