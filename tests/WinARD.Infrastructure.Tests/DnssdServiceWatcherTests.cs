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
    public async Task Txt_attributes_over_255_utf8_bytes_are_rejected()
    {
        var factory = new FakeDeviceWatcherFactory();
        await using var watcher = new DnssdServiceWatcher(factory);
        var changes = new List<BonjourServiceChange>();
        watcher.Changed += (_, change) => changes.Add(change);
        await watcher.StartAsync(CancellationToken.None);
        var properties = CompleteProperties("Mac", "mac.local", 5900);
        properties[DnssdServiceWatcher.TextAttributesProperty] = new[] { $"name={new string('\u754c', 128)}" };

        factory.Watcher.PublishAdded("service", properties);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task Events_queued_by_an_old_watcher_generation_do_not_touch_the_new_generation()
    {
        var factory = new FakeDeviceWatcherFactory();
        await using var source = new DnssdServiceWatcher(factory);
        await using var discovery = new BonjourDeviceDiscovery(source, TimeProvider.System);
        await discovery.StartAsync(CancellationToken.None);
        var oldWatcher = factory.Watcher;
        var oldAdded = oldWatcher.CaptureAdded("old", CompleteProperties("Old", "old.local", 5900));
        var oldUpdated = oldWatcher.CaptureUpdated("new", new Dictionary<string, object?>
        {
            [DnssdServiceWatcher.InstanceNameProperty] = "Corrupted",
        });
        var oldRemoved = oldWatcher.CaptureRemoved("new");
        var oldEnumerationCompleted = oldWatcher.CaptureEnumerationCompleted();
        var oldStopped = oldWatcher.CaptureStopped();

        await discovery.StopAsync(CancellationToken.None);
        await discovery.StartAsync(CancellationToken.None);
        factory.Watcher.PublishAdded("new", CompleteProperties("New", "new.local", 5900));

        oldAdded();
        oldUpdated();
        oldRemoved();
        oldEnumerationCompleted();
        oldStopped();

        var current = Assert.Single(discovery.Current);
        Assert.Equal("New", current.DisplayName);
        Assert.Equal("new.local", current.Host);
    }

    [Fact]
    public async Task Interface_changes_remove_the_old_identity_before_publishing_the_new_one()
    {
        var factory = new FakeDeviceWatcherFactory();
        await using var watcher = new DnssdServiceWatcher(factory);
        var changes = new List<BonjourServiceChange>();
        watcher.Changed += (_, change) => changes.Add(change);
        await watcher.StartAsync(CancellationToken.None);
        var properties = CompleteProperties("Mac", "mac.local", 5900);
        properties[DnssdServiceWatcher.NetworkAdapterIdProperty] = Guid.Parse("11111111-1111-1111-1111-111111111111");
        factory.Watcher.PublishAdded("service", properties);
        var firstInterface = Assert.Single(changes).InterfaceIndex;

        factory.Watcher.PublishUpdated("service", new Dictionary<string, object?>
        {
            [DnssdServiceWatcher.NetworkAdapterIdProperty] = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        });
        factory.Watcher.PublishUpdated("service", new Dictionary<string, object?>
        {
            [DnssdServiceWatcher.NetworkAdapterIdProperty] = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        });

        Assert.Equal(BonjourServiceChangeKind.Removed, changes[1].Kind);
        Assert.Equal(firstInterface, changes[1].InterfaceIndex);
        Assert.Equal(BonjourServiceChangeKind.Updated, changes[2].Kind);
        Assert.Equal(BonjourServiceChangeKind.Removed, changes[3].Kind);
        Assert.Equal(changes[2].InterfaceIndex, changes[3].InterfaceIndex);
        Assert.Equal(BonjourServiceChangeKind.Updated, changes[4].Kind);
    }

    [Fact]
    public async Task Rapid_interface_changes_then_remove_leave_no_ghost_device()
    {
        var factory = new FakeDeviceWatcherFactory();
        await using var source = new DnssdServiceWatcher(factory);
        await using var discovery = new BonjourDeviceDiscovery(source, TimeProvider.System);
        await discovery.StartAsync(CancellationToken.None);
        var properties = CompleteProperties("Mac", "mac.local", 5900);
        properties[DnssdServiceWatcher.NetworkAdapterIdProperty] = Guid.Parse("11111111-1111-1111-1111-111111111111");
        factory.Watcher.PublishAdded("service", properties);
        factory.Watcher.PublishUpdated("service", new Dictionary<string, object?>
        {
            [DnssdServiceWatcher.NetworkAdapterIdProperty] = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        });
        factory.Watcher.PublishUpdated("service", new Dictionary<string, object?>
        {
            [DnssdServiceWatcher.NetworkAdapterIdProperty] = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        });

        factory.Watcher.PublishRemoved("service");

        Assert.Empty(discovery.Current);
    }

    [Fact]
    public void Production_source_has_no_hidden_compile_gate_or_missing_dnssd_instance_api()
    {
        var sourcePath = RepositoryFile("src", "WinARD.Infrastructure", "Discovery", "DnssdServiceWatcher.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("WINARD_DNSSD_DEVICE_WATCHER", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DnssdServiceInstance", source, StringComparison.Ordinal);
        Assert.DoesNotContain("#if", source, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException("Could not locate repository root.")
            : Path.Combine([directory.FullName, .. segments]);
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
        private readonly List<FakeWindowsDeviceWatcher> _watchers = [];

        public FakeWindowsDeviceWatcher Watcher => _watchers[^1];
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
            var watcher = new FakeWindowsDeviceWatcher();
            _watchers.Add(watcher);
            return watcher;
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

        public Action CaptureAdded(string id, IReadOnlyDictionary<string, object?> properties)
        {
            var handler = Added;
            return () => handler?.Invoke(this, new WindowsDeviceProperties(id, properties));
        }

        public Action CaptureUpdated(string id, IReadOnlyDictionary<string, object?> properties)
        {
            var handler = Updated;
            return () => handler?.Invoke(this, new WindowsDeviceProperties(id, properties));
        }

        public Action CaptureRemoved(string id)
        {
            var handler = Removed;
            return () => handler?.Invoke(this, id);
        }

        public Action CaptureEnumerationCompleted()
        {
            var handler = EnumerationCompleted;
            return () => handler?.Invoke(this, EventArgs.Empty);
        }

        public Action CaptureStopped()
        {
            var handler = Stopped;
            return () => handler?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }
}
