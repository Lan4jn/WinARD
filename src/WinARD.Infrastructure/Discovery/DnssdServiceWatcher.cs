#if WINARD_DNSSD_DEVICE_WATCHER
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Networking.ServiceDiscovery.Dnssd;

namespace WinARD.Infrastructure.Discovery;

public sealed class DnssdServiceWatcher : IBonjourServiceWatcher
{
    public const string ServiceType = "_rfb._tcp";
    private static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, uint> _interfaces = new(StringComparer.Ordinal);
    private DeviceWatcher? _watcher;
    private bool _disposed;

    public event EventHandler<BonjourServiceChange>? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher is not null)
            {
                return Task.CompletedTask;
            }

            var selector = DnssdServiceInstance.GetDeviceSelector(ServiceType);
            var watcher = DeviceInformation.CreateWatcher(
                selector,
                Array.Empty<string>(),
                DeviceInformationKind.AssociationEndpointService);
            watcher.Added += OnAdded;
            watcher.Updated += OnUpdated;
            watcher.Removed += OnRemoved;
            watcher.Stopped += OnStopped;
            _watcher = watcher;
            watcher.Start();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeviceWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            if (watcher is null)
            {
                return Task.CompletedTask;
            }

            Detach(watcher);
        }

        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            watcher.Stop();
        }

        _interfaces.Clear();
        return Task.CompletedTask;
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
        Changed = null;
    }

    private async void OnAdded(DeviceWatcher sender, DeviceInformation information) =>
        await ResolveAndPublishAsync(information.Id, BonjourServiceChangeKind.Added).ConfigureAwait(false);

    private async void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update) =>
        await ResolveAndPublishAsync(update.Id, BonjourServiceChangeKind.Updated).ConfigureAwait(false);

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (_interfaces.TryRemove(update.Id, out var interfaceIndex))
        {
            Changed?.Invoke(this, BonjourServiceChange.Removed(update.Id, interfaceIndex));
        }
    }

    private void OnStopped(DeviceWatcher sender, object args) => _interfaces.Clear();

    private async Task ResolveAndPublishAsync(string id, BonjourServiceChangeKind kind)
    {
        try
        {
            var instance = await DnssdServiceInstance.FromIdAsync(id);
            if (instance is null)
            {
                return;
            }

            var endpoints = await instance.GetEndpointPairsAsync();
            var interfaceIndex = InterfaceIndex(endpoints.FirstOrDefault()?.LocalHostName?.IPInformation?.NetworkAdapter?.NetworkAdapterId);
            _interfaces[id] = interfaceIndex;
            var txt = instance.TextAttributes.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            var service = new BonjourService(
                id,
                interfaceIndex,
                instance.DnssdServiceInstanceName,
                instance.HostName.CanonicalName,
                instance.Port,
                txt,
                DefaultTimeToLive);
            Changed?.Invoke(
                this,
                kind == BonjourServiceChangeKind.Added
                    ? BonjourServiceChange.Added(service)
                    : BonjourServiceChange.Updated(service));
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or InvalidOperationException or ArgumentException or COMException)
        {
            // A DNS-SD service may disappear while WinRT is resolving it.
        }
    }

    private static uint InterfaceIndex(Guid? networkAdapterId)
    {
        if (networkAdapterId is null)
        {
            return 0;
        }

        Span<byte> bytes = stackalloc byte[16];
        networkAdapterId.Value.TryWriteBytes(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private void Detach(DeviceWatcher watcher)
    {
        watcher.Added -= OnAdded;
        watcher.Updated -= OnUpdated;
        watcher.Removed -= OnRemoved;
        watcher.Stopped -= OnStopped;
    }
}
#else
namespace WinARD.Infrastructure.Discovery;

public sealed class DnssdServiceWatcher : IBonjourServiceWatcher
{
    public const string ServiceType = "_rfb._tcp";
    private const string UnsupportedMessage =
        "The current Windows SDK projection does not expose the DNS-SD DeviceWatcher bridge. " +
        "Build the Windows adapter with WINARD_DNSSD_DEVICE_WATCHER against a compatible SDK projection.";
    private bool _disposed;

    public event EventHandler<BonjourServiceChange>? Changed
    {
        add { }
        remove { }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new PlatformNotSupportedException(UnsupportedMessage);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
#endif
