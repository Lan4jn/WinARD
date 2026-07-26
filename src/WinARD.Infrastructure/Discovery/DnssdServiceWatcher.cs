using System.Buffers.Binary;
using System.Collections;
using System.Globalization;

namespace WinARD.Infrastructure.Discovery;

public sealed class DnssdServiceWatcher : IBonjourServiceWatcher
{
    public const string ServiceType = "_rfb._tcp";
    public const string DnsSdProtocolId = "{4526e8c1-8aac-4153-9b16-55e86ada0e54}";
    public const string ServiceNameProperty = "System.Devices.Dnssd.ServiceName";
    public const string InstanceNameProperty = "System.Devices.Dnssd.InstanceName";
    public const string DomainProperty = "System.Devices.Dnssd.Domain";
    public const string HostNameProperty = "System.Devices.Dnssd.HostName";
    public const string IpAddressProperty = "System.Devices.IpAddress";
    public const string PortNumberProperty = "System.Devices.Dnssd.PortNumber";
    public const string TextAttributesProperty = "System.Devices.Dnssd.TextAttributes";
    public const string NetworkAdapterIdProperty = "System.Devices.Dnssd.NetworkAdapterId";

    public const string Aqs =
        "System.Devices.AepService.ProtocolId:=\"" + DnsSdProtocolId + "\" AND " +
        ServiceNameProperty + ":=\"" + ServiceType + "\"";

    private static readonly string[] RequestedPropertyNames =
    [
        ServiceNameProperty,
        InstanceNameProperty,
        DomainProperty,
        HostNameProperty,
        IpAddressProperty,
        PortNumberProperty,
        TextAttributesProperty,
        NetworkAdapterIdProperty,
    ];

    private static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly IWindowsDeviceWatcherFactory _factory;
    private readonly Dictionary<string, CachedService> _services = new(StringComparer.Ordinal);
    private IWindowsDeviceWatcher? _watcher;
    private bool _disposed;

    public DnssdServiceWatcher()
        : this(new WindowsDeviceWatcherFactory())
    {
    }

    public DnssdServiceWatcher(IWindowsDeviceWatcherFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public event EventHandler<BonjourServiceChange>? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IWindowsDeviceWatcher watcher;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher is not null)
            {
                return Task.CompletedTask;
            }

