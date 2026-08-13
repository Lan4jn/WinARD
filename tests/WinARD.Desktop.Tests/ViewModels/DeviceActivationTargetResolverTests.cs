using WinARD.Application.Ports;
using WinARD.Desktop.ViewModels;
using Xunit;

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class DeviceActivationTargetResolverTests
{
    [Fact]
    public void ResolvesOnlyDeviceItemDataContext()
    {
        var device = DeviceItemViewModel.FromDiscovery(new DiscoveredDevice(
            "nearby", "Nearby", "nearby.local", 5900,
            new Dictionary<string, string>(), DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.Same(device, DeviceActivationTargetResolver.Resolve(device));
        Assert.Null(DeviceActivationTargetResolver.Resolve(new object()));
        Assert.Null(DeviceActivationTargetResolver.Resolve(null));
    }
}
