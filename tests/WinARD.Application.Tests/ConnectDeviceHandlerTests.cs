using System.Collections.Concurrent;
using System.Buffers;
using System.Net.Sockets;
using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Application.Sessions;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Domain.Sessions;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Transport;
using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Application.Tests;

public sealed class ConnectDeviceHandlerTests
{
    [Theory]
    [MemberData(nameof(BootstrapProfileCases))]
    public async Task Handler_plans_bootstrap_from_each_connection_profile(
        QualityProfile quality,
        RemotePixelFormatKind expectedPixelFormat)
    {
        var client = new TestRfbClient();
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(),
            new TestSecretProvider(new TestConnectionSecret()),
            new TestRfbClientFactory(client),
            new ErrorMapper(() => "correlation-id"));

        var result = await handler.HandleAsync(
            CreateProfile().WithQualityProfile(quality),
            CancellationToken.None);

        Assert.Equal(expectedPixelFormat, client.ConfiguredSettings!.PixelFormat);
        await result.Session!.DisposeAsync();
    }

    [Fact]
    public void Handler_preserves_the_exact_legacy_four_parameter_constructor()
    {
        Assert.NotNull(typeof(ConnectDeviceHandler).GetConstructor(
        [
            typeof(IRemoteTransportFactory),
            typeof(IConnectionSecretProvider),
            typeof(IRfbClientFactory),
            typeof(IErrorMapper),
        ]));
    }

    [Fact]
    public void Framebuffer_message_preserves_legacy_constructors_and_defaults_to_pixel_content()
    {
        Assert.NotNull(typeof(RemoteFramebufferMessage).GetConstructor(
        [
            typeof(RemoteFramebufferSize),
            typeof(byte[]),
            typeof(int),
            typeof(IReadOnlyList<RemoteRectangle>),
            typeof(RemoteCursorUpdate),
            typeof(RemoteUpdateStatistics),
        ]));
        Assert.NotNull(typeof(RemoteFramebufferMessage).GetConstructor(
        [
            typeof(RemoteFramebufferSize),
            typeof(IMemoryOwner<byte>),
            typeof(int),
            typeof(int),
            typeof(IReadOnlyList<RemoteRectangle>),
            typeof(RemoteCursorUpdate),
            typeof(RemoteUpdateStatistics),
            typeof(bool),
        ]));

        using var message = new RemoteFramebufferMessage(
            new RemoteFramebufferSize(1, 1),
            [0, 0, 0, 255],
            4,
            [new RemoteRectangle(0, 0, 1, 1)]);

        Assert.True(message.HasPixelContent);
    }

    [Fact]
    public async Task Invalid_bootstrap_attempt_is_rejected_before_any_dependency_is_called()
    {
        var secretProvider = new TestSecretProvider(new TestConnectionSecret());
        var transportFactory = new TestTransportFactory();
        var clientFactory = new TestRfbClientFactory(new TestRfbClient());
        var handler = new ConnectDeviceHandler(
            transportFactory,
            secretProvider,
            clientFactory,
            new ErrorMapper(() => "correlation-id"));

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            handler.HandleAsync(
                CreateProfile(),
                (QualityBootstrapAttempt)999,
                CancellationToken.None));

        Assert.Equal("attempt", exception.ParamName);
        Assert.Equal(0, secretProvider.CallCount);
        Assert.Equal(0, transportFactory.CallCount);
        Assert.Equal(0, clientFactory.CreateCount);
    }

    public static TheoryData<QualityProfile, RemotePixelFormatKind> BootstrapProfileCases => new()
    {
        { QualityProfile.Original, RemotePixelFormatKind.Bgra32 },
        {
            QualityProfile.CreateCustom(
                1024,
                QualityColor.Full32,
                QualityScale.Percent100,
                FrameRefreshPolicy.Automatic,
                allowAutomaticGrayscale: false,
                colorLocked: true),
            RemotePixelFormatKind.Bgra32
        },
        { QualityProfile.Smooth, RemotePixelFormatKind.Rgb565 },
    };
    [Fact]
    public async Task Handler_bootstraps_requests_and_preloads_cursor_then_frame_in_wire_order()
    {
        var events = new List<string>();
        var cursorOwner = new CountingOwner(4);
        var frameOwner = new CountingOwner(4);
        var client = new TestRfbClient
        {
            Events = events,
            Messages = new Queue<RemoteServerMessage>(
            [
                new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 1, 1, cursorOwner, 4)),
                new RemoteFramebufferMessage(
                    new RemoteFramebufferSize(1, 1), frameOwner, 4, 4,
                    [new RemoteRectangle(0, 0, 1, 1)]),
            ]),
        };
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(),
            new TestSecretProvider(new TestConnectionSecret()),
            new TestRfbClientFactory(client),
            new ErrorMapper(() => "correlation-id"));

        var result = await handler.HandleAsync(CreateProfile(), CancellationToken.None);
        var session = Assert.IsType<RemoteSession>(result.Session);

        Assert.Equal(
            ["initialize", "configure-Preferred", "request-False", "receive", "request-False", "receive"],
            events.Where(value => value is "initialize" or "receive" ||
                value.StartsWith("configure", StringComparison.Ordinal) ||
                value.StartsWith("request", StringComparison.Ordinal)));
        Assert.True(session.HasPreloadedFramebuffer);
        using var cursor = Assert.IsType<RemoteCursorMessage>(await session.ReceiveAsync(default));
        Assert.True(session.HasPreloadedFramebuffer);
        using var frame = Assert.IsType<RemoteFramebufferMessage>(await session.ReceiveAsync(default));
        Assert.False(session.HasPreloadedFramebuffer);
        Assert.Equal(2, client.ReceiveCount);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_session_releases_unconsumed_preloaded_owners_exactly_once()
    {
        var cursorOwner = new CountingOwner(4);
        var frameOwner = new CountingOwner(4);
        var client = new TestRfbClient
        {
            Messages = new Queue<RemoteServerMessage>(
            [
                new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 1, 1, cursorOwner, 4)),
                new RemoteFramebufferMessage(
                    new RemoteFramebufferSize(1, 1), frameOwner, 4, 4,
                    [new RemoteRectangle(0, 0, 1, 1)]),
            ]),
        };
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(),
            new TestSecretProvider(new TestConnectionSecret()),
            new TestRfbClientFactory(client),
            new ErrorMapper(() => "correlation-id"));
        var result = await handler.HandleAsync(CreateProfile(), default);

        await result.Session!.DisposeAsync();
        await result.Session.DisposeAsync();

        Assert.Equal(1, cursorOwner.DisposeCount);
        Assert.Equal(1, frameOwner.DisposeCount);
    }

    [Fact]
    public async Task Handler_discards_empty_frame_requests_again_and_preloads_the_first_pixel_frame()
    {
        var events = new List<string>();
        var emptyOwner = new CountingOwner(4);
        var pixelOwner = new CountingOwner(4);
        var client = new TestRfbClient
        {
            Events = events,
            Messages = new Queue<RemoteServerMessage>(
            [
                CreateFramebufferMessage(emptyOwner, hasPixelContent: false),
                CreateFramebufferMessage(pixelOwner),
            ]),
        };
        var handler = CreateHandler(client);

        var result = await handler.HandleAsync(CreateProfile(), default);

        Assert.Equal(SessionState.Connected, result.State);
        Assert.Equal(1, emptyOwner.DisposeCount);
        Assert.Equal(
            ["request-False", "receive", "request-False", "receive"],
            events.Where(value => value is "receive" || value.StartsWith("request", StringComparison.Ordinal)));
        await result.Session!.DisposeAsync();
        Assert.Equal(1, pixelOwner.DisposeCount);
    }

    [Fact]
    public async Task Handler_preserves_cursor_order_across_a_nonpixel_frame_and_reissues_only_for_the_frame()
    {
        var events = new List<string>();
        var firstCursorOwner = new CountingOwner(4);
        var emptyOwner = new CountingOwner(4);
        var secondCursorOwner = new CountingOwner(4);
        var pixelOwner = new CountingOwner(4);
        var client = new TestRfbClient
        {
            Events = events,
            Messages = new Queue<RemoteServerMessage>(
            [
                CreateCursorMessage(firstCursorOwner),
                CreateFramebufferMessage(emptyOwner, hasPixelContent: false),
                CreateCursorMessage(secondCursorOwner),
                CreateFramebufferMessage(pixelOwner),
            ]),
        };
        var handler = CreateHandler(client);

        var result = await handler.HandleAsync(CreateProfile(), default);
        var session = Assert.IsType<RemoteSession>(result.Session);

        Assert.Equal(
            [
                "request-False", "receive", "request-False", "receive",
                "request-False", "receive", "request-False", "receive",
            ],
            events.Where(value => value is "receive" || value.StartsWith("request", StringComparison.Ordinal)));
        using var firstCursor = Assert.IsType<RemoteCursorMessage>(await session.ReceiveAsync(default));
        using var secondCursor = Assert.IsType<RemoteCursorMessage>(await session.ReceiveAsync(default));
        using var pixelFrame = Assert.IsType<RemoteFramebufferMessage>(await session.ReceiveAsync(default));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Handler_preserves_bootstrap_compatibility_failure_after_an_empty_frame()
    {
        var emptyOwner = new CountingOwner(4);
        var compatibility = new QualityBootstrapCompatibilityException(
            QualityBootstrapFailureReason.UnsupportedEncoding);
        var client = new TestRfbClient
        {
            Messages = new Queue<RemoteServerMessage>(
            [
                CreateFramebufferMessage(emptyOwner, hasPixelContent: false),
            ]),
            ReceiveException = compatibility,
        };
        var mapper = new CapturingErrorMapper();
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(),
            new TestSecretProvider(new TestConnectionSecret()),
            new TestRfbClientFactory(client),
            mapper);

        var result = await handler.HandleAsync(CreateProfile(), default);

        Assert.Equal(SessionState.Failed, result.State);
        Assert.Same(compatibility, mapper.Exception);
        Assert.Equal(1, emptyOwner.DisposeCount);
        Assert.Equal(2, client.RequestCount);
    }

    [Fact]
    public async Task Handler_bounds_cursor_and_nonpixel_messages_and_releases_every_owner()
    {
        var owners = Enumerable.Range(0, 9).Select(_ => new CountingOwner(4)).ToArray();
        var messages = owners.Select((owner, index) =>
            index % 2 == 0
                ? (RemoteServerMessage)CreateFramebufferMessage(owner, hasPixelContent: false)
                : CreateCursorMessage(owner));
        var client = new TestRfbClient { Messages = new Queue<RemoteServerMessage>(messages) };
        var mapper = new CapturingErrorMapper();
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(),
            new TestSecretProvider(new TestConnectionSecret()),
            new TestRfbClientFactory(client),
            mapper);

        var result = await handler.HandleAsync(CreateProfile(), default);

        Assert.Equal(SessionState.Failed, result.State);
        var exception = Assert.IsType<InvalidOperationException>(mapper.Exception);
        Assert.Equal("The bootstrap preload queue exceeded its limit.", exception.Message);
        Assert.Equal(8, client.ReceiveCount);
        Assert.All(owners[..8], owner => Assert.Equal(1, owner.DisposeCount));
        Assert.Equal(0, owners[8].DisposeCount);
    }

    [Fact]
    public async Task Preload_failure_preserves_primary_and_best_effort_cleans_all_owned_resources()
    {
        var firstOwner = new CountingOwner(4)
        {
            DisposeException = new InvalidOperationException("sensitive owner cleanup"),
        };
        var secondOwner = new CountingOwner(4);
        var overflowOwner = new CountingOwner(4);
        var messages = new List<RemoteServerMessage>
        {
            CreateCursorMessage(firstOwner),
            CreateCursorMessage(secondOwner),
        };
        messages.AddRange(Enumerable.Range(0, 6).Select(_ =>
            CreateCursorMessage(new CountingOwner(4))));
        messages.Add(CreateFramebufferMessage(overflowOwner));
        var secret = new TestConnectionSecret();
        var transportLifetime = new TestAsyncDisposable();
        var client = new TestRfbClient { Messages = new Queue<RemoteServerMessage>(messages) };
        var mapper = new CapturingErrorMapper();
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(transportLifetime),
            new TestSecretProvider(secret),
            new TestRfbClientFactory(client),
            mapper);

        var result = await handler.HandleAsync(CreateProfile(), CancellationToken.None);

        Assert.Equal(SessionState.Failed, result.State);
        var primary = Assert.IsType<InvalidOperationException>(mapper.Exception);
        Assert.Equal("The bootstrap preload queue exceeded its limit.", primary.Message);
        Assert.Equal(1, firstOwner.DisposeCount);
        Assert.Equal(1, secondOwner.DisposeCount);
        Assert.Equal(0, overflowOwner.DisposeCount);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, transportLifetime.DisposeCount);
        Assert.Equal(1, secret.DisposeCount);
    }
    public static TheoryData<Exception, string> StableMappedErrors => new()
    {
        { new SocketException((int)SocketError.HostNotFound), "DNS_RESOLUTION_FAILED" },
        { new SocketException((int)SocketError.ConnectionRefused), "TCP_CONNECTION_FAILED" },
        { new TransportTimeoutException(TransportTimeoutStage.Connection), "TRANSPORT_TIMEOUT" },
        { new OpenSshTunnelException(255, "secret diagnostic"), "SSH_CONNECTION_FAILED" },
        { new OpenSshKeyScanException("secret key scan failure"), "SSH_CONNECTION_FAILED" },
        { new OpenSshAuthenticationUnsupportedException(), "SSH_AUTH_UNSUPPORTED" },
        { CreateUnknownHostKeyException(), "SSH_HOST_KEY_UNKNOWN" },
        { new SshHostKeyChangedException(new SshHostKeyEndpoint("mac.local", 22)), "SSH_HOST_KEY_CHANGED" },
        { new UnsupportedRfbVersionException("secret banner"), "RFB_VERSION_UNSUPPORTED" },
        { new UnsupportedSecurityTypeException(RfbVersion.V3_8, [2]), "RFB_SECURITY_UNSUPPORTED" },
        { new RfbConnectionRejectedException(RfbVersion.V3_8, "secret reason", false), "RFB_CONNECTION_REJECTED" },
        { new ArdAuthenticationRejectedException(1, "secret reason", false), "ARD_AUTH_REJECTED" },
        { new ArdExtendedInitializationRequiredException(), "ARD_EXTENDED_INIT_REQUIRED" },
        { new ArdControlNotAllowedException(0xDEADBEEF), "ARD_CONTROL_NOT_ALLOWED" },
        { new ArdSessionCommandUnavailableException(), "ARD_SESSION_COMMAND_UNAVAILABLE" },
        { new ArdSessionDeniedException(0xCAFEBABE), "ARD_SESSION_DENIED" },
        { new ArdSessionMalformedException(), "ARD_SESSION_MALFORMED" },
        { new RfbProtocolException("secret packet"), "RFB_PROTOCOL_ERROR" },
        { new OperationCanceledException("secret cancellation"), "CONNECTION_INTERRUPTED" },
    };

    [Fact]
    public async Task Authentication_failure_returns_failed_result_at_authenticating_stage()
    {
        var secret = new TestConnectionSecret();
        var client = new TestRfbClient
        {
            AuthenticateException = new ArdAuthenticationRejectedException(1, "sensitive reason", false),
        };
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(),
            new TestSecretProvider(secret),
            new TestRfbClientFactory(client),
            new ErrorMapper(() => "correlation-id"));

        var result = await handler.HandleAsync(CreateProfile(), CancellationToken.None);

        Assert.Equal(SessionState.Failed, result.State);
        Assert.Null(result.Session);
        Assert.NotNull(result.Error);
        Assert.Equal(ConnectionStage.Authenticating, result.Error.Stage);
        Assert.Equal("ARD_AUTH_REJECTED", result.Error.Code);
    }

    [Fact]
    public async Task Coordinator_rejects_second_session_until_first_lease_is_released()
    {
        var coordinator = new ActiveSessionCoordinator();
        var first = await coordinator.AcquireAsync(CancellationToken.None);

        await Assert.ThrowsAsync<SessionAlreadyActiveException>(
            async () => await coordinator.AcquireAsync(CancellationToken.None));

        await first.DisposeAsync();
        await using var second = await coordinator.AcquireAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_allows_only_one_winner_under_concurrent_acquisition()
    {
        var coordinator = new ActiveSessionCoordinator();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winners = new ConcurrentBag<ActiveSessionCoordinator.ActiveSessionLease>();
        var rejected = 0;
        var attempts = Enumerable.Range(0, 32).Select(async _ =>
        {
            await start.Task;
            try
            {
                winners.Add(await coordinator.AcquireAsync(CancellationToken.None));
            }
            catch (SessionAlreadyActiveException)
            {
                _ = Interlocked.Increment(ref rejected);
            }
        }).ToArray();

        start.SetResult();
        await Task.WhenAll(attempts);

        Assert.Single(winners);
        Assert.Equal(31, rejected);
        await winners.Single().DisposeAsync();
        await using var next = await coordinator.AcquireAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_cancellation_and_repeated_stale_disposal_do_not_corrupt_active_lease()
    {
        var coordinator = new ActiveSessionCoordinator();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await coordinator.AcquireAsync(cancellation.Token));

        var first = await coordinator.AcquireAsync(CancellationToken.None);
        await first.DisposeAsync();
        await first.DisposeAsync();
        await using var second = await coordinator.AcquireAsync(CancellationToken.None);
        await first.DisposeAsync();

        await Assert.ThrowsAsync<SessionAlreadyActiveException>(
            async () => await coordinator.AcquireAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Remote_session_reaches_idle_and_disposes_resources_once_when_transport_disposal_fails()
    {
        var lifetime = new TestAsyncDisposable
        {
            DisposeException = new InvalidOperationException("transport cleanup failed"),
        };
        var client = new TestRfbClient();
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(lifetime),
            new TestSecretProvider(new TestConnectionSecret()),
            new TestRfbClientFactory(client),
            new ErrorMapper(() => "correlation-id"));
        var result = await handler.HandleAsync(CreateProfile(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await result.Session!.DisposeAsync());
        var repeated = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await result.Session!.DisposeAsync());

        Assert.Equal("transport cleanup failed", exception.Message);
        Assert.Same(exception, repeated);
        Assert.Equal(SessionState.Idle, result.Session!.State);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Failed_remote_session_cleans_resources_once_under_concurrent_and_repeated_disposal()
    {
        var lifetime = new TestAsyncDisposable();
        var client = new TestRfbClient();
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(lifetime),
            new TestSecretProvider(new TestConnectionSecret()),
            new TestRfbClientFactory(client),
            new ErrorMapper(() => "correlation-id"));
        var result = await handler.HandleAsync(CreateProfile(), CancellationToken.None);
        var session = Assert.IsType<RemoteSession>(result.Session);
        session.MarkFailed();
        Assert.Equal(SessionState.Failed, session.State);

        var first = session.DisposeAsync().AsTask();
        var second = session.DisposeAsync().AsTask();
        await Task.WhenAll(first, second);
        await session.DisposeAsync();

        Assert.Equal(SessionState.Idle, session.State);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Remote_session_synchronous_reentrant_dispose_reuses_published_completion()
    {
        var lifetime = new TestAsyncDisposable();
        var client = new TestRfbClient();
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(lifetime),
            new TestSecretProvider(new TestConnectionSecret()),
            new TestRfbClientFactory(client),
            new ErrorMapper(() => "correlation-id"));
        var result = await handler.HandleAsync(CreateProfile(), CancellationToken.None);
        var session = Assert.IsType<RemoteSession>(result.Session);
        Task? reentrantCompletion = null;
        var reentered = 0;
        client.DisposeCallback = () =>
        {
            if (Interlocked.Exchange(ref reentered, 1) == 0)
            {
                reentrantCompletion = session.DisposeAsync().AsTask();
            }
        };

        var outerCompletion = session.DisposeAsync().AsTask();
        await outerCompletion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(outerCompletion, reentrantCompletion);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Remote_session_concurrent_dispose_calls_share_blocking_failure_and_cleanup_once()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new TestAsyncDisposable();
        var client = new TestRfbClient
        {
            DisposeCallback = () => entered.TrySetResult(),
            DisposeTask = blockedDispose.Task,
        };
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(lifetime),
            new TestSecretProvider(new TestConnectionSecret()),
            new TestRfbClientFactory(client),
            new ErrorMapper(() => "correlation-id"));
        var result = await handler.HandleAsync(CreateProfile(), CancellationToken.None);
        var session = Assert.IsType<RemoteSession>(result.Session);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new ConcurrentBag<Task>();
        var callers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            completions.Add(session.DisposeAsync().AsTask());
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(2));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var shared = completions.ToArray();
        Assert.Equal(2, shared.Length);
        Assert.Same(shared[0], shared[1]);
        var primary = new InvalidOperationException("blocked client cleanup failed");
        blockedDispose.SetException(primary);

        var firstFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => shared[0].WaitAsync(TimeSpan.FromSeconds(2)));
        var secondFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => shared[1].WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(primary, firstFailure);
        Assert.Same(firstFailure, secondFailure);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public void Remote_session_does_not_expose_disposable_resources()
    {
        Assert.Null(typeof(RemoteSession).GetProperty("Client"));
        Assert.Null(typeof(RemoteSession).GetProperty("Transport"));
    }

    [Fact]
    public async Task Precancelled_connection_does_not_invoke_dependencies()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var secretProvider = new TestSecretProvider(new TestConnectionSecret());
        var transportFactory = new TestTransportFactory();
        var clientFactory = new TestRfbClientFactory(new TestRfbClient());
        var handler = new ConnectDeviceHandler(
            transportFactory,
            secretProvider,
            clientFactory,
            new ErrorMapper(() => "correlation-id"));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.HandleAsync(CreateProfile(), cancellation.Token));

        Assert.Equal(0, secretProvider.CallCount);
        Assert.Equal(0, transportFactory.CallCount);
        Assert.Equal(0, clientFactory.CreateCount);
    }

    [Fact]
    public async Task Cancellation_after_secret_resolution_stops_before_transport_and_cleans_secret()
    {
        using var cancellation = new CancellationTokenSource();
        var secret = new TestConnectionSecret();
        var secretProvider = new TestSecretProvider(secret, cancellation);
        var transportFactory = new TestTransportFactory();
        var handler = new ConnectDeviceHandler(
            transportFactory,
            secretProvider,
            new TestRfbClientFactory(new TestRfbClient()),
            new ErrorMapper(() => "correlation-id"));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.HandleAsync(CreateProfile(), cancellation.Token));

        Assert.Equal(1, secretProvider.CallCount);
        Assert.Equal(0, transportFactory.CallCount);
        Assert.Equal(1, secret.DisposeCount);
    }

    [Fact]
    public async Task Cancellation_after_authentication_destroys_secret_and_skips_initialization()
    {
        using var cancellation = new CancellationTokenSource();
        var secret = new TestConnectionSecret();
        var lifetime = new TestAsyncDisposable();
        var client = new TestRfbClient
        {
            CancellationAfterAuthenticate = cancellation,
        };
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(lifetime),
            new TestSecretProvider(secret),
            new TestRfbClientFactory(client),
            new ErrorMapper(() => "correlation-id"));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.HandleAsync(CreateProfile(), cancellation.Token));

        Assert.Equal(1, secret.DisposeCount);
        Assert.Equal(0, client.InitializeCount);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Successful_connection_runs_phases_in_order_and_session_owns_resources()
    {
        var events = new List<string>();
        var secret = new TestConnectionSecret { Events = events };
        var lifetime = new TestAsyncDisposable { Events = events };
        var client = new TestRfbClient { Events = events };
        var secretProvider = new TestSecretProvider(secret) { Events = events };
        var transportFactory = new TestTransportFactory(lifetime) { Events = events };
        var clientFactory = new TestRfbClientFactory(client) { Events = events };
        var handler = new ConnectDeviceHandler(
            transportFactory,
            secretProvider,
            clientFactory,
            new ErrorMapper(() => "correlation-id"));

        var result = await handler.HandleAsync(CreateProfile(), CancellationToken.None);

        Assert.Equal(SessionState.Connected, result.State);
        Assert.Null(result.Error);
        Assert.NotNull(result.Session);
        Assert.Equal(
            [
                "resolve-secret", "connect-transport", "create-client", "negotiate", "authenticate",
                "secret-dispose", "initialize", "configure-Preferred", "request-False", "receive",
            ],
            events);

        await result.Session.DisposeAsync();

        Assert.Equal(SessionState.Idle, result.Session.State);
        Assert.Equal("transport-dispose", events[^2]);
        Assert.Equal("client-dispose", events[^1]);
    }

    [Fact]
    public async Task Cancellation_cleanup_attempts_all_resources_in_order_without_masking_cancellation()
    {
        var events = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var primary = new OperationCanceledException(cancellation.Token);
        var secret = new TestConnectionSecret
        {
            Events = events,
            DisposeException = new InvalidOperationException("secret cleanup"),
        };
        var lifetime = new TestAsyncDisposable
        {
            Events = events,
            DisposeException = new InvalidOperationException("transport cleanup"),
        };
        var client = new TestRfbClient
        {
            Events = events,
            NegotiateException = primary,
            CancellationBeforeNegotiateException = cancellation,
            DisposeException = new InvalidOperationException("client cleanup"),
        };
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(lifetime),
            new TestSecretProvider(secret),
            new TestRfbClientFactory(client),
            new ErrorMapper(() => "correlation-id"));

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.HandleAsync(CreateProfile(), cancellation.Token));

        Assert.Same(primary, thrown);
        Assert.Equal(["negotiate", "client-dispose", "transport-dispose", "secret-dispose"], events);
    }

    [Fact]
    public async Task Internal_operation_cancellation_is_mapped_as_failure_when_caller_did_not_cancel()
    {
        var primary = new OperationCanceledException("internal timeout");
        var handler = new ConnectDeviceHandler(
            new TestTransportFactory(),
            new TestSecretProvider(new TestConnectionSecret()),
            new TestRfbClientFactory(new TestRfbClient { NegotiateException = primary }),
            new ErrorMapper(() => "internal-correlation"));

        var result = await handler.HandleAsync(CreateProfile(), CancellationToken.None);

        Assert.Equal(SessionState.Failed, result.State);
        Assert.Equal(ConnectionStage.Negotiating, result.Error!.Stage);
        Assert.Equal("CONNECTION_INTERRUPTED", result.Error.Code);
        Assert.NotEqual("CONNECTION_CANCELLED", result.Error.Code);
        Assert.Equal("internal-correlation", result.Error.CorrelationId);
    }

    [Theory]
    [InlineData("resolving", ConnectionStage.Resolving)]
    [InlineData("connecting", ConnectionStage.Connecting)]
    [InlineData("negotiating", ConnectionStage.Negotiating)]
    [InlineData("authenticating", ConnectionStage.Authenticating)]
    [InlineData("initializing", ConnectionStage.Initializing)]
    public async Task Failure_is_mapped_at_the_stage_where_it_occurred(
        string failurePoint,
        ConnectionStage expectedStage)
    {
        var primary = new InvalidOperationException($"failure at {failurePoint}");
        var secret = new TestConnectionSecret();
        var lifetime = new TestAsyncDisposable();
        var client = new TestRfbClient
        {
            NegotiateException = failurePoint == "negotiating" ? primary : null,
            AuthenticateException = failurePoint == "authenticating" ? primary : null,
            InitializeException = failurePoint == "initializing" ? primary : null,
        };
        var secretProvider = new TestSecretProvider(secret)
        {
            GetException = failurePoint == "resolving" ? primary : null,
        };
        var transportFactory = new TestTransportFactory(lifetime)
        {
            ConnectException = failurePoint == "connecting" ? primary : null,
        };
        var mapper = new CapturingErrorMapper();
        var handler = new ConnectDeviceHandler(
            transportFactory,
            secretProvider,
            new TestRfbClientFactory(client),
            mapper);

        var result = await handler.HandleAsync(CreateProfile(), CancellationToken.None);

        Assert.Equal(SessionState.Failed, result.State);
        Assert.Null(result.Session);
        Assert.Equal(expectedStage, result.Error!.Stage);
        Assert.Same(primary, mapper.Exception);
        Assert.Equal(expectedStage, mapper.Stage);
    }

    [Theory]
    [MemberData(nameof(StableMappedErrors))]
    public void Error_mapper_returns_stable_code_without_exposing_exception_message(
        Exception exception,
        string expectedCode)
    {
        var mapper = new ErrorMapper(() => "correlation-id");

        var error = mapper.Map(exception, ConnectionStage.Connecting);

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(ConnectionStage.Connecting, error.Stage);
        Assert.Equal("correlation-id", error.CorrelationId);
        Assert.DoesNotContain("secret", error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Error_mapper_uses_safe_summary_for_unknown_exception()
    {
        var mapper = new ErrorMapper(() => "log-only-id");

        var error = mapper.Map(
            new InvalidOperationException("password=hunter2; host=private.example"),
            ConnectionStage.Initializing);

        Assert.Equal("UNEXPECTED_CONNECTION_ERROR", error.Code);
        Assert.Equal("The connection failed unexpectedly.", error.UserMessage);
        Assert.Equal("log-only-id", error.CorrelationId);
        Assert.DoesNotContain("hunter2", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private.example", error.UserMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(ArdExtendedInitializationRequiredException), "ARD_EXTENDED_INIT_REQUIRED", "The Mac requires Apple Remote Desktop extended initialization before control can begin.")]
    [InlineData(typeof(ArdControlNotAllowedException), "ARD_CONTROL_NOT_ALLOWED", "The Mac did not allow control for this session.")]
    [InlineData(typeof(ArdSessionCommandUnavailableException), "ARD_SESSION_COMMAND_UNAVAILABLE", "The Mac does not support selecting a console session.")]
    [InlineData(typeof(ArdSessionDeniedException), "ARD_SESSION_DENIED", "The Mac denied access to the console session.")]
    [InlineData(typeof(ArdSessionMalformedException), "ARD_SESSION_MALFORMED", "The Mac returned an invalid console session response.")]
    public void Error_mapper_returns_fixed_safe_summary_for_control_negotiation_failures(
        Type exceptionType,
        string expectedCode,
        string expectedMessage)
    {
        var exception = CreateControlNegotiationException(exceptionType);
        var mapper = new ErrorMapper(() => "control-correlation-id");

        var error = mapper.Map(exception, ConnectionStage.Initializing);

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedMessage, error.UserMessage);
        Assert.DoesNotContain(exception.Message, error.UserMessage, StringComparison.Ordinal);
    }

    private static ConnectionProfile CreateProfile() =>
        ConnectionProfile.Create(Guid.NewGuid(), "Office Mac", "mac.local", 5900, "alice");

    private static SshHostKeyUnknownException CreateUnknownHostKeyException()
    {
        var endpoint = new SshHostKeyEndpoint("mac.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "AQID");
        return new SshHostKeyUnknownException(SshHostKeyVerifier.Verify(candidate, pin: null));
    }

    private static Exception CreateControlNegotiationException(Type exceptionType) =>
        exceptionType == typeof(ArdExtendedInitializationRequiredException)
            ? new ArdExtendedInitializationRequiredException()
            : exceptionType == typeof(ArdControlNotAllowedException)
                ? new ArdControlNotAllowedException(0xDEADBEEF)
                : exceptionType == typeof(ArdSessionCommandUnavailableException)
                    ? new ArdSessionCommandUnavailableException()
                    : exceptionType == typeof(ArdSessionDeniedException)
                        ? new ArdSessionDeniedException(0xCAFEBABE)
                        : exceptionType == typeof(ArdSessionMalformedException)
                            ? new ArdSessionMalformedException()
                            : throw new ArgumentOutOfRangeException(nameof(exceptionType));

    private sealed class TestSecretProvider(
        ISecret secret,
        CancellationTokenSource? cancellation = null) : IConnectionSecretProvider
    {
        public List<string>? Events { get; init; }

        public Exception? GetException { get; init; }

        public int CallCount { get; private set; }

        public ValueTask<ISecret> GetSecretAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Events?.Add("resolve-secret");
            if (GetException is not null)
            {
                return ValueTask.FromException<ISecret>(GetException);
            }

            cancellation?.Cancel();
            return ValueTask.FromResult(secret);
        }
    }

    private sealed class TestConnectionSecret : ISecret
    {
        public List<string>? Events { get; init; }

        public Exception? DisposeException { get; init; }

        public int DisposeCount { get; private set; }

        public int Length => 8;

        public void CopyTo(Span<byte> destination) => "password"u8.CopyTo(destination);

        public ISecret Clone() => new TestConnectionSecret();

        public void Dispose()
        {
            DisposeCount++;
            Events?.Add("secret-dispose");
            if (DisposeException is not null)
            {
                throw DisposeException;
            }
        }
    }

    private sealed class TestTransportFactory(IAsyncDisposable? lifetime = null) : IRemoteTransportFactory
    {
        public List<string>? Events { get; init; }

        public Exception? ConnectException { get; init; }

        public int CallCount { get; private set; }

        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Events?.Add("connect-transport");
            if (ConnectException is not null)
            {
                return Task.FromException<TransportConnection>(ConnectException);
            }

            return Task.FromResult(new TransportConnection(
                new MemoryStream(),
                new EndPointDescription(profile.Host, profile.Port),
                lifetime));
        }
    }

    private sealed class TestRfbClientFactory(IRfbClient client) : IRfbClientFactory
    {
        public List<string>? Events { get; init; }

        public int CreateCount { get; private set; }

        public IRfbClient Create(Stream stream)
        {
            CreateCount++;
            Events?.Add("create-client");
            return client;
        }
    }

    private sealed class TestRfbClient : IRfbClient
    {
        public Exception? AuthenticateException { get; init; }

        public Exception? NegotiateException { get; init; }

        public CancellationTokenSource? CancellationBeforeNegotiateException { get; init; }

        public Exception? InitializeException { get; init; }

        public Exception? ReceiveException { get; init; }

        public Exception? DisposeException { get; init; }

        public List<string>? Events { get; init; }

        public Action? DisposeCallback { get; set; }

        public Task? DisposeTask { get; init; }

        public CancellationTokenSource? CancellationAfterAuthenticate { get; init; }

        public int DisposeCount { get; private set; }

        public int InitializeCount { get; private set; }
        public int RequestCount { get; private set; }
        public int ReceiveCount { get; private set; }
        public Queue<RemoteServerMessage>? Messages { get; init; }
        public QualityBootstrapSettings? ConfiguredSettings { get; private set; }

        public Task NegotiateAsync(CancellationToken cancellationToken)
        {
            Events?.Add("negotiate");
            if (NegotiateException is not null)
            {
                CancellationBeforeNegotiateException?.Cancel();
                return Task.FromException(NegotiateException);
            }

            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(
            string username,
            ISecret secret,
            CancellationToken cancellationToken)
        {
            Events?.Add("authenticate");
            if (AuthenticateException is not null)
            {
                return Task.FromException(AuthenticateException);
            }

            CancellationAfterAuthenticate?.Cancel();
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCount++;
            Events?.Add("initialize");
            return InitializeException is null
                ? Task.CompletedTask
                : Task.FromException(InitializeException);
        }

        public ValueTask ConfigureBootstrapAsync(
            QualityBootstrapSettings settings,
            QualityBootstrapAttempt attempt,
            CancellationToken cancellationToken)
        {
            ConfiguredSettings = settings;
            Events?.Add($"configure-{attempt}");
            return ValueTask.CompletedTask;
        }

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            RequestCount++;
            Events?.Add($"request-{incremental}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveCount++;
            Events?.Add("receive");
            if (Messages is null || Messages.Count == 0)
            {
                if (ReceiveException is not null)
                {
                    return ValueTask.FromException<RemoteServerMessage>(ReceiveException);
                }

                return ValueTask.FromResult<RemoteServerMessage>(
                    new RemoteFramebufferMessage(
                        new RemoteFramebufferSize(1, 1), [0, 0, 0, 255], 4,
                        [new RemoteRectangle(0, 0, 1, 1)]));
            }

            return ValueTask.FromResult(
                Messages.Dequeue());
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Events?.Add("client-dispose");
            DisposeCallback?.Invoke();
            if (DisposeTask is not null)
            {
                return new ValueTask(DisposeTask);
            }

            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }

    private sealed class CountingOwner(int length) : IMemoryOwner<byte>
    {
        private byte[]? _buffer = new byte[length];
        public int DisposeCount { get; private set; }
        public Exception? DisposeException { get; init; }
        public Memory<byte> Memory => _buffer ?? throw new ObjectDisposedException(nameof(CountingOwner));
        public void Dispose()
        {
            DisposeCount++;
            _buffer = null;
            if (DisposeException is not null)
            {
                throw DisposeException;
            }
        }
    }

    private static RemoteCursorMessage CreateCursorMessage(CountingOwner owner) =>
        new(new RemoteCursorUpdate(0, 0, 1, 1, owner, 4));

    private static ConnectDeviceHandler CreateHandler(TestRfbClient client) =>
        new(
            new TestTransportFactory(),
            new TestSecretProvider(new TestConnectionSecret()),
            new TestRfbClientFactory(client),
            new ErrorMapper(() => "correlation-id"));

    private static RemoteFramebufferMessage CreateFramebufferMessage(
        CountingOwner owner,
        bool hasPixelContent = true) =>
        new(
            new RemoteFramebufferSize(1, 1),
            owner,
            4,
            4,
            [new RemoteRectangle(0, 0, 1, 1)],
            cursor: null,
            statistics: null,
            hasPixelContent);

    private sealed class TestAsyncDisposable : IAsyncDisposable
    {
        public List<string>? Events { get; init; }

        public Exception? DisposeException { get; init; }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Events?.Add("transport-dispose");
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }

    private sealed class CapturingErrorMapper : IErrorMapper
    {
        public Exception? Exception { get; private set; }

        public ConnectionStage? Stage { get; private set; }

        public WinArdError Map(Exception exception, ConnectionStage stage)
        {
            Exception = exception;
            Stage = stage;
            return WinArdError.Create(stage, "TEST_ERROR", "Safe test error.", "test-correlation");
        }
    }
}
