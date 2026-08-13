using Xunit;

namespace WinARD.Desktop.Tests.Views;

public sealed class DeviceLibraryIntegrationTests
{
    [Fact]
    public void DeviceLibraryUsesDoubleTapForActivationAndTextOnlyDetails()
    {
        var source = File.ReadAllText(RepositoryFile("src", "WinARD.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("list.DoubleTapped", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ActivateSelectedCommand.Execute(null)", source, StringComparison.Ordinal);
        Assert.Contains("无预览", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Screenshot", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Framebuffer", source, StringComparison.OrdinalIgnoreCase);
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
