using System.Net;
using System.Net.Sockets;
using WinARD.Domain.Connections;
using WinARD.Transport;
using WinARD.Transport.Tcp;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class TcpRemoteTransportTests
{
    [Fact]
    public async Task ConnectAsync_returns_remote_stream_and_disposal_closes_the_socket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();
        var transport = new TcpRemoteTransport();
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "127.0.0.1", port, "operator");

        var connection = await transport.ConnectAsync(profile, CancellationToken.None);
        using var accepted = await acceptTask;

        Assert.Equal("127.0.0.1", connection.EndPoint.Host);
        Assert.Equal(port, connection.EndPoint.Port);
        Assert.True(connection.Stream.CanRead);

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        var buffer = new byte[1];
        Assert.Equal(0, await accepted.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_transport_timeout()
    {
        var resolver = new BlockingResolver();
        var transport = new TcpRemoteTransport(
            new TransportTimeouts(TimeSpan.FromMinutes(1)),
            TimeProvider.System,
            resolver,
            new SystemTcpClientConnector());
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.invalid", 5900, "operator");
        using var cancellation = new CancellationTokenSource();

        var connectTask = transport.ConnectAsync(profile, cancellation.Token);
        await resolver.Started.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask);
        Assert.IsNotType<TransportTimeoutException>(exception);
    }

    [Fact]
    public async Task Dns_deadline_is_reported_as_a_stable_transport_timeout()
    {
        var timeProvider = new ManualTimeProvider();
        var resolver = new BlockingResolver();
        var transport = new TcpRemoteTransport(
            new TransportTimeouts(TimeSpan.FromSeconds(30)),
            timeProvider,
            resolver,
            new SystemTcpClientConnector());
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.invalid", 5900, "operator");

        var connectTask = transport.ConnectAsync(profile, CancellationToken.None);
        await resolver.Started.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        var exception = await Assert.ThrowsAsync<TransportTimeoutException>(() => connectTask);
        Assert.Equal(TransportTimeoutStage.DnsResolution, exception.Stage);
    }

    [Fact]
    public async Task Connect_deadline_is_reported_as_a_stable_transport_timeout()
    {
        var timeProvider = new ManualTimeProvider();
        var connector = new BlockingConnector();
        var transport = new TcpRemoteTransport(
            new TransportTimeouts(TimeSpan.FromSeconds(30)),
            timeProvider,
            new FixedResolver(IPAddress.Loopback),
            connector);
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.invalid", 5900, "operator");

        var connectTask = transport.ConnectAsync(profile, CancellationToken.None);
        await connector.Started.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        var exception = await Assert.ThrowsAsync<TransportTimeoutException>(() => connectTask);
        Assert.Equal(TransportTimeoutStage.Connection, exception.Stage);
    }

    private sealed class BlockingResolver : IHostAddressResolver
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }

    private sealed class FixedResolver(params IPAddress[] addresses) : IHostAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(addresses);
    }

    private sealed class BlockingConnector : ITcpClientConnector
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TcpClient> ConnectAsync(
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, _utcNow + dueTime, period);
            lock (_sync)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            _utcNow += amount;
            ManualTimer[] due;
            lock (_sync)
            {
                due = _timers.Where(timer => timer.IsDue(_utcNow)).ToArray();
            }

            foreach (var timer in due)
            {
                timer.Fire(_utcNow);
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_sync)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAt,
            TimeSpan period) : ITimer
        {
            private bool _disposed;
            private DateTimeOffset _dueAt = dueAt;
            private TimeSpan _period = period;

            public bool IsDue(DateTimeOffset now) => !_disposed && now >= _dueAt;

            public void Fire(DateTimeOffset now)
            {
                callback(state);
                if (_period == Timeout.InfiniteTimeSpan)
                {
                    Dispose();
                }
                else
                {
                    _dueAt = now + _period;
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            {
                _dueAt = owner.GetUtcNow() + dueTime;
                _period = newPeriod;
                return !_disposed;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
