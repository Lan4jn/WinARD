using WinARD.Desktop.Services;
using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Services;

public sealed class RemoteSessionReconnectRequestTests
{
    [Fact]
    public async Task Capture_is_atomic_and_invoke_uses_latest_desired_profile()
    {
        var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user");
        var desired = original.WithQualityProfile(QualityProfile.Smooth);
        ConnectionProfile? observed = null;
        var request = new RemoteSessionReconnectRequest(
            original,
            (profile, _) =>
            {
                observed = profile;
                return Task.CompletedTask;
            });

        request.Capture(desired);
        await request.InvokeAsync(default);

        Assert.Same(desired, observed);
    }
}
