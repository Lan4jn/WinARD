using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Views;

public sealed class SettingsDialogIntegrationTests
{
    [Fact]
    public void Main_window_hosts_settings_and_new_session_defaults()
    {
        var main = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "MainWindow.xaml.cs"));
        var dialog = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "SettingsDialog.xaml"));

        Assert.Contains("ApplicationSettingsButton", main, StringComparison.Ordinal);
        Assert.Contains("CredentialBackendMigrationService", main, StringComparison.Ordinal);
        Assert.Contains("ConfirmSourceCleanupAsync", main, StringComparison.Ordinal);
        Assert.Contains("ApplyApplicationDefaults", main, StringComparison.Ordinal);
        Assert.Contains("LockVaultNowButton", dialog, StringComparison.Ordinal);
        Assert.Contains("DiagnosticLevelBox", dialog, StringComparison.Ordinal);
        Assert.Contains("ClipboardDefaultBox", dialog, StringComparison.Ordinal);
        Assert.Contains("CredentialBackendBox", dialog, StringComparison.Ordinal);
        Assert.Contains("VaultTimeoutBox", dialog, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] segments) => Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..")),
        Path.Combine(segments));
}
