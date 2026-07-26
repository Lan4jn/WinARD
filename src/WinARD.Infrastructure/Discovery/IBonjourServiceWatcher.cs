namespace WinARD.Infrastructure.Discovery;

public interface IBonjourServiceWatcher : IAsyncDisposable
{
    event EventHandler<BonjourServiceChange>? Changed;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public sealed record BonjourService(
    string ServiceIdentity,
    uint InterfaceIndex,
    string DisplayName,
    string Host,
    int Port,
    IReadOnlyDictionary<string, string> TxtRecords,
    TimeSpan TimeToLive);

public enum BonjourServiceChangeKind
{
    Added,
    Updated,
    Removed,
}

public sealed record BonjourServiceChange(
    BonjourServiceChangeKind Kind,
    string ServiceIdentity,
    uint InterfaceIndex,
    BonjourService? Service)
{
    public static BonjourServiceChange Added(BonjourService service) =>
        new(BonjourServiceChangeKind.Added, service.ServiceIdentity, service.InterfaceIndex, service);

    public static BonjourServiceChange Updated(BonjourService service) =>
        new(BonjourServiceChangeKind.Updated, service.ServiceIdentity, service.InterfaceIndex, service);

    public static BonjourServiceChange Removed(string serviceIdentity, uint interfaceIndex) =>
        new(BonjourServiceChangeKind.Removed, serviceIdentity, interfaceIndex, null);
}
