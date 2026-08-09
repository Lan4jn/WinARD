using WinARD.Desktop.Views;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Views;

public sealed class RemoteSessionDiagnosticExportStateTests
{
    [Fact]
    public void Completing_export_after_closing_does_not_reenable_export()
    {
        var state = new RemoteSessionDiagnosticExportState(serviceAvailable: true);

        Assert.True(state.IsEnabled);
        Assert.True(state.TryBeginExport());
        Assert.False(state.IsEnabled);

        state.BeginClosing();
        state.CompleteExport();

        Assert.False(state.IsEnabled);
        Assert.False(state.TryBeginExport());
    }

    [Fact]
    public void Missing_service_never_enables_export()
    {
        var state = new RemoteSessionDiagnosticExportState(serviceAvailable: false);

        Assert.False(state.IsEnabled);
        Assert.False(state.TryBeginExport());
    }
}
