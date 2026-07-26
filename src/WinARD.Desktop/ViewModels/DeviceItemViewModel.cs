using WinARD.Application.Ports;
using WinARD.Domain.Connections;

namespace WinARD.Desktop.ViewModels;

public sealed class DeviceItemViewModel
{
    private DeviceItemViewModel(
        string identity,
        string displayName,
        string host,
        int port,
        ConnectionProfile? profile,
        bool isDiscovered)
    {
        Identity = identity;
        DisplayName = displayName;
        Host = host;
        Port = port;
        Profile = profile;
        IsDiscovered = isDiscovered;
    }

    public string Identity { get; }
    public string DisplayName { get; }
    public string Host { get; }
    public int Port { get; }
    public string Endpoint => $"{Host}:{Port}";
    public string SourceLabel => IsDiscovered ? "附近设备" : "已保存";
    public bool IsDiscovered { get; }
    public ConnectionProfile? Profile { get; }

    public static DeviceItemViewModel FromProfile(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new DeviceItemViewModel(
            profile.Id.ToString("D"),
            profile.DisplayName,
            profile.Host,
            profile.Port,
            profile,
            isDiscovered: false);
    }

    public static DeviceItemViewModel FromDiscovery(DiscoveredDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceItemViewModel(
            device.Identity,
            device.DisplayName,
            device.Host,
            device.Port,
            profile: null,
            isDiscovered: true);
    }

    public override string ToString() => $"{DisplayName}  —  {Endpoint}";
}
