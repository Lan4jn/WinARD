using WinARD.Desktop.ViewModels;
using WinARD.Domain.Errors;
using Xunit;

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class ConnectionErrorViewModelTests
{
    [Theory]
    [InlineData("MACOS_SCREEN_RECORDING_DENIED", ConnectionErrorActionKind.OpenHelp)]
    [InlineData("ARD_AUTH_REJECTED", ConnectionErrorActionKind.ReenterCredentials)]
    [InlineData("VAULT_LOCKED", ConnectionErrorActionKind.UnlockVault)]
    [InlineData("RFB_VERSION_UNSUPPORTED", ConnectionErrorActionKind.ExportDiagnostics)]
    [InlineData("TCP_CONNECTION_FAILED", ConnectionErrorActionKind.Retry)]
    [InlineData("DNS_RESOLUTION_FAILED", ConnectionErrorActionKind.Retry)]
    [InlineData("UNKNOWN_CODE", ConnectionErrorActionKind.CopyCorrelationId)]
    public void StableCodesMapToActionableSafeCards(string code, ConnectionErrorActionKind expected)
    {
        var error = WinArdError.Create(
            ConnectionStage.Authenticating,
            code,
            "This mapped message is ignored",
            "corr-safe-123");

        var viewModel = ConnectionErrorViewModel.FromError(error);

        Assert.False(string.IsNullOrWhiteSpace(viewModel.Title));
        Assert.False(string.IsNullOrWhiteSpace(viewModel.Summary));
        Assert.Equal("corr-safe-123", viewModel.CorrelationId);
        Assert.Contains(viewModel.Actions, action => action.Kind == expected);
        Assert.Contains(viewModel.Actions, action => action.Kind == ConnectionErrorActionKind.ExportDiagnostics);
        Assert.DoesNotContain(error.UserMessage, viewModel.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedHostKeyShowsOnlyFingerprintsAndDefaultsToCancel()
    {
        var error = WinArdError.Create(
            ConnectionStage.Connecting,
            "SSH_HOST_KEY_CHANGED",
            "raw exception must not be shown",
            "corr-host-key");

        var viewModel = ConnectionErrorViewModel.FromError(
            error,
            oldFingerprint: "SHA256:old-safe-fingerprint",
            newFingerprint: "SHA256:new-safe-fingerprint");

        Assert.Contains("SHA256:old-safe-fingerprint", viewModel.Summary, StringComparison.Ordinal);
        Assert.Contains("SHA256:new-safe-fingerprint", viewModel.Summary, StringComparison.Ordinal);
        Assert.Equal(ConnectionErrorActionKind.Cancel, viewModel.Actions[0].Kind);
        Assert.Contains(viewModel.Actions, action => action.Kind == ConnectionErrorActionKind.ReplaceHostKey);
        Assert.DoesNotContain("raw exception", viewModel.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionRecordRejectsBlankLabels()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionErrorAction(" ", ConnectionErrorActionKind.Retry));
    }

    [Fact]
    public void InterruptedRemoteSessionOffersRetryAndDisconnect()
    {
        var error = WinArdError.Create(
            ConnectionStage.Connected,
            "REMOTE_SESSION_INTERRUPTED",
            "ignored",
            "corr-remote");

        var viewModel = ConnectionErrorViewModel.FromError(error);

        Assert.Contains(viewModel.Actions, action => action.Kind == ConnectionErrorActionKind.Retry);
        Assert.Contains(viewModel.Actions, action => action.Kind == ConnectionErrorActionKind.Disconnect);
        Assert.Contains(viewModel.Actions, action => action.Kind == ConnectionErrorActionKind.CopyCorrelationId);
        Assert.Contains(viewModel.Actions, action => action.Kind == ConnectionErrorActionKind.ExportDiagnostics);
    }

    [Fact]
    public void RfbConnectionRejectedDoesNotMisleadUserToReenterCredentials()
    {
        var error = WinArdError.Create(
            ConnectionStage.Negotiating,
            "RFB_CONNECTION_REJECTED",
            "ignored",
            "corr-rejected");

        var viewModel = ConnectionErrorViewModel.FromError(error);

        Assert.DoesNotContain(
            viewModel.Actions,
            action => action.Kind == ConnectionErrorActionKind.ReenterCredentials);
        Assert.Contains(viewModel.Actions, action => action.Kind == ConnectionErrorActionKind.Retry);
        Assert.Contains(viewModel.Actions, action => action.Kind == ConnectionErrorActionKind.CopyCorrelationId);
    }
}