            watcher = _factory.Create(
                Aqs,
                RequestedPropertyNames,
                WindowsDeviceInformationKind.AssociationEndpointService);
            Attach(watcher);
            _watcher = watcher;
        }

        try
        {
            watcher.Start();
            return Task.CompletedTask;
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_watcher, watcher))
                {
                    _watcher = null;
                }

                Detach(watcher);
            }

            watcher.Dispose();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IWindowsDeviceWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            _services.Clear();
            if (watcher is not null)
            {
                Detach(watcher);
            }
        }

        if (watcher is not null)
        {
            watcher.StopWatching();
            watcher.Dispose();
        }

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

    private void OnAdded(object? sender, WindowsDeviceProperties update) => Apply(update);

    private void OnUpdated(object? sender, WindowsDeviceProperties update) => Apply(update);

    private void Apply(WindowsDeviceProperties update)
    {
        BonjourServiceChange? notification = null;
        lock (_gate)
        {
            if (_disposed || _watcher is null || string.IsNullOrWhiteSpace(update.Id) || update.Id.Length > 1024)
            {
                return;
            }

            if (!_services.TryGetValue(update.Id, out var cached))
            {
                cached = new CachedService(new Dictionary<string, object?>(StringComparer.Ordinal), null);
            }

            var previousService = cached.Service;
            foreach (var property in update.Properties)
            {
                if (property.Value is null)
                {
                    cached.Properties.Remove(property.Key);
                }
                else
                {
                    cached.Properties[property.Key] = property.Value;
                }
            }

            var service = TryParse(update.Id, cached.Properties);
            _services[update.Id] = cached with { Service = service };
            if (service is not null)
            {
                notification = previousService is null
                    ? BonjourServiceChange.Added(service)
                    : BonjourServiceChange.Updated(service);
            }
            else if (previousService is not null)
            {
                notification = BonjourServiceChange.Removed(update.Id, previousService.InterfaceIndex);
            }
        }

        if (notification is not null)
        {
            Changed?.Invoke(this, notification);
        }
    }

    private void OnRemoved(object? sender, string id)
    {
        BonjourServiceChange? notification = null;
        lock (_gate)
        {
            if (_services.Remove(id, out var cached) && cached.Service is not null)
            {
                notification = BonjourServiceChange.Removed(id, cached.Service.InterfaceIndex);
            }
        }

        if (notification is not null)
        {
            Changed?.Invoke(this, notification);
        }
    }

    private void OnEnumerationCompleted(object? sender, EventArgs args)
    {
        // Enumeration remains live; subsequent updates and removals continue through DeviceWatcher.
    }

    private void OnStopped(object? sender, EventArgs args)
    {
        BonjourServiceChange[] notifications;
        IWindowsDeviceWatcher? stoppedWatcher = null;
        lock (_gate)
        {
            if (sender is IWindowsDeviceWatcher watcher && ReferenceEquals(_watcher, watcher))
            {
                stoppedWatcher = watcher;
                _watcher = null;
                Detach(watcher);
            }

            notifications = _services
                .Where(static pair => pair.Value.Service is not null)
                .Select(static pair => BonjourServiceChange.Removed(pair.Key, pair.Value.Service!.InterfaceIndex))
                .ToArray();
            _services.Clear();
        }

        foreach (var notification in notifications)
        {
            Changed?.Invoke(this, notification);
        }

        stoppedWatcher?.Dispose();
    }

    private static BonjourService? TryParse(string id, IReadOnlyDictionary<string, object?> properties)
    {
        if (!TryString(properties, ServiceNameProperty, out var serviceName) ||
            !string.Equals(serviceName, ServiceType, StringComparison.Ordinal) ||
            !TryString(properties, InstanceNameProperty, out var instanceName) || instanceName.Length > 255 ||
            !TryPort(properties, out var port) ||
            !TryHost(properties, out var host) ||
            !TryTxt(properties, out var txtRecords))
        {
            return null;
        }

        return new BonjourService(
            id,
            InterfaceIndex(properties),
            instanceName,
            host,
            port,
            txtRecords,
            DefaultTimeToLive);
    }

    private static bool TryHost(IReadOnlyDictionary<string, object?> properties, out string host)
    {
        if (TryString(properties, HostNameProperty, out host))
        {
            return host.Length <= 255;
        }

        if (properties.TryGetValue(IpAddressProperty, out var value))
        {
            host = Strings(value).FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty;
            return host.Length is > 0 and <= 255;
        }

        host = string.Empty;
        return false;
    }

    private static bool TryPort(IReadOnlyDictionary<string, object?> properties, out int port)
    {
        port = 0;
        if (!properties.TryGetValue(PortNumberProperty, out var value) || value is null)
        {
            return false;
        }

        switch (value)
        {
            case ushort unsignedShort:
                port = unsignedShort;
                break;
            case short signedShort when signedShort > 0:
                port = signedShort;
                break;
            case uint unsignedInteger when unsignedInteger <= 65535:
                port = (int)unsignedInteger;
                break;
            case int integer:
                port = integer;
                break;
            case string text when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed):
                port = parsed;
                break;
        }

        return port is >= 1 and <= 65535;
    }

    private static bool TryTxt(
        IReadOnlyDictionary<string, object?> properties,
        out IReadOnlyDictionary<string, string> records)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!properties.TryGetValue(TextAttributesProperty, out var value) || value is null)
        {
            records = result;
            return true;
        }

        var total = 0;
        foreach (var attribute in Strings(value))
        {
            if (attribute.Length > 255)
            {
                records = result;
                return false;
            }

            total += attribute.Length;
            var separator = attribute.IndexOf('=');
            var key = separator < 0 ? attribute : attribute[..separator];
            var text = separator < 0 ? string.Empty : attribute[(separator + 1)..];
            if (string.IsNullOrEmpty(key) || key.Length > 255 || text.Length > 255 || total > 1300 || result.Count >= 64)
            {
                records = result;
                return false;
            }

            result[key] = text;
        }

        records = result;
        return true;
    }

    private static IEnumerable<string> Strings(object? value)
    {
        if (value is string text)
        {
            yield return text;
            yield break;
        }

        if (value is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            if (item is string itemText)
            {
                yield return itemText;
            }
        }
    }

    private static uint InterfaceIndex(IReadOnlyDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue(NetworkAdapterIdProperty, out var value) ||
            !TryGuid(value, out var adapterId))
        {
            return 0;
        }

        Span<byte> bytes = stackalloc byte[16];
        adapterId.TryWriteBytes(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static bool TryGuid(object? value, out Guid guid)
    {
        if (value is Guid typed)
        {
            guid = typed;
            return true;
        }

        return Guid.TryParse(value as string, out guid);
    }

    private static bool TryString(
        IReadOnlyDictionary<string, object?> properties,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!properties.TryGetValue(propertyName, out var boxed) || boxed is not string text)
        {
            return false;
        }

        value = text.Trim();
        return value.Length > 0;
    }

    private void Attach(IWindowsDeviceWatcher watcher)
    {
        watcher.Added += OnAdded;
        watcher.Updated += OnUpdated;
        watcher.Removed += OnRemoved;
        watcher.EnumerationCompleted += OnEnumerationCompleted;
        watcher.Stopped += OnStopped;
    }

    private void Detach(IWindowsDeviceWatcher watcher)
    {
        watcher.Added -= OnAdded;
        watcher.Updated -= OnUpdated;
        watcher.Removed -= OnRemoved;
        watcher.EnumerationCompleted -= OnEnumerationCompleted;
        watcher.Stopped -= OnStopped;
    }

    private sealed record CachedService(
        Dictionary<string, object?> Properties,
        BonjourService? Service);
}
