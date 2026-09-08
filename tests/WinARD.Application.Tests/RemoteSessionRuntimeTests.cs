using System.Buffers;
using System.Reflection;
using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Application.Sessions;
using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707, CA1822

namespace WinARD.Application.Tests;

public sealed class RemoteSessionRuntimeTests
{
    [Fact]
    public void Constructor_rejects_null_preload_collection_with_the_owned_parameter_name()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            CreateSessionWithPreloads(null!));

        Assert.Equal("preloadedMessages", exception.ParamName);
    }

    [Fact]
    public void Constructor_disposes_owned_messages_when_a_preload_entry_is_null()
    {
        var owner = new CountingMemoryOwner(4);
        var cursor = CreateCursorMessage(owner);

        var exception = Assert.Throws<ArgumentException>(() =>
            CreateSessionWithPreloads([cursor, null!]));

        Assert.Equal("preloadedMessages", exception.ParamName);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public void Constructor_disposes_every_owned_message_when_preload_limit_is_exceeded()
    {
        var owners = Enumerable.Range(0, 9).Select(_ => new CountingMemoryOwner(4)).ToArray();
        var messages = owners.Select(CreateCursorMessage).Cast<RemoteServerMessage>().ToArray();

        var exception = Assert.Throws<ArgumentException>(() =>
            CreateSessionWithPreloads(messages));

        Assert.Equal("preloadedMessages", exception.ParamName);
        Assert.All(owners, owner => Assert.Equal(1, owner.DisposeCount));
    }

    [Fact]
    public void Constructor_disposes_every_owned_message_when_multiple_framebuffers_are_preloaded()
    {
        var owners = new[] { new CountingMemoryOwner(4), new CountingMemoryOwner(4) };
        var messages = owners.Select(CreateFramebufferMessage).Cast<RemoteServerMessage>().ToArray();

        var exception = Assert.Throws<ArgumentException>(() =>
            CreateSessionWithPreloads(messages));

        Assert.Equal("preloadedMessages", exception.ParamName);
        Assert.All(owners, owner => Assert.Equal(1, owner.DisposeCount));
    }

    [Fact]
    public void Constructor_preserves_validation_failure_and_disposes_all_owners_when_one_dispose_throws()
    {
        var first = new CountingMemoryOwner(4)
        {
            DisposeException = new InvalidOperationException("sensitive owner cleanup"),
        };
        var second = new CountingMemoryOwner(4);

        var exception = Assert.Throws<ArgumentException>(() =>
            CreateSessionWithPreloads(
            [
                CreateFramebufferMessage(first),
                CreateFramebufferMessage(second),
            ]));

        Assert.Equal("preloadedMessages", exception.ParamName);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public void Constructor_disposes_owned_messages_when_a_preload_type_is_not_allowed()
    {
        var owner = new CountingMemoryOwner(4);
        var cursor = CreateCursorMessage(owner);

        var exception = Assert.Throws<ArgumentException>(() =>
            CreateSessionWithPreloads([cursor, new RemoteBellMessage()]));

        Assert.Equal("preloadedMessages", exception.ParamName);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public void Runtime_defaults_to_no_preloaded_framebuffer_and_compatibility_exception_is_sanitized()
    {
        IRemoteSessionRuntime runtime = new LegacyRuntime();

        Assert.False(runtime.HasPreloadedFramebuffer);
        var exception = new QualityBootstrapCompatibilityException(
            QualityBootstrapFailureReason.DecoderFailure);
        Assert.Equal(QualityBootstrapFailureReason.DecoderFailure, exception.Reason);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LegacyRuntime : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<RemoteServerMessage>(new RemoteBellMessage());
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task Controlled_runtime_api_forwards_without_exposing_transport()
    {
        var client = new RuntimeClient
        {
            NextMessage = new RemoteClipboardMessage("remote text"),
            DisplayCapabilities = new RemoteDisplayCapabilities(120),
            PerformanceSnapshot = new RemoteRuntimePerformanceSnapshot(7, 2, 11),
            QualityCapabilities = new ArdDisplayCapabilities(
                CapabilitySupport.Observed,
                CapabilitySupport.Observed,
                CapabilitySupport.Unknown,
                CapabilitySupport.Unknown,
                CapabilitySupport.Unknown,
                true,
                false,
                120),
        };
        await using var session = await ConnectAsync(client);
        using var preloaded = Assert.IsType<RemoteFramebufferMessage>(
            await session.ReceiveAsync(CancellationToken.None));

        await session.RequestFramebufferUpdateAsync(incremental: true, CancellationToken.None);
        var quality = new RemoteQualitySettings(RemotePixelFormatKind.Rgb565, [16, 0], 1);
        var transition = await session.ApplyQualityTransitionAsync(quality, CancellationToken.None);
        var message = await session.ReceiveAsync(CancellationToken.None);
        await session.SendPointerAsync(3, 12, 34, CancellationToken.None);
        await session.SendKeyAsync(0xffe3, true, CancellationToken.None);
        await session.SendClipboardTextAsync("local text", CancellationToken.None);

        Assert.Equal(new RemoteFramebufferSize(640, 480), session.FramebufferSize);
        Assert.Equal(120, session.DisplayCapabilities.MaximumRefreshRate);
        Assert.Equal(new RemoteRuntimePerformanceSnapshot(7, 2, 11), session.PerformanceSnapshot);
        Assert.Same(client.QualityCapabilities, session.QualityCapabilities);
        Assert.IsType<RemoteClipboardMessage>(message);
        Assert.True(client.LastIncremental);
        Assert.Same(quality, client.LastQualitySettings);
        Assert.Equal(QualityTransitionStatus.Applied, transition);
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
        using var preloaded = Assert.IsType<RemoteFramebufferMessage>(
            await session.ReceiveAsync(CancellationToken.None));
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

    [Fact]
    public async Task Disconnect_stops_runtime_writes_before_transport_and_client_disposal()
    {
        var events = new List<string>();
        var client = new RuntimeClient
        {
            BeginShutdownAction = () => events.Add("client-begin"),
            DisposeAction = () => events.Add("client-dispose"),
        };
        var stream = new OrderingDisposeStream(() => events.Add("transport-dispose"));
        var session = await ConnectAsync(client, stream);

        await session.DisconnectAsync();

        Assert.Equal(
            ["client-begin", "transport-dispose", "client-dispose"],
            events);
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

    private static RemoteSession CreateSessionWithPreloads(
        IEnumerable<RemoteServerMessage> preloadedMessages)
    {
        var constructor = typeof(RemoteSession).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(SessionStateMachine),
                typeof(TransportConnection),
                typeof(IRfbClient),
                typeof(IEnumerable<RemoteServerMessage>),
            ],
            modifiers: null);
        Assert.NotNull(constructor);
        try
        {
            return (RemoteSession)constructor.Invoke(
                [
                    new SessionStateMachine(),
                    new TransportConnection(
                        new MemoryStream(),
                        new EndPointDescription("mac.local", 5900)),
                    new RuntimeClient(),
                    preloadedMessages,
                ]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static RemoteCursorMessage CreateCursorMessage(CountingMemoryOwner owner) =>
        new(new RemoteCursorUpdate(0, 0, 1, 1, owner, 4));

    private static RemoteFramebufferMessage CreateFramebufferMessage(CountingMemoryOwner owner) =>
        new(
            new RemoteFramebufferSize(1, 1),
            owner,
            4,
            4,
            [new RemoteRectangle(0, 0, 1, 1)]);

    private sealed class CountingMemoryOwner(int length) : IMemoryOwner<byte>
    {
        private byte[]? _memory = new byte[length];

        public int DisposeCount { get; private set; }
        public Exception? DisposeException { get; init; }
        public Memory<byte> Memory =>
            _memory ?? throw new ObjectDisposedException(nameof(CountingMemoryOwner));

        public void Dispose()
        {
            DisposeCount++;
            _memory = null;
            if (DisposeException is not null)
            {
                throw DisposeException;
            }
        }
    }

    private sealed class RuntimeClient : IRfbClient
    {
        private int _receiveCount;
        public RemoteFramebufferSize FramebufferSize { get; init; } = new(640, 480);
        public RemoteDisplayCapabilities DisplayCapabilities { get; init; } =
            RemoteDisplayCapabilities.Unknown;
        public RemoteRuntimePerformanceSnapshot PerformanceSnapshot { get; init; } =
            RemoteRuntimePerformanceSnapshot.Empty;
        public ArdDisplayCapabilities QualityCapabilities { get; init; } =
            ArdDisplayCapabilities.Unknown;
        public RemoteServerMessage NextMessage { get; init; } =
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(1, 1),
                [0, 0, 0, 255],
                4,
                [new RemoteRectangle(0, 0, 1, 1)]);
        public bool LastIncremental { get; private set; }
        public RemoteQualitySettings? LastQualitySettings { get; private set; }
        public (byte Buttons, int X, int Y) LastPointer { get; private set; }
        public (uint Keysym, bool Down) LastKey { get; private set; }
        public string? LastClipboard { get; private set; }
        public int DisposeCount { get; private set; }
        public Action? BeginShutdownAction { get; init; }
        public Action? DisposeAction { get; init; }

        public void ConfirmBootstrap(RemoteFramebufferSize framebufferSize) { }

        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask ConfigureBootstrapAsync(
            QualityBootstrapSettings settings,
            QualityBootstrapAttempt attempt,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            LastIncremental = incremental;
            return ValueTask.CompletedTask;
        }
        public ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(
            RemoteQualitySettings settings,
            CancellationToken cancellationToken)
        {
            LastQualitySettings = settings;
            return ValueTask.FromResult(QualityTransitionStatus.Applied);
        }
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                Interlocked.Increment(ref _receiveCount) == 1
                    ? new RemoteFramebufferMessage(
                        FramebufferSize, [0, 0, 0, 255], 4,
                        [new RemoteRectangle(0, 0, (ushort)FramebufferSize.Width, (ushort)FramebufferSize.Height)])
                    : NextMessage);
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
        public void BeginShutdown() => BeginShutdownAction?.Invoke();
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeAction?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingReceiveClient(Stream stream) : IRfbClient
    {
        private readonly SemaphoreSlim _receiveGate = new(1, 1);
        private int _receives;

        public TaskCompletionSource ReceiveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask ConfigureBootstrapAsync(
            QualityBootstrapSettings settings,
            QualityBootstrapAttempt attempt,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void ConfirmBootstrap(RemoteFramebufferSize framebufferSize) { }

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _receives) == 1)
            {
                return new RemoteFramebufferMessage(
                    new RemoteFramebufferSize(1, 1), [0, 0, 0, 255], 4,
                    [new RemoteRectangle(0, 0, 1, 1)]);
            }

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

    private sealed class OrderingDisposeStream(Action onDispose) : MemoryStream
    {
        public override ValueTask DisposeAsync()
        {
            onDispose();
            return base.DisposeAsync();
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
