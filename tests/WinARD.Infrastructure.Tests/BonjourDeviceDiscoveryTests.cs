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
        watcher.Publish(BonjourServiceChange.Added(Service("multibyte-txt", 1, "mac.local", 5900) with
        {
            TxtRecords = new Dictionary<string, string> { ["name"] = new string('\u754c', 128) },
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

    [Fact]
    public async Task Ttl_renewal_prevents_stale_expiry_and_eventually_expires()
    {
        var watcher = new FakeBonjourWatcher();
        using var time = new ManualTimeProvider();
        await using var discovery = new BonjourDeviceDiscovery(watcher, time);
        await discovery.StartAsync(CancellationToken.None);
        var service = Service("mac", 1, "mac.local", 5900) with { TimeToLive = TimeSpan.FromMinutes(1) };
        watcher.Publish(BonjourServiceChange.Added(service));

        time.Advance(TimeSpan.FromSeconds(45));
        watcher.Publish(BonjourServiceChange.Updated(service));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(discovery.Current);

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Empty(discovery.Current);
    }

    [Fact]
    public async Task Stop_waits_for_an_in_flight_start_before_stopping_the_watcher()
    {
        var watcher = new ControllableBonjourWatcher();
        var firstStart = watcher.EnqueueStart();
        await using var discovery = new BonjourDeviceDiscovery(watcher, TimeProvider.System);

        var startTask = discovery.StartAsync(CancellationToken.None);
        await firstStart.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopTask = discovery.StopAsync(CancellationToken.None);

        Assert.False(stopTask.IsCompleted);
        Assert.Equal(0, watcher.StopCount);
        firstStart.Completion.SetResult();
        await startTask;
        await stopTask;
        Assert.Equal(1, watcher.StopCount);
    }

    [Fact]
    public async Task Second_start_retries_after_the_first_start_fails()
    {
        var watcher = new ControllableBonjourWatcher();
        var firstStart = watcher.EnqueueStart();
        watcher.EnqueueCompletedStart();
        await using var discovery = new BonjourDeviceDiscovery(watcher, TimeProvider.System);

        var failedStart = discovery.StartAsync(CancellationToken.None);
        await firstStart.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retryStart = discovery.StartAsync(CancellationToken.None);
        Assert.False(retryStart.IsCompleted);

        firstStart.Completion.SetException(new InvalidOperationException("fixture start failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failedStart);
        await retryStart;
        Assert.Equal(2, watcher.StartCount);
    }

    [Fact]
    public async Task Dispose_waits_for_start_then_stops_and_disposes_once()
    {
        var watcher = new ControllableBonjourWatcher();
        var firstStart = watcher.EnqueueStart();
        var discovery = new BonjourDeviceDiscovery(watcher, TimeProvider.System);

        var startTask = discovery.StartAsync(CancellationToken.None);
        await firstStart.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstDispose = discovery.DisposeAsync().AsTask();
        var secondDispose = discovery.DisposeAsync().AsTask();

        Assert.False(firstDispose.IsCompleted);
        Assert.Same(firstDispose, secondDispose);
        Assert.Equal(0, watcher.StopCount);
        Assert.Equal(0, watcher.DisposeCount);
        firstStart.Completion.SetResult();
        await startTask;
        await firstDispose;
        Assert.Equal(1, watcher.StopCount);
        Assert.Equal(1, watcher.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => discovery.StartAsync(CancellationToken.None));
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

    private sealed class ControllableBonjourWatcher : IBonjourServiceWatcher
    {
        private readonly Queue<StartOperation> _starts = new();

        public event EventHandler<BonjourServiceChange>? Changed;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public StartOperation EnqueueStart()
        {
            var operation = new StartOperation();
            _starts.Enqueue(operation);
            return operation;
        }

        public void EnqueueCompletedStart()
        {
            var operation = EnqueueStart();
            operation.Completion.SetResult();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            var operation = _starts.Dequeue();
            operation.Entered.SetResult();
            await operation.Completion.Task.WaitAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return Task.CompletedTask;
        }

        public void Publish(BonjourServiceChange change) => Changed?.Invoke(this, change);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Changed = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StartOperation
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManualTimeProvider : TimeProvider, IDisposable
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private ManualTimer? _timer;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new ManualTimer(callback, state, _utcNow + dueTime, period);
            return _timer;
        }

        public void Advance(TimeSpan amount)
        {
            _utcNow += amount;
            _timer?.FireIfDue(_utcNow);
        }

        public void Dispose() => _timer?.Dispose();

        private sealed class ManualTimer(
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAt,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset _dueAt = dueAt;
            private bool _disposed;

            public void FireIfDue(DateTimeOffset now)
            {
                if (_disposed || now < _dueAt)
                {
                    return;
                }

                callback(state);
                _dueAt = now + period;
            }

            public bool Change(TimeSpan dueTime, TimeSpan newPeriod) => !_disposed;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
