using WinARD.Domain.Errors;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Domain.Tests;

public sealed class WinArdErrorTests
{
    [Fact]
    public void Create_rejects_blank_fields_and_trims_values()
    {
        var error = WinArdError.Create(ConnectionStage.Connecting, " timeout ", " Could not connect ", " corr-1 ");

        Assert.Equal("timeout", error.Code);
        Assert.Equal("Could not connect", error.UserMessage);
        Assert.Equal("corr-1", error.CorrelationId);
        Assert.Throws<ArgumentException>(() => WinArdError.Create(ConnectionStage.Connecting, " ", "Message", "id"));
    }

    [Fact]
    public void Create_rejects_undefined_connection_stage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WinArdError.Create((ConnectionStage)999, "timeout", "Could not connect", "corr-1"));
    }
}
