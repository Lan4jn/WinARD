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
}
