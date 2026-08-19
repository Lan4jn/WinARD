using WinARD.Domain.Settings;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Domain.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_preserve_current_safe_behavior()
    {
        var settings = AppSettings.Default;

        Assert.Equal(1, settings.Version);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(SafeDiagnosticLevel.Standard, settings.DiagnosticLevel);
        Assert.True(settings.ClipboardEnabledByDefault);
        Assert.Equal(CredentialBackend.Windows, settings.DefaultCredentialBackend);
        Assert.Equal(TimeSpan.FromMinutes(15), settings.VaultIdleTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void Rejects_vault_timeout_outside_safe_bounds(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AppSettings.Create(
            version: 1,
            AppTheme.Dark,
            SafeDiagnosticLevel.Verbose,
            clipboardEnabledByDefault: false,
            CredentialBackend.EncryptedVault,
            TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void Rejects_fractional_minute_vault_timeout_that_cannot_round_trip()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AppSettings.Create(
            1, AppTheme.System, SafeDiagnosticLevel.Standard, true,
            CredentialBackend.Windows, TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void Rejects_undefined_closed_set_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AppSettings.Create(
            1, (AppTheme)99, SafeDiagnosticLevel.Standard, true,
            CredentialBackend.Windows, TimeSpan.FromMinutes(15)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AppSettings.Create(
            1, AppTheme.System, (SafeDiagnosticLevel)99, true,
            CredentialBackend.Windows, TimeSpan.FromMinutes(15)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AppSettings.Create(
            1, AppTheme.System, SafeDiagnosticLevel.Standard, true,
            (CredentialBackend)99, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Safe_log_levels_cannot_enable_content_or_pixels()
    {
        Assert.All(Enum.GetValues<SafeDiagnosticLevel>(), level =>
        {
            Assert.False(level.CapturesClipboardContent());
            Assert.False(level.CapturesRemotePixels());
            Assert.False(level.CapturesSecretMaterial());
        });
    }
}
