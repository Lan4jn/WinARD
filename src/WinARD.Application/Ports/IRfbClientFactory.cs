namespace WinARD.Application.Ports;

public interface IRfbClientFactory
{
    IRfbClient Create(Stream stream);
}

public interface IRfbClient : IAsyncDisposable
{
    Task NegotiateAsync(CancellationToken cancellationToken);

    Task AuthenticateAsync(
        string username,
        ISecret secret,
        CancellationToken cancellationToken);

    Task InitializeAsync(CancellationToken cancellationToken);
}
