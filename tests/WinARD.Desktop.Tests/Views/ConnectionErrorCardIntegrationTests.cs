using Xunit;

namespace WinARD.Desktop.Tests.Views;

public sealed class ConnectionErrorCardIntegrationTests
{
    [Fact]
    public void CardExposesStableAutomationSurface()
    {
        var xaml = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "ConnectionErrorCard.xaml"));

        Assert.Contains("ConnectionErrorCard", xaml, StringComparison.Ordinal);
        Assert.Contains("ConnectionErrorTitle", xaml, StringComparison.Ordinal);
        Assert.Contains("ConnectionErrorSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("ConnectionErrorCorrelationId", xaml, StringComparison.Ordinal);
        Assert.Contains("ConnectionErrorActions", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowAndEditorHostTheActionableCard()
    {
        var main = File.ReadAllText(RepositoryFile("src", "WinARD.Desktop", "MainWindow.xaml.cs"));
        var editor = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "ConnectionEditorDialog.xaml"));

        Assert.Contains("ConnectionErrorCard", main, StringComparison.Ordinal);
        Assert.Contains("ConnectionErrorCard", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticPickerIsInitializedWithTheOwningWindow()
    {
        var service = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Services", "DiagnosticExportService.cs"));

        Assert.Contains("FileSavePicker", service, StringComparison.Ordinal);
        Assert.Contains("WindowNative.GetWindowHandle", service, StringComparison.Ordinal);
        Assert.Contains("InitializeWithWindow.Initialize", service, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(0", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorPresentationDoesNotReadRawExceptionMessage()
    {
        var files = new[]
        {
            RepositoryFile("src", "WinARD.Desktop", "MainWindow.xaml.cs"),
            RepositoryFile("src", "WinARD.Desktop", "Views", "ConnectionEditorDialog.xaml.cs"),
            RepositoryFile("src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"),
            RepositoryFile("src", "WinARD.Desktop", "ViewModels", "ConnectionErrorViewModel.cs"),
        };

        foreach (var file in files)
        {
            Assert.DoesNotContain("exception.Message", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException("Could not locate repository root.")
            : Path.Combine([directory.FullName, .. segments]);
    }
}
