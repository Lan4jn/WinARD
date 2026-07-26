namespace WinARD.Application.Ports;

public enum DiscoveryChangeKind
{
    Added,
    Updated,
    Removed,
}

public sealed record DiscoveredDevice(
    string Identity,
    string DisplayName,
    string Host,
    int Port,
    IReadOnlyDictionary<string, string> TxtRecords,
    DateTimeOffset ExpiresUtc);

public sealed record DiscoveryChange(DiscoveryChangeKind Kind, DiscoveredDevice Device);

public interface IDeviceDiscovery : IAsyncDisposable
{
    event EventHandler<DiscoveryChange>? Changed;

    IReadOnlyList<DiscoveredDevice> Current { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
