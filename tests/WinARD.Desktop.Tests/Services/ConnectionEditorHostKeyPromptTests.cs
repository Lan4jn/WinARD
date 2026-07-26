using WinARD.Desktop.Services;
using WinARD.Domain.Connections;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class ConnectionEditorHostKeyPromptTests
{
    [Theory]
    [InlineData(false, SshHostKeyPromptDecision.Trust)]
    [InlineData(true, SshHostKeyPromptDecision.Replace)]
    public async Task PromptExposesSanitizedInlineStateAndCompletesWithExpectedDecision(
        bool changed,
        SshHostKeyPromptDecision expectedDecision)
    {
        using var sut = new ConnectionEditorHostKeyPrompt();
        var endpoint = new SshHostKeyEndpoint("jump.local", 2222);
        var request = new SshHostKeyPromptRequest(
            endpoint,
            "ssh-ed25519",
            "SHA256:new",
            changed ? "SHA256:old" : null,
            changed);

        var pending = sut.PromptAsync(request, CancellationToken.None).AsTask();

        Assert.True(sut.State.IsVisible);
        Assert.Equal(endpoint, sut.State.Endpoint);
        Assert.Equal("ssh-ed25519", sut.State.Algorithm);
        Assert.Equal("SHA256:new", sut.State.NewFingerprint);
        Assert.Equal(request.PreviousFingerprint, sut.State.PreviousFingerprint);
        Assert.Equal(changed, sut.State.IsChanged);
        if (changed)
        {
            sut.Replace();
        }
        else
        {
            sut.Trust();
        }

        Assert.Equal(expectedDecision, await pending);
        Assert.False(sut.State.IsVisible);
    }

    [Fact]
    public async Task DisposeCompletesPendingPromptAsCancel()
    {
        var sut = new ConnectionEditorHostKeyPrompt();
        var pending = sut.PromptAsync(
            new SshHostKeyPromptRequest(
                new SshHostKeyEndpoint("jump.local", 22),
                "ssh-ed25519",
                "SHA256:new",
                PreviousFingerprint: null,
                IsChanged: false),
            CancellationToken.None).AsTask();

        sut.Dispose();

        Assert.Equal(SshHostKeyPromptDecision.Cancel, await pending);
        Assert.False(sut.State.IsVisible);
    }
}
