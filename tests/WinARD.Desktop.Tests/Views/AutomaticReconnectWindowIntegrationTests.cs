using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Views;

public sealed class AutomaticReconnectWindowIntegrationTests
{
    [Fact]
    public void Window_exposes_countdown_and_cancel_and_cleans_up_the_coordinator()
    {
        var xaml = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml"));
        var window = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Contains("x:Name=\"AutomaticReconnectPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AutomaticReconnectStatusText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OnCancelAutomaticReconnectClicked", xaml, StringComparison.Ordinal);
        Assert.Contains("new AutomaticReconnectCoordinator(", window, StringComparison.Ordinal);
        Assert.Contains("ReconnectFailureClassifier.IsTransient", window, StringComparison.Ordinal);
        Assert.Contains("_automaticReconnect?.Cancel();", window, StringComparison.Ordinal);
        Assert.Contains("await _automaticReconnect.DisposeAsync()", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_rethrows_safe_failures_only_for_the_automatic_loop()
    {
        var source = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("throwOnFailure", source, StringComparison.Ordinal);
        Assert.Contains("throw new ReconnectFailureException(error)", source, StringComparison.Ordinal);
        Assert.Contains("throwOnFailure: true", source, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. path]);
    }
}
