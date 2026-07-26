using WinARD.Desktop.Services;
using WinARD.Desktop.ViewModels;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class ConnectionErrorActionHandlerTests
{
    [Theory]
    [InlineData(ConnectionErrorActionKind.Retry)]
    [InlineData(ConnectionErrorActionKind.ReplaceHostKey)]
    [InlineData(ConnectionErrorActionKind.ExportDiagnostics)]
    public async Task RegisteredActionInvokesItsConcreteHandler(ConnectionErrorActionKind action)
    {
        var calls = 0;
        var sut = new ConnectionErrorActionHandler(
            [new(action, _ => { calls++; return Task.CompletedTask; })]);

        await sut.HandleAsync(action, CancellationToken.None);

        Assert.True(sut.CanHandle(action));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UnsupportedActionIsDisabledAndNeverSilentlyIgnored()
    {
        var sut = new ConnectionErrorActionHandler([]);

        Assert.False(sut.CanHandle(ConnectionErrorActionKind.Retry));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.HandleAsync(ConnectionErrorActionKind.Retry, CancellationToken.None));
    }

    [Fact]
    public async Task RemoteRetryClosesOldOwnershipBeforeStartingOneNewConnection()
    {
        var sequence = new List<string>();
        var connectCalls = 0;
        var sut = new RemoteSessionRetryAction(
            () => { sequence.Add("close"); return Task.CompletedTask; },
            _ => { sequence.Add("connect"); connectCalls++; return Task.CompletedTask; });

        await sut.ExecuteAsync(CancellationToken.None);

        Assert.Equal(["close", "connect"], sequence);
        Assert.Equal(1, connectCalls);
    }
}
