using WinARD.Domain.Devices;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Domain.Tests;

public sealed class DeviceTests
{
    [Fact]
    public void Create_validates_and_trims_values()
    {
        var created = DateTimeOffset.UtcNow;
        var device = Device.Create(Guid.NewGuid(), " Office Mac ", created, created);

        Assert.Equal("Office Mac", device.DisplayName);
        Assert.Throws<ArgumentException>(() => Device.Create(Guid.Empty, "Mac", created, created));
        Assert.Throws<ArgumentException>(() => Device.Create(Guid.NewGuid(), " ", created, created));
        Assert.Throws<ArgumentException>(() => Device.Create(Guid.NewGuid(), "Mac", created, created.AddTicks(-1)));
    }

    [Fact]
    public void Create_normalizes_timestamps_to_utc_without_changing_instants()
    {
        var created = new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.FromHours(8));
        var updated = created.AddMinutes(5);

        var device = Device.Create(Guid.NewGuid(), "Mac", created, updated);

        Assert.Equal(created, device.CreatedUtc);
        Assert.Equal(updated, device.UpdatedUtc);
        Assert.Equal(TimeSpan.Zero, device.CreatedUtc.Offset);
        Assert.Equal(TimeSpan.Zero, device.UpdatedUtc.Offset);
    }
}
