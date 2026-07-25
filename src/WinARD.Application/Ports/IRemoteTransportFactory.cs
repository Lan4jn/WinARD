using WinARD.Domain.Connections;
using System.Runtime.ExceptionServices;

namespace WinARD.Application.Ports;

public interface IRemoteTransportFactory
{
    Task<TransportConnection> ConnectAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);
}

public sealed record EndPointDescription
{
    public EndPointDescription(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Endpoint host cannot be blank.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Endpoint port must be between 1 and 65535.");
        }

        Host = host.Trim();
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }
}

public sealed class TransportConnection : IAsyncDisposable
{
    private readonly IAsyncDisposable? _lifetime;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    public TransportConnection(Stream stream, EndPointDescription endPoint)
        : this(stream, endPoint, lifetime: null)
    {
    }

    public TransportConnection(
        Stream stream,
        EndPointDescription endPoint,
        IAsyncDisposable? lifetime)
    {
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        EndPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));
        _lifetime = lifetime;
    }

    public Stream Stream { get; }

    public EndPointDescription EndPoint { get; }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        Exception? streamException = null;
        try
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            streamException = exception;
        }

        try
        {
            if (_lifetime is not null)
            {
                await _lifetime.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception lifetimeException) when (streamException is not null)
        {
            throw new AggregateException(streamException, lifetimeException);
        }

        if (streamException is not null)
        {
            ExceptionDispatchInfo.Capture(streamException).Throw();
        }
    }
}
