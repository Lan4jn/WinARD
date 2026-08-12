using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Domain.Sessions;

namespace WinARD.Application.Sessions;

public sealed record ConnectResult(SessionState State, RemoteSession? Session, WinArdError? Error);

internal sealed record ConnectionFailureObservation(Exception Exception, WinArdError Error);

public sealed class ConnectDeviceHandler
{
    private readonly IRemoteTransportFactory _transportFactory;
    private readonly IConnectionSecretProvider _secretProvider;
    private readonly IRfbClientFactory _clientFactory;
    private readonly IErrorMapper _errorMapper;
    private readonly QualityDecoderGates _decoderGates;

    public ConnectDeviceHandler(
        IRemoteTransportFactory transportFactory,
        IConnectionSecretProvider secretProvider,
        IRfbClientFactory clientFactory,
        IErrorMapper errorMapper)
        : this(transportFactory, secretProvider, clientFactory, errorMapper, new QualityDecoderGates())
    {
    }

    public ConnectDeviceHandler(
        IRemoteTransportFactory transportFactory,
        IConnectionSecretProvider secretProvider,
        IRfbClientFactory clientFactory,
        IErrorMapper errorMapper,
        QualityDecoderGates decoderGates)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _errorMapper = errorMapper ?? throw new ArgumentNullException(nameof(errorMapper));
        _decoderGates = decoderGates ?? throw new ArgumentNullException(nameof(decoderGates));
    }

    public async Task<ConnectResult> HandleAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken) =>
        await HandleAsync(profile, QualityBootstrapAttempt.Preferred, stageChanged: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ConnectResult> HandleAsync(
        ConnectionProfile profile,
        QualityBootstrapAttempt attempt,
        CancellationToken cancellationToken) =>
        await HandleAsync(profile, attempt, stageChanged: null, cancellationToken).ConfigureAwait(false);

    public async Task<ConnectResult> HandleAsync(
        ConnectionProfile profile,
        Action<ConnectionStage>? stageChanged,
        CancellationToken cancellationToken) =>
        await HandleAsync(profile, QualityBootstrapAttempt.Preferred, stageChanged, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ConnectResult> HandleAsync(
        ConnectionProfile profile,
        QualityBootstrapAttempt attempt,
        Action<ConnectionStage>? stageChanged,
        CancellationToken cancellationToken) =>
        await HandleCoreAsync(
            profile,
            attempt,
            preferredFailureReason: null,
            stageChanged,
            failureObserved: null,
            cancellationToken)
            .ConfigureAwait(false);

    internal async Task<ConnectResult> HandleWithFailureObservationAsync(
        ConnectionProfile profile,
        Action<ConnectionStage>? stageChanged,
        Func<ConnectionFailureObservation, ValueTask> failureObserved,
        CancellationToken cancellationToken) =>
        await HandleWithFailureObservationAsync(
            profile,
            QualityBootstrapAttempt.Preferred,
            preferredFailureReason: null,
            stageChanged,
            failureObserved,
            cancellationToken).ConfigureAwait(false);

    internal async Task<ConnectResult> HandleWithFailureObservationAsync(
        ConnectionProfile profile,
        QualityBootstrapAttempt attempt,
        QualityBootstrapFailureReason? preferredFailureReason,
        Action<ConnectionStage>? stageChanged,
        Func<ConnectionFailureObservation, ValueTask> failureObserved,
        CancellationToken cancellationToken) =>
        await HandleCoreAsync(
            profile,
            attempt,
            preferredFailureReason,
            stageChanged,
            failureObserved ?? throw new ArgumentNullException(nameof(failureObserved)),
            cancellationToken).ConfigureAwait(false);

    private async Task<ConnectResult> HandleCoreAsync(
        ConnectionProfile profile,
        QualityBootstrapAttempt attempt,
        QualityBootstrapFailureReason? preferredFailureReason,
        Action<ConnectionStage>? stageChanged,
        Func<ConnectionFailureObservation, ValueTask>? failureObserved,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(attempt))
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        if (preferredFailureReason is { } failureReason && !Enum.IsDefined(failureReason))
        {
            throw new ArgumentOutOfRangeException(nameof(preferredFailureReason));
        }

        if (preferredFailureReason is not null && attempt != QualityBootstrapAttempt.Fallback)
        {
            throw new ArgumentException(
                "A preferred failure reason is only valid for a fallback attempt.",
                nameof(preferredFailureReason));
        }

        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var stateMachine = new SessionStateMachine();
        ISecret? secret = null;
        TransportConnection? transport = null;
        IRfbClient? client = null;
        var sessionOwnsResources = false;
        var preloadedMessages = new List<RemoteServerMessage>();
        using var preloadedOwnership = new PreloadedMessageOwnership(preloadedMessages);

        try
        {
            stateMachine.MoveTo(SessionState.Resolving);
            stageChanged?.Invoke(ConnectionStage.Resolving);
            secret = await _secretProvider.GetSecretAsync(profile, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            stateMachine.MoveTo(SessionState.Connecting);
            stageChanged?.Invoke(ConnectionStage.Connecting);
            transport = await _transportFactory.ConnectAsync(profile, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            stateMachine.MoveTo(SessionState.Negotiating);
            stageChanged?.Invoke(ConnectionStage.Negotiating);
            client = _clientFactory.Create(transport.Stream);
            await client.NegotiateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            stateMachine.MoveTo(SessionState.Authenticating);
            stageChanged?.Invoke(ConnectionStage.Authenticating);
            await client.AuthenticateAsync(profile.MacUsername, secret, cancellationToken).ConfigureAwait(false);
            secret.Dispose();
            secret = null;
            cancellationToken.ThrowIfCancellationRequested();
            stateMachine.MoveTo(SessionState.Initializing);
            stageChanged?.Invoke(ConnectionStage.Initializing);
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var plan = QualityBootstrapPlanner.CreatePlan(
                profile.Quality,
                client.QualityCapabilities,
                _decoderGates);
            var settings = attempt == QualityBootstrapAttempt.Preferred ? plan.Preferred : plan.Fallback;
            await client.ConfigureBootstrapAsync(settings, attempt, cancellationToken).ConfigureAwait(false);
            await client.RequestFramebufferUpdateAsync(incremental: false, cancellationToken).ConfigureAwait(false);
            const int bootstrapMessageBudget = 8;
            var bootstrapMessageCount = 0;
            while (true)
            {
                var message = await client.ReceiveBootstrapAsync(cancellationToken).ConfigureAwait(false);
                bootstrapMessageCount++;
                if (message is not RemoteCursorMessage and not RemoteFramebufferMessage)
                {
                    preloadedMessages.Add(message);
                    throw new InvalidOperationException("Bootstrap preload received an unsupported message type.");
                }

                if (message is RemoteFramebufferMessage { HasPixelContent: false } emptyFrame)
                {
                    emptyFrame.Dispose();
                    if (bootstrapMessageCount == bootstrapMessageBudget)
                    {
                        throw new InvalidOperationException("The bootstrap preload queue exceeded its limit.");
                    }

                    await client.RequestFramebufferUpdateAsync(incremental: false, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                preloadedMessages.Add(message);
                if (message is RemoteFramebufferMessage)
                {
                    break;
                }

                if (bootstrapMessageCount == bootstrapMessageBudget)
                {
                    throw new InvalidOperationException("The bootstrap preload queue exceeded its limit.");
                }

                await client.RequestFramebufferUpdateAsync(incremental: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            stateMachine.MoveTo(SessionState.Connected);
            stageChanged?.Invoke(ConnectionStage.Connected);
            var result = new ConnectResult(
                stateMachine.Current,
                new RemoteSession(
                    stateMachine,
                    transport,
                    client,
                    preloadedOwnership,
                    preferredFailureReason),
                null);
            sessionOwnsResources = true;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failedAt = ToConnectionStage(stateMachine.Current);
            var error = _errorMapper.Map(exception, failedAt);
            stateMachine.MoveTo(SessionState.Failed);
            if (failureObserved is not null)
            {
                try
                {
                    await failureObserved(new ConnectionFailureObservation(exception, error))
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            return new ConnectResult(
                stateMachine.Current,
                null,
                error);
        }
        finally
        {
            if (!sessionOwnsResources)
            {
                preloadedOwnership.Dispose();
                await CleanupAsync(client, transport, secret).ConfigureAwait(false);
            }
        }
    }

    private static async Task CleanupAsync(
        IRfbClient? client,
        TransportConnection? transport,
        ISecret? secret)
    {
        try
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            secret?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private static ConnectionStage ToConnectionStage(SessionState state) => state switch
    {
        SessionState.Resolving => ConnectionStage.Resolving,
        SessionState.Connecting => ConnectionStage.Connecting,
        SessionState.Negotiating => ConnectionStage.Negotiating,
        SessionState.Authenticating => ConnectionStage.Authenticating,
        SessionState.Initializing => ConnectionStage.Initializing,
        SessionState.Connected => ConnectionStage.Connected,
        SessionState.Reconnecting => ConnectionStage.Reconnecting,
        SessionState.Disconnecting => ConnectionStage.Disconnecting,
        _ => throw new InvalidOperationException($"Session state {state} has no connection stage."),
    };
}
