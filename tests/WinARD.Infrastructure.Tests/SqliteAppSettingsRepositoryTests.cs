using WinARD.Domain.Settings;
using WinARD.Infrastructure.Database;
using WinARD.Infrastructure.Settings;
using WinARD.Infrastructure.Diagnostics;
using Xunit;

#pragma warning disable CA1707
#pragma warning disable CA1001

namespace WinARD.Infrastructure.Tests;

public sealed class SqliteAppSettingsRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"));
    private WinArdDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = new WinArdDatabase(Path.Combine(_directory, "winard.db"));
        await _database.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task New_database_returns_current_defaults()
    {
        await using var repository = new SqliteAppSettingsRepository(_database);

        var snapshot = await repository.GetAsync(CancellationToken.None);

        Assert.Equal(AppSettings.Default, snapshot.Settings);
        Assert.Equal(0, snapshot.Revision);
    }

    [Fact]
    public async Task Repository_round_trips_every_setting()
    {
        await using var repository = new SqliteAppSettingsRepository(_database);
        var original = await repository.GetAsync(CancellationToken.None);
        var changed = AppSettings.Create(
            1, AppTheme.Dark, SafeDiagnosticLevel.Verbose,
            clipboardEnabledByDefault: false,
            CredentialBackend.EncryptedVault,
            TimeSpan.FromMinutes(45));

        var updated = await repository.TryUpdateAsync(
            original.Revision, changed, CancellationToken.None);

        Assert.True(updated);
        var actual = await repository.GetAsync(CancellationToken.None);
        Assert.Equal(changed, actual.Settings);
        Assert.Equal(1, actual.Revision);
    }

    [Fact]
    public async Task Stale_concurrent_update_is_rejected_without_overwrite()
    {
        await using var first = new SqliteAppSettingsRepository(_database);
        await using var second = new SqliteAppSettingsRepository(_database);
        var firstRead = await first.GetAsync(CancellationToken.None);
        var secondRead = await second.GetAsync(CancellationToken.None);
        var dark = AppSettings.Create(
            1, AppTheme.Dark, SafeDiagnosticLevel.Standard, true,
            CredentialBackend.Windows, TimeSpan.FromMinutes(15));
        var light = AppSettings.Create(
            1, AppTheme.Light, SafeDiagnosticLevel.Standard, true,
            CredentialBackend.Windows, TimeSpan.FromMinutes(15));

        Assert.True(await first.TryUpdateAsync(
            firstRead.Revision, dark, CancellationToken.None));
        Assert.False(await second.TryUpdateAsync(
            secondRead.Revision, light, CancellationToken.None));

        Assert.Equal(dark, (await first.GetAsync(CancellationToken.None)).Settings);
    }

    [Fact]
    public void Safe_diagnostic_levels_bound_only_structured_field_detail()
    {
        var level = new SafeDiagnosticLevelController();
        var sink = new InMemorySafeDiagnosticSink(new SecretRedactor(), level);
        var fields = Enumerable.Range(0, 20)
            .Select(index => new DiagnosticField(
                $"field_{index}",
                index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        level.Level = SafeDiagnosticLevel.Minimal;
        sink.Write(new SafeDiagnosticEventInput("MIN", "c1", "safe", fields));
        level.Level = SafeDiagnosticLevel.Standard;
        sink.Write(new SafeDiagnosticEventInput("STD", "c2", "safe", fields));
        level.Level = SafeDiagnosticLevel.Verbose;
        sink.Write(new SafeDiagnosticEventInput("VER", "c3", "safe", fields));

        var events = sink.Snapshot();
        Assert.Empty(events[0].Fields);
        Assert.Equal(16, events[1].Fields.Count);
        Assert.Equal(20, events[2].Fields.Count);
        Assert.All(Enum.GetValues<SafeDiagnosticLevel>(), value =>
        {
            level.Level = value;
            Assert.False(value.CapturesClipboardContent());
            Assert.False(value.CapturesRemotePixels());
            Assert.False(value.CapturesSecretMaterial());
        });
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
