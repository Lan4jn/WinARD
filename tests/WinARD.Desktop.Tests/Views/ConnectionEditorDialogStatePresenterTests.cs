using WinARD.Application.Ports;
using WinARD.Desktop.ViewModels;
using WinARD.Desktop.Views;
using WinARD.Domain.Connections;
using WinARD.Domain.Security;
using Xunit;

namespace WinARD.Desktop.Tests.Views;

public sealed class ConnectionEditorDialogStatePresenterTests
{
    [Fact]
    public void AuthenticationModeHandlerClearsVisibleAndPendingSecretsOnlyWhenModeChanges()
    {
        var source = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "ConnectionEditorDialog.xaml.cs"));

        Assert.Contains("selected != ViewModel.SshAuthenticationMode", source, StringComparison.Ordinal);
        Assert.Contains("SshPasswordBox.Password = string.Empty", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.HasSshAuthenticationSecret = false", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _pendingHostKeyRetrySecret, null)?.Dispose()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticationModeControlsVisibleInput()
    {
        var viewModel = new ConnectionEditorViewModel(
            null,
            (saved, _, _, _) => Task.FromResult(saved),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

        viewModel.SshAuthenticationMode = SshAuthenticationMode.Password;
        Assert.True(ConnectionEditorDialogStatePresenter.Present(viewModel).ShowSshSecret);
        Assert.Equal("SSH 密码", ConnectionEditorDialogStatePresenter.Present(viewModel).SshSecretTitle);
        Assert.False(ConnectionEditorDialogStatePresenter.Present(viewModel).ShowPrivateKeyPath);

        viewModel.SshAuthenticationMode = SshAuthenticationMode.PrivateKey;
        Assert.True(ConnectionEditorDialogStatePresenter.Present(viewModel).ShowSshSecret);
        Assert.Equal("私钥口令（可选）", ConnectionEditorDialogStatePresenter.Present(viewModel).SshSecretTitle);
        Assert.True(ConnectionEditorDialogStatePresenter.Present(viewModel).ShowPrivateKeyPath);
    }
    [Fact]
    public void UnknownCredentialStateIsVisibleImmediatelyAndUpdatesAfterExplicitModeSelection()
    {
        var profile = ConnectionProfile.Create(
                Guid.NewGuid(), "Legacy", "legacy.local", 5900, "operator")
            .WithCredential(CredentialReference.Create("legacy-plugin", "shared/mac"));
        var viewModel = new ConnectionEditorViewModel(
            profile,
            (saved, _, _, _) => Task.FromResult(saved),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<ConnectionTestStageResult>>([]));

        var initial = ConnectionEditorDialogStatePresenter.Present(viewModel);

        Assert.True(initial.SaveEnabled);
        Assert.False(initial.TestEnabled);
        Assert.Contains("不受支持的后端", initial.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("选择受支持的凭据保存方式", initial.StatusMessage, StringComparison.Ordinal);

        viewModel.CredentialSaveMode = CredentialSaveMode.AskEveryTime;
        var updated = ConnectionEditorDialogStatePresenter.Present(viewModel);

        Assert.True(updated.SaveEnabled);
        Assert.True(updated.TestEnabled);
        Assert.Equal(string.Empty, updated.StatusMessage);
    }

    private static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, Path.Combine(parts));
    }
}
