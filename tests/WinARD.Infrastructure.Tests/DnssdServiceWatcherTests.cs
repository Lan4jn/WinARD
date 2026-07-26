using WinARD.Application.Ports;
using WinARD.Infrastructure.Discovery;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Infrastructure.Tests;

public sealed class DnssdServiceWatcherTests
{
    private static readonly string[] InitialTxt = ["display=one"];
    private static readonly string[] UpdatedTxt = ["display=two", "flag"];

    [Fact]
    public async Task Start_uses_dns_sd_aqs_and_requested_properties_with_real_device_kind()
    {
        var factory = new FakeDeviceWatcherFactory();
        await using var watcher = new DnssdServiceWatcher(factory);

        await watcher.StartAsync(CancellationToken.None);

        Assert.Contains("System.Devices.AepService.ProtocolId:=\"{4526e8c1-8aac-4153-9b16-55e86ada0e54}\"", factory.Aqs, StringComparison.Ordinal);
        Assert.Contains("System.Devices.Dnssd.ServiceName:=\"_rfb._tcp\"", factory.Aqs, StringComparison.Ordinal);
        Assert.Equal(WindowsDeviceInformationKind.AssociationEndpointService, factory.Kind);
        Assert.Contains(DnssdServiceWatcher.InstanceNameProperty, factory.Properties);
        Assert.Contains(DnssdServiceWatcher.ServiceNameProperty, factory.Properties);
        Assert.Contains(DnssdServiceWatcher.DomainProperty, factory.Properties);
        Assert.Contains(DnssdServiceWatcher.HostNameProperty, factory.Properties);
        Assert.Contains(DnssdServiceWatcher.IpAddressProperty, factory.Properties);
        Assert.Contains(DnssdServiceWatcher.PortNumberProperty, factory.Properties);
        Assert.Contains(DnssdServiceWatcher.TextAttributesProperty, factory.Properties);
        Assert.Contains(DnssdServiceWatcher.NetworkAdapterIdProperty, factory.Properties);
    }

