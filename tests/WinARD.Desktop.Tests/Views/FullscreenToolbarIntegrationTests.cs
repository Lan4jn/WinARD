using Xunit;

namespace WinARD.Desktop.Tests.Views;

public sealed class FullscreenToolbarIntegrationTests
{
    [Fact]
    public void ToolbarIsNamedOverlayWithFullscreenOnlyHotZoneAndBoundedTimer()
    {
        var xaml = File.ReadAllText(RepositoryFile("src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml"));
        var source = File.ReadAllText(RepositoryFile("src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Contains("x:Name=\"SessionCommandBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FullscreenToolbarLayer\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FullscreenToolbarHotZone\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.RowSpan=\"3\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(2)", source, StringComparison.Ordinal);
        Assert.Contains("_fullscreenToolbarController.IsHotZoneEnabled", source, StringComparison.Ordinal);
        Assert.Contains("FullscreenToolbarHideScheduler", source, StringComparison.Ordinal);
        Assert.Contains("Cancel()", source, StringComparison.Ordinal);
    }


    [Fact]
    public void FullscreenRecallShortcutIsDiscoverableAndConsumedBeforeRemoteInput()
    {
        var xaml = File.ReadAllText(RepositoryFile("src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml"));
        var source = File.ReadAllText(RepositoryFile("src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Contains("Ctrl+Alt+T", xaml, StringComparison.Ordinal);
        Assert.Contains("HandleFullscreenToolbarRecall", source, StringComparison.Ordinal);
        Assert.Contains("_consumeToolbarRecallKeys", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseInputAsync", source, StringComparison.Ordinal);
        Assert.Contains("args.Handled = true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapeIsHandledLocallyBeforeRemoteKeyboardMapping()
    {
        var source = File.ReadAllText(RepositoryFile("src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));
        var escape = source.IndexOf("HandleLocalEscape()", StringComparison.Ordinal);
        var remote = source.IndexOf("_textInput.OnPhysicalKeyDown", StringComparison.Ordinal);

        Assert.True(escape >= 0 && escape < remote);
        Assert.Contains("args.Handled = true", source[escape..remote], StringComparison.Ordinal);
        Assert.Contains("FullscreenEscapeAction.CloseDropDown", source, StringComparison.Ordinal);
        Assert.Contains("FullscreenEscapeAction.CloseQualityOverlay", source, StringComparison.Ordinal);
        Assert.Contains("FullscreenEscapeAction.ExitFullscreen", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowCloseUsesIdempotentBestEffortSchedulerCleanup()
    {
        var source = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Contains("RemoteSessionWindowCloseCoordinator", source, StringComparison.Ordinal);
        Assert.Contains("_fullscreenToolbarHideScheduler.DisposeAsync", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _closedCleanupStarted, 1)", source, StringComparison.Ordinal);
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
