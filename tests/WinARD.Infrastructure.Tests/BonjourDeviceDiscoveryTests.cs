using WinARD.Application.Ports;
using WinARD.Infrastructure.Discovery;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Infrastructure.Tests;

public sealed class BonjourDeviceDiscoveryTests
{
    [Fact]
    public void Production_watcher_targets_rfb_tcp()
    {
        Assert.Equal("_rfb._tcp", DnssdServiceWatcher.ServiceType);
    }

    [Fact]
    public async Task Services_with_same_canonical_endpoint_are_deduplicated_until_last_identity_is_removed()
    {
        var watcher = new FakeBonjourWatcher();
        await using var discovery = new BonjourDeviceDiscovery(watcher, TimeProvider.System);
        var changes = new List<DiscoveryChange>();
        discovery.Changed += (_, change) => changes.Add(change);
        await discovery.StartAsync(CancellationToken.None);

        watcher.Publish(BonjourServiceChange.Added(Service("one", 4, "BÜCHER.local.", 5900)));
        watcher.Publish(BonjourServiceChange.Added(Service("two", 7, "xn--bcher-kva.local", 5900)));
        watcher.Publish(BonjourServiceChange.Removed("missing", 9));
        watcher.Publish(BonjourServiceChange.Removed("one", 4));

        Assert.Single(discovery.Current);
        Assert.Equal("xn--bcher-kva.local", discovery.Current[0].Host);
        Assert.Single(changes, change => change.Kind == DiscoveryChangeKind.Added);
        Assert.DoesNotContain(changes, change => change.Kind == DiscoveryChangeKind.Removed);

        watcher.Publish(BonjourServiceChange.Removed("two", 7));
        Assert.Empty(discovery.Current);
        Assert.Single(changes, change => change.Kind == DiscoveryChangeKind.Removed);
    }

    [Fact]
    public async Task Start_stop_and_dispose_are_idempotent_and_ignore_late_events()
    {
        var watcher = new FakeBonjourWatcher();
        var discovery = new BonjourDeviceDiscovery(watcher, TimeProvider.System);
        await discovery.StartAsync(CancellationToken.None);
        await discovery.StartAsync(CancellationToken.None);
        await discovery.StopAsync(CancellationToken.None);
        await discovery.StopAsync(CancellationToken.None);
        await discovery.DisposeAsync();
        await discovery.DisposeAsync();

        watcher.Publish(BonjourServiceChange.Added(Service("late", 1, "late.local", 5900)));

        Assert.Equal(1, watcher.StartCount);
        Assert.Equal(1, watcher.StopCount);
        Assert.True(watcher.Disposed);
        Assert.Empty(discovery.Current);
    }

    [Fact]
    public async Task Invalid_or_oversized_service_data_is_discarded()
    {
        var watcher = new FakeBonjourWatcher();
        await using var discovery = new BonjourDeviceDiscovery(watcher, TimeProvider.System);
        await discovery.StartAsync(CancellationToken.None);

        watcher.Publish(BonjourServiceChange.Added(Service("bad-port", 1, "mac.local", 0)));
        watcher.Publish(BonjourServiceChange.Added(Service("bad-host", 1, new string('a', 254), 5900)));
        watcher.Publish(BonjourServiceChange.Added(Service("bad-txt", 1, "mac.local", 5900) with
        {
            TxtRecords = new Dictionary<string, string> { ["oversized"] = new string('x', 256) },
        }));

        Assert.Empty(discovery.Current);
    }

    [Fact]
    public async Task Discovered_device_txt_records_are_immutable()
    {
        var watcher = new FakeBonjourWatcher();
        await using var discovery = new BonjourDeviceDiscovery(watcher, TimeProvider.System);
        await discovery.StartAsync(CancellationToken.None);
        watcher.Publish(BonjourServiceChange.Added(Service("mac", 1, "mac.local", 5900) with
        {
            TxtRecords = new Dictionary<string, string> { ["name"] = "Studio" },
        }));

        var records = Assert.IsAssignableFrom<IDictionary<string, string>>(Assert.Single(discovery.Current).TxtRecords);
        Assert.Throws<NotSupportedException>(() => records.Add("mutable", "no"));
    }

    private static BonjourService Service(string id, uint interfaceIndex, string host, int port) =>
        new(id, interfaceIndex, id, host, port, new Dictionary<string, string>(), TimeSpan.FromMinutes(2));

    private sealed class FakeBonjourWatcher : IBonjourServiceWatcher
    {
        public event EventHandler<BonjourServiceChange>? Changed;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool Disposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void Publish(BonjourServiceChange change) => Changed?.Invoke(this, change);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