    [Fact]
    public void Production_factory_accepts_dns_sd_aqs_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var properties = new[]
        {
            DnssdServiceWatcher.InstanceNameProperty,
            DnssdServiceWatcher.HostNameProperty,
            DnssdServiceWatcher.PortNumberProperty,
        };
        using var watcher = new WindowsDeviceWatcherFactory().Create(
            DnssdServiceWatcher.Aqs,
            properties,
            WindowsDeviceInformationKind.AssociationEndpointService);
    }

    [Fact]
    public async Task Added_partial_update_removed_and_stopped_flow_to_discovered_devices()
    {
        var factory = new FakeDeviceWatcherFactory();
        await using var source = new DnssdServiceWatcher(factory);
        await using var discovery = new BonjourDeviceDiscovery(source, TimeProvider.System);
        var changes = new List<DiscoveryChange>();
        discovery.Changed += (_, change) => changes.Add(change);
        await discovery.StartAsync(CancellationToken.None);

        factory.Watcher.PublishAdded("service-1", CompleteProperties("Studio Mac", "Mac.Local.", 5900));

        var added = Assert.Single(discovery.Current);
        Assert.Equal("mac.local", added.Host);
        Assert.Equal("Studio Mac", added.DisplayName);
        Assert.Equal("one", added.TxtRecords["display"]);

        factory.Watcher.PublishUpdated("service-1", new Dictionary<string, object?>
        {
            [DnssdServiceWatcher.InstanceNameProperty] = "Renamed Mac",
            [DnssdServiceWatcher.TextAttributesProperty] = UpdatedTxt,
        });

        var updated = Assert.Single(discovery.Current);
        Assert.Equal("Renamed Mac", updated.DisplayName);
        Assert.Equal("two", updated.TxtRecords["display"]);
        Assert.Equal(string.Empty, updated.TxtRecords["flag"]);
        Assert.Contains(changes, change => change.Kind == DiscoveryChangeKind.Updated);

        factory.Watcher.PublishEnumerationCompleted();
        Assert.Single(discovery.Current);

        factory.Watcher.PublishRemoved("service-1");
        Assert.Empty(discovery.Current);

        factory.Watcher.PublishAdded("service-2", CompleteProperties("Other", "other.local", (ushort)5900));
        Assert.Single(discovery.Current);
        factory.Watcher.PublishStopped();
        Assert.Empty(discovery.Current);
        factory.Watcher.PublishAdded("late", CompleteProperties("Late", "late.local", 5900));
        Assert.Empty(discovery.Current);
    }

    [Fact]
    public async Task Boxed_property_variants_and_ip_fallback_are_parsed_without_truncation()
    {
        var factory = new FakeDeviceWatcherFactory();
        await using var watcher = new DnssdServiceWatcher(factory);
        var changes = new List<BonjourServiceChange>();
        watcher.Changed += (_, change) => changes.Add(change);
        await watcher.StartAsync(CancellationToken.None);

        var properties = CompleteProperties("IPv6 Mac", host: null, port: "5901");
        properties[DnssdServiceWatcher.IpAddressProperty] = new object[] { "[FD00::1]", "192.0.2.1" };
        properties[DnssdServiceWatcher.NetworkAdapterIdProperty] = "01234567-89ab-cdef-0123-456789abcdef";
        factory.Watcher.PublishAdded("ipv6", properties);

        var service = Assert.Single(changes).Service;
        Assert.NotNull(service);
        Assert.Equal("[FD00::1]", service.Host);
        Assert.Equal(5901, service.Port);
        Assert.NotEqual(0U, service.InterfaceIndex);
    }

    [Fact]
    public async Task Update_that_removes_a_required_property_removes_the_discovered_device()
    {
        var factory = new FakeDeviceWatcherFactory();
        await using var source = new DnssdServiceWatcher(factory);
        await using var discovery = new BonjourDeviceDiscovery(source, TimeProvider.System);
        await discovery.StartAsync(CancellationToken.None);
        factory.Watcher.PublishAdded("service", CompleteProperties("Mac", "mac.local", 5900));
        Assert.Single(discovery.Current);

        factory.Watcher.PublishUpdated("service", new Dictionary<string, object?>
        {
            [DnssdServiceWatcher.PortNumberProperty] = null,
        });

        Assert.Empty(discovery.Current);
    }

    [Fact]
    public void Production_source_has_no_hidden_compile_gate_or_missing_dnssd_instance_api()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "WinARD.Infrastructure", "Discovery", "DnssdServiceWatcher.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("WINARD_DNSSD_DEVICE_WATCHER", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DnssdServiceInstance", source, StringComparison.Ordinal);
        Assert.DoesNotContain("#if", source, StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> CompleteProperties(string name, string? host, object port) =>
        new()
        {
            [DnssdServiceWatcher.ServiceNameProperty] = DnssdServiceWatcher.ServiceType,
            [DnssdServiceWatcher.InstanceNameProperty] = name,
            [DnssdServiceWatcher.DomainProperty] = "local",
            [DnssdServiceWatcher.HostNameProperty] = host,
            [DnssdServiceWatcher.PortNumberProperty] = port,
            [DnssdServiceWatcher.TextAttributesProperty] = InitialTxt,
            [DnssdServiceWatcher.NetworkAdapterIdProperty] = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
        };

    private sealed class FakeDeviceWatcherFactory : IWindowsDeviceWatcherFactory
    {
        public FakeWindowsDeviceWatcher Watcher { get; } = new();
        public string Aqs { get; private set; } = string.Empty;
        public IReadOnlyList<string> Properties { get; private set; } = [];
        public WindowsDeviceInformationKind Kind { get; private set; }

        public IWindowsDeviceWatcher Create(
            string aqs,
            IReadOnlyList<string> requestedProperties,
            WindowsDeviceInformationKind kind)
        {
            Aqs = aqs;
            Properties = requestedProperties;
            Kind = kind;
            return Watcher;
        }
    }

    private sealed class FakeWindowsDeviceWatcher : IWindowsDeviceWatcher
    {
        public event EventHandler<WindowsDeviceProperties>? Added;
        public event EventHandler<WindowsDeviceProperties>? Updated;
        public event EventHandler<string>? Removed;
        public event EventHandler? EnumerationCompleted;
        public event EventHandler? Stopped;

        public void Start()
        {
        }

        public void StopWatching()
        {
        }

        public void PublishAdded(string id, IReadOnlyDictionary<string, object?> properties) =>
            Added?.Invoke(this, new WindowsDeviceProperties(id, properties));

        public void PublishUpdated(string id, IReadOnlyDictionary<string, object?> properties) =>
            Updated?.Invoke(this, new WindowsDeviceProperties(id, properties));

        public void PublishRemoved(string id) => Removed?.Invoke(this, id);

        public void PublishStopped() => Stopped?.Invoke(this, EventArgs.Empty);

        public void PublishEnumerationCompleted() => EnumerationCompleted?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }
}
