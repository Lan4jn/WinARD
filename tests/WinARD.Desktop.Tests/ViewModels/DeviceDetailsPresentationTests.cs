using WinARD.Application.Ports;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using Xunit;

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class DeviceDetailsPresentationTests
{
    [Fact]
    public void SavedSshDeviceShowsUsernameAndSafeNetworkPath()
    {
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Studio", "mac.internal", 5900, "alex")
            .WithSsh(SshProfile.Create("jump.example", 2222, "ssh-user", null,
                "mac.internal", 5900, null, null, null));

        var details = DeviceDetailsPresentation.From(DeviceItemViewModel.FromProfile(profile));

        Assert.Equal("alex", details.Username);
        Assert.Equal("SSH jump.example:2222 → mac.internal:5900", details.NetworkPath);
        Assert.Equal("无预览", details.PreviewPlaceholder);
    }

    [Fact]
    public void DiscoveredDeviceShowsSafeSummaryOnly()
    {
        var discovery = new DiscoveredDevice("nearby", "Nearby", "nearby.local", 5900,
            new Dictionary<string, string>(), DateTimeOffset.UtcNow.AddMinutes(1));

        var details = DeviceDetailsPresentation.From(DeviceItemViewModel.FromDiscovery(discovery));

        Assert.Equal("未设置", details.Username);
        Assert.Equal("Bonjour nearby.local:5900", details.NetworkPath);
        Assert.Equal("无预览", details.PreviewPlaceholder);
    }
}
