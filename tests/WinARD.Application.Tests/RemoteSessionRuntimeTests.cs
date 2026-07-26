using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707, CA1822

namespace WinARD.Application.Tests;

public sealed class RemoteSessionRuntimeTests
{
    [Fact]
    public async Task Controlled_runtime_api_forwards_without_exposing_transport()
    {
        var client = new RuntimeClient
        {
            NextMessage = new RemoteClipboardMessage("remote text"),
        };
        await using var session = await ConnectAsync(client);

        await session.RequestFramebufferUpdateAsync(incremental: true, CancellationToken.None);
        var message = await session.ReceiveAsync(CancellationToken.None);
        await session.SendPointerAsync(3, 12, 34, CancellationToken.None);
        await session.SendKeyAsync(0xffe3, true, CancellationToken.None);
        await session.SendClipboardTextAsync("local text", CancellationToken.None);

        Assert.Equal(new RemoteFramebufferSize(640, 480), session.FramebufferSize);
        Assert.IsType<RemoteClipboardMessage>(message);
        Assert.True(client.LastIncremental);
        Assert.Equal((3, 12, 34), client.LastPointer);
        Assert.Equal((0xffe3u, true), client.LastKey);
        Assert.Equal("local text", client.LastClipboard);
    }

    [Fact]
    public async Task Disconnect_is_idempotent_and_runtime_calls_after_disconnect_fail()
    {
        var client = new RuntimeClient();
        var session = await ConnectAsync(client);

        await Task.WhenAll(
            session.DisconnectAsync().AsTask(),
            session.DisconnectAsync().AsTask());

        Assert.Equal(1, client.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.SendKeyAsync(0x61, true, CancellationToken.None));
    }

    [Fact]
    public async Task Disconnect_interrupts_a_receive_blocked_on_the_transport()
    {
        var stream = new DisposeReleasesReadStream();
        var client = new BlockingReceiveClient(stream);
        var session = await ConnectAsync(client, stream);
        var receive = session.ReceiveAsync(CancellationToken.None).AsTask();
        await client.ReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var disconnect = session.DisconnectAsync().AsTask();
        Task completed;
        try
        {
            completed = await Task.WhenAny(disconnect, Task.Delay(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            await stream.DisposeAsync();
            await disconnect.WaitAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Same(disconnect, completed);
        Assert.IsType<RemoteBellMessage>(await receive.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, client.DisposeCount);
    }

    private static async Task<RemoteSession> ConnectAsync(RuntimeClient client)
        => await ConnectAsync(client, new MemoryStream());

    private static async Task<RemoteSession> ConnectAsync(IRfbClient client, Stream stream)
    {
        var handler = new ConnectDeviceHandler(
            new TransportFactory(stream),
            new SecretProvider(),
            new ClientFactory(client),
            new ErrorMapper());
        var result = await handler.HandleAsync(
            ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "user"),
            CancellationToken.None);
        return Assert.IsType<RemoteSession>(result.Session);
    }

    private sealed class RuntimeClient : IRfbClient
    {
        public RemoteFramebufferSize FramebufferSize => new(640, 480);
        public RemoteServerMessage NextMessage { get; init; } =
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(1, 1),
                [0, 0, 0, 255],
                4,
                [new RemoteRectangle(0, 0, 1, 1)]);
        public bool LastIncremental { get; private set; }
        public (byte Buttons, int X, int Y) LastPointer { get; private set; }
        public (uint Keysym, bool Down) LastKey { get; private set; }
        public string? LastClipboard { get; private set; }
        public int DisposeCount { get; private set; }

        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            LastIncremental = incremental;
            return ValueTask.CompletedTask;
        }
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(NextMessage);
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken)
        {
            LastPointer = (buttons, x, y);
            return ValueTask.CompletedTask;
        }
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken)
        {
            LastKey = (keysym, down);
            return ValueTask.CompletedTask;
        }
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken)
        {
            LastClipboard = text;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingReceiveClient(Stream stream) : IRfbClient
    {
        private readonly SemaphoreSlim _receiveGate = new(1, 1);

        public TaskCompletionSource ReceiveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            await _receiveGate.WaitAsync(cancellationToken);
            try
            {
                ReceiveStarted.TrySetResult();
                var buffer = new byte[1];
                _ = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                return new RemoteBellMessage();
            }
            finally
            {
                _receiveGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await _receiveGate.WaitAsync();
            _receiveGate.Release();
        }
    }

    private sealed class DisposeReleasesReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _read =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(_read.Task.WaitAsync(cancellationToken));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.TrySetResult(0);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TransportFactory(Stream? stream = null) : IRemoteTransportFactory
    {
        public Task<TransportConnection> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult(new TransportConnection(
                stream ?? new MemoryStream(),
                new EndPointDescription(profile.Host, profile.Port)));
    }

    private sealed class SecretProvider : IConnectionSecretProvider
    {
        public ValueTask<ISecret> GetSecretAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ISecret>(new Secret());
    }

    private sealed class ClientFactory(IRfbClient client) : IRfbClientFactory
    {
        public IRfbClient Create(Stream stream) => client;
    }

    private sealed class Secret : ISecret
    {
        public int Length => 1;
        public void CopyTo(Span<byte> destination) => destination[0] = 1;
        public ISecret Clone() => new Secret();
        public void Dispose() { }
    }
}
