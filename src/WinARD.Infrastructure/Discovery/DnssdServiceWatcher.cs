using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Text;

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
    private WatcherRegistration? _registration;
    private long _generation;
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
        WatcherRegistration registration;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registration is not null)
            {
                return Task.CompletedTask;
            }

            var watcher = _factory.Create(
                Aqs,
                RequestedPropertyNames,
                WindowsDeviceInformationKind.AssociationEndpointService);
            registration = CreateRegistration(watcher, ++_generation);
            Attach(registration);
            _registration = registration;
        }

        try
        {
            registration.Watcher.Start();
            return Task.CompletedTask;
        }
        catch
        {
            lock (_gate)
            {
                if (IsCurrent(registration))
                {
                    _registration = null;
                    _generation++;
                }

                Detach(registration);
            }

            registration.Watcher.Dispose();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WatcherRegistration? registration;
        lock (_gate)
        {
            registration = _registration;
            _registration = null;
            _generation++;
            _services.Clear();
            if (registration is not null)
            {
                Detach(registration);
            }
        }

        if (registration is not null)
        {
            registration.Watcher.StopWatching();
            registration.Watcher.Dispose();
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

    private void Apply(WatcherRegistration registration, WindowsDeviceProperties update)
    {
        lock (_gate)
        {
            if (!IsCurrent(registration) || string.IsNullOrWhiteSpace(update.Id) || update.Id.Length > 1024)
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
                if (previousService is not null && previousService.InterfaceIndex != service.InterfaceIndex)
                {
                    Changed?.Invoke(
                        this,
                        BonjourServiceChange.Removed(update.Id, previousService.InterfaceIndex));
                }

                Changed?.Invoke(
                    this,
                    previousService is null
                        ? BonjourServiceChange.Added(service)
                        : BonjourServiceChange.Updated(service));
            }
            else if (previousService is not null)
            {
                Changed?.Invoke(
                    this,
                    BonjourServiceChange.Removed(update.Id, previousService.InterfaceIndex));
            }
        }
    }

    private void OnRemoved(WatcherRegistration registration, string id)
    {
        lock (_gate)
        {
            if (!IsCurrent(registration))
            {
                return;
            }

            if (_services.Remove(id, out var cached) && cached.Service is not null)
            {
                Changed?.Invoke(
                    this,
                    BonjourServiceChange.Removed(id, cached.Service.InterfaceIndex));
            }
        }
    }

    private void OnEnumerationCompleted(WatcherRegistration registration)
    {
        lock (_gate)
        {
            if (!IsCurrent(registration))
            {
                return;
            }

            // Enumeration remains live; subsequent updates and removals continue through DeviceWatcher.
        }
    }

    private void OnStopped(WatcherRegistration registration)
    {
        lock (_gate)
        {
            if (!IsCurrent(registration))
            {
                return;
            }

            _registration = null;
            _generation++;
            Detach(registration);
            var notifications = _services
                .Where(static pair => pair.Value.Service is not null)
                .Select(static pair => BonjourServiceChange.Removed(pair.Key, pair.Value.Service!.InterfaceIndex))
                .ToArray();
            _services.Clear();

            foreach (var notification in notifications)
            {
                Changed?.Invoke(this, notification);
            }
        }

        registration.Watcher.Dispose();
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
            var attributeBytes = Encoding.UTF8.GetByteCount(attribute);
            if (attributeBytes > 255)
            {
                records = result;
                return false;
            }

            total += attributeBytes;
            var separator = attribute.IndexOf('=');
            var key = separator < 0 ? attribute : attribute[..separator];
            var text = separator < 0 ? string.Empty : attribute[(separator + 1)..];
            if (string.IsNullOrEmpty(key) || total > 1300 || result.Count >= 64)
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

    private WatcherRegistration CreateRegistration(IWindowsDeviceWatcher watcher, long generation)
    {
        WatcherRegistration? registration = null;
        EventHandler<WindowsDeviceProperties> added = (_, update) => Apply(registration!, update);
        EventHandler<WindowsDeviceProperties> updated = (_, update) => Apply(registration!, update);
        EventHandler<string> removed = (_, id) => OnRemoved(registration!, id);
        EventHandler enumerationCompleted = (_, _) => OnEnumerationCompleted(registration!);
        EventHandler stopped = (_, _) => OnStopped(registration!);
        registration = new WatcherRegistration(
            watcher,
            generation,
            added,
            updated,
            removed,
            enumerationCompleted,
            stopped);
        return registration;
    }

    private static void Attach(WatcherRegistration registration)
    {
        registration.Watcher.Added += registration.Added;
        registration.Watcher.Updated += registration.Updated;
        registration.Watcher.Removed += registration.Removed;
        registration.Watcher.EnumerationCompleted += registration.EnumerationCompleted;
        registration.Watcher.Stopped += registration.Stopped;
    }

    private static void Detach(WatcherRegistration registration)
    {
        registration.Watcher.Added -= registration.Added;
        registration.Watcher.Updated -= registration.Updated;
        registration.Watcher.Removed -= registration.Removed;
        registration.Watcher.EnumerationCompleted -= registration.EnumerationCompleted;
        registration.Watcher.Stopped -= registration.Stopped;
    }

    private bool IsCurrent(WatcherRegistration registration) =>
        !_disposed &&
        ReferenceEquals(_registration, registration) &&
        _generation == registration.Generation;

    private sealed record CachedService(
        Dictionary<string, object?> Properties,
        BonjourService? Service);

    private sealed record WatcherRegistration(
        IWindowsDeviceWatcher Watcher,
        long Generation,
        EventHandler<WindowsDeviceProperties> Added,
        EventHandler<WindowsDeviceProperties> Updated,
        EventHandler<string> Removed,
        EventHandler EnumerationCompleted,
        EventHandler Stopped);
}
