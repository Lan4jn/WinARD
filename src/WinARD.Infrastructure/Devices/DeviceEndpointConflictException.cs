namespace WinARD.Infrastructure.Devices;

public sealed class DeviceEndpointConflictException : InvalidOperationException
{
    public DeviceEndpointConflictException(string host, int port, Exception innerException)
        : base($"A device already exists for endpoint {host}:{port}.", innerException)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }
}
