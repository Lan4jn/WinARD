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
        Assert.Contains("dialogBase.Revision", main, StringComparison.Ordinal);
        Assert.Contains("result.CommittedSettingsRevision", main, StringComparison.Ordinal);
        Assert.Contains("ConfirmSourceCleanupAsync", main, StringComparison.Ordinal);
        Assert.Contains("if (!requiresVault || _vaultSession.IsUnlocked)", main, StringComparison.Ordinal);
        Assert.Contains("_vaultSession.UnlockAsync(master, cancellationToken)", main, StringComparison.Ordinal);
        Assert.Contains("解锁迁移所需的凭据保险库", main, StringComparison.Ordinal);
        Assert.DoesNotContain("解锁目标凭据保险库", main, StringComparison.Ordinal);
        Assert.Contains("ApplyApplicationDefaults", main, StringComparison.Ordinal);
        Assert.Contains("LockVaultNowButton", dialog, StringComparison.Ordinal);
        Assert.Contains("DiagnosticLevelBox", dialog, StringComparison.Ordinal);
        Assert.Contains("ClipboardDefaultBox", dialog, StringComparison.Ordinal);
        Assert.Contains("CredentialBackendBox", dialog, StringComparison.Ordinal);
        Assert.Contains("旧源密码默认保留", dialog, StringComparison.Ordinal);
        Assert.Contains("VaultTimeoutBox", dialog, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
            {
                return Path.Combine(directory.FullName, Path.Combine(segments));
            }
        }
        throw new DirectoryNotFoundException("The repository root could not be located.");
    }
}
