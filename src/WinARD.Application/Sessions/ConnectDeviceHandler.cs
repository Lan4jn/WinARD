using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Domain.Sessions;

namespace WinARD.Application.Sessions;

public sealed record ConnectResult(SessionState State, RemoteSession? Session, WinArdError? Error);

public sealed class ConnectDeviceHandler
{
    private readonly IRemoteTransportFactory _transportFactory;
    private readonly IConnectionSecretProvider _secretProvider;
    private readonly IRfbClientFactory _clientFactory;
    private readonly IErrorMapper _errorMapper;

    public ConnectDeviceHandler(
        IRemoteTransportFactory transportFactory,
        IConnectionSecretProvider secretProvider,
        IRfbClientFactory clientFactory,
        IErrorMapper errorMapper)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _errorMapper = errorMapper ?? throw new ArgumentNullException(nameof(errorMapper));
    }

    public async Task<ConnectResult> HandleAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken) =>
        await HandleAsync(profile, stageChanged: null, cancellationToken).ConfigureAwait(false);

    public async Task<ConnectResult> HandleAsync(
        ConnectionProfile profile,
        Action<ConnectionStage>? stageChanged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var stateMachine = new SessionStateMachine();
        ISecret? secret = null;
        TransportConnection? transport = null;
        IRfbClient? client = null;

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
            stateMachine.MoveTo(SessionState.Connected);
            stageChanged?.Invoke(ConnectionStage.Connected);

            return new ConnectResult(
                stateMachine.Current,
                new RemoteSession(stateMachine, transport, client),
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupAsync(client, transport, secret).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            var failedAt = ToConnectionStage(stateMachine.Current);
            await CleanupAsync(client, transport, secret).ConfigureAwait(false);
            stateMachine.MoveTo(SessionState.Failed);
            return new ConnectResult(
                stateMachine.Current,
                null,
                _errorMapper.Map(exception, failedAt));
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
