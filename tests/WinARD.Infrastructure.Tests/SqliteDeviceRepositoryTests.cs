using System.Text;
using Microsoft.Data.Sqlite;
using WinARD.Domain.Connections;
using WinARD.Domain.Security;
using WinARD.Infrastructure.Database;
using WinARD.Infrastructure.Devices;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Infrastructure.Tests;

public sealed class SqliteDeviceRepositoryTests
{
    public enum RetiredCredentialSlot
    {
        Device,
        SshPassword,
        SshPassphrase,
    }

    public static TheoryData<FrameRefreshPolicy> RefreshPolicies =>
    [
        FrameRefreshPolicy.Automatic,
        FrameRefreshPolicy.Fixed(30),
        FrameRefreshPolicy.Fixed(120),
        FrameRefreshPolicy.Unlimited,
    ];

    [Theory]
    [MemberData(nameof(RefreshPolicies))]
    public async Task Repository_round_trips_refresh_policy(FrameRefreshPolicy policy)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database);
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user")
            .WithFrameRefreshPolicy(policy);

        await repository.SaveAsync(profile, CancellationToken.None);

        Assert.Equal(policy, (await repository.GetAsync(profile.Id, CancellationToken.None))!.FrameRefreshPolicy);
    }

    [Fact]
    public async Task Repository_round_trips_every_quality_profile_field()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database);
        var quality = QualityProfile.CreateCustom(
            8L * 1024 * 1024,
            QualityColor.Grayscale,
            QualityScale.Percent75,
            FrameRefreshPolicy.Fixed(75),
            allowAutomaticGrayscale: true,
            bandwidthLocked: true,
            colorLocked: false,
            scaleLocked: true,
            refreshLocked: true);
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user")
            .WithQualityProfile(quality);

        await repository.SaveAsync(profile, CancellationToken.None);

        Assert.Equal(quality, (await repository.GetAsync(profile.Id, CancellationToken.None))!.Quality);
    }

    [Fact]
    public async Task Save_updates_every_quality_profile_field()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database);
        var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user");
        await repository.SaveAsync(original, CancellationToken.None);
        var quality = QualityProfile.CreateCustom(
            null,
            QualityColor.Full32,
            QualityScale.Percent100,
            FrameRefreshPolicy.Unlimited,
            allowAutomaticGrayscale: false,
            bandwidthLocked: true,
            colorLocked: true,
            scaleLocked: true,
            refreshLocked: true);

        await repository.SaveAsync(original.WithQualityProfile(quality), CancellationToken.None);

        Assert.Equal(quality, (await repository.GetAsync(original.Id, CancellationToken.None))!.Quality);
    }

    [Fact]
    public async Task Save_updates_existing_refresh_policy()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database);
        var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user");
        await repository.SaveAsync(original, CancellationToken.None);
        var updated = original.WithFrameRefreshPolicy(FrameRefreshPolicy.Fixed(90));

        await repository.SaveAsync(updated, CancellationToken.None);

        Assert.Equal(
            FrameRefreshPolicy.Fixed(90),
            (await repository.GetAsync(original.Id, CancellationToken.None))!.FrameRefreshPolicy);
    }

    [Fact]
    public async Task Saved_profile_round_trips_all_non_secret_fields_without_secret_material()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database, new FixedTimeProvider());
        var macCredential = CredentialReference.Create("windows", "fixture-mac-password");
        var sshPassword = CredentialReference.Create("windows", "fixture-ssh-password");
        var keyPassphrase = CredentialReference.Create("vault", "fixture-key-passphrase");
        var pin = new SshHostKeyPin(
            new SshHostKeyEndpoint("BÜCHER.example.", 2222),
            "SSH-ED25519",
            "AAAAC3NzaC1lZDI1NTE5AAAAIFixturePublicKey",
            "SHA256:fixture-fingerprint");
        var ssh = SshProfile
            .Create("xn--bcher-kva.example", 2222, "jump", "C:\\keys\\id_ed25519", "fd00::1", 5901, null, null, null)
            .WithAuthenticationCredentials(sshPassword, keyPassphrase)
            .WithHostKeyPin(pin);
        var profile = ConnectionProfile
            .Create(Guid.NewGuid(), "Studio Mac", "studio-mac.local", 5900, "alex")
            .WithCredential(macCredential)
            .WithSsh(ssh);

        await repository.SaveAsync(profile, CancellationToken.None);
        var loaded = await repository.GetAsync(profile.Id, CancellationToken.None);

        Assert.Equal(profile, loaded);
        foreach (var raw in fixture.ReadRawDatabaseFiles())
        {
            Assert.False(ContainsSequence(raw, Encoding.UTF8.GetBytes("correct horse battery staple")));
            Assert.False(ContainsSequence(raw, Encoding.UTF8.GetBytes("AskPass-token-fixture")));
            Assert.False(ContainsSequence(raw, Encoding.UTF8.GetBytes("private-key-passphrase-secret")));
        }
    }

    [Fact]
    public async Task Save_updates_existing_profile_and_list_delete_are_atomic()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var clock = new FixedTimeProvider();
        await using var repository = new SqliteDeviceRepository(fixture.Database, clock);
        var id = Guid.NewGuid();
        var original = ConnectionProfile.Create(id, "Mac", "mac.local", 5900, "alex")
            .WithSsh(SshProfile.Create("jump.local", 22, "jump", null, "mac.local", 5900, null, null, null));
        await repository.SaveAsync(original, CancellationToken.None);

        var updated = ConnectionProfile.Create(id, "Renamed", "mac.local", 5900, "casey");
        await repository.SaveAsync(updated, CancellationToken.None);

        Assert.Equal(updated, Assert.Single(await repository.GetAllAsync(CancellationToken.None)));
        await repository.DeleteAsync(id, CancellationToken.None);
        Assert.Null(await repository.GetAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Canonical_host_and_port_have_a_unique_conflict()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database, new FixedTimeProvider());
        await repository.SaveAsync(
            ConnectionProfile.Create(Guid.NewGuid(), "First", "BÜCHER.example.", 5900, "alex"),
            CancellationToken.None);

        await Assert.ThrowsAsync<DeviceEndpointConflictException>(() => repository.SaveAsync(
            ConnectionProfile.Create(Guid.NewGuid(), "Second", "xn--bcher-kva.example", 5900, "casey"),
            CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_persisted_transport_mode_is_rejected_on_read()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database, new FixedTimeProvider());
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "alex");
        await repository.SaveAsync(profile, CancellationToken.None);
        await fixture.ExecuteAsync($"PRAGMA ignore_check_constraints=ON; UPDATE devices SET transport_mode=99 WHERE id='{profile.Id:D}';");

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(profile.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(99, null)]
    [InlineData((int)FrameRefreshMode.Automatic, 30)]
    [InlineData((int)FrameRefreshMode.Unlimited, 30)]
    [InlineData((int)FrameRefreshMode.Fixed, null)]
    [InlineData((int)FrameRefreshMode.Fixed, 31)]
    public async Task Invalid_persisted_refresh_policy_is_rejected_on_read(int mode, int? framesPerSecond)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database, new FixedTimeProvider());
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "alex");
        await repository.SaveAsync(profile, CancellationToken.None);
        var persistedFramesPerSecond = framesPerSecond?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
        await fixture.ExecuteAsync(
            $"PRAGMA ignore_check_constraints=ON; UPDATE devices SET refresh_mode={mode}, refresh_fps={persistedFramesPerSecond} WHERE id='{profile.Id:D}';");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.GetAsync(profile.Id, CancellationToken.None));
        if (mode == (int)FrameRefreshMode.Fixed && framesPerSecond == 31)
        {
            Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
        }
    }

    [Theory]
    [InlineData(RetiredCredentialSlot.Device)]
    [InlineData(RetiredCredentialSlot.SshPassword)]
    [InlineData(RetiredCredentialSlot.SshPassphrase)]
    public async Task Save_rejects_retired_credential_reference_without_endpoint_conflict_mapping(
        RetiredCredentialSlot slot)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database, new FixedTimeProvider());
        var retired = CredentialReference.Create("vault", $"retired/{slot}");
        await fixture.ExecuteAsync($"""
            INSERT INTO retired_credential_references(backend, credential_key, retired_utc)
            VALUES ('{retired.Store}', '{retired.Key}', '2026-08-19T00:00:00.0000000+00:00');
            """);
        var ssh = SshProfile.Create(
                "jump.local", 22, "jump", null, "target.local", 5900, null, null, null)
            .WithAuthenticationCredentials(
                slot == RetiredCredentialSlot.SshPassword ? retired : null,
                slot == RetiredCredentialSlot.SshPassphrase ? retired : null);
        var profile = ConnectionProfile.Create(
            Guid.NewGuid(), $"Mac {slot}", $"{slot.ToString().ToLowerInvariant()}.local", 5900, "alex");
        profile = slot == RetiredCredentialSlot.Device
            ? profile.WithCredential(retired)
            : profile.WithSsh(ssh);

        var exception = await Assert.ThrowsAsync<RetiredCredentialReferenceException>(() =>
            repository.SaveAsync(profile, CancellationToken.None));

        Assert.Equal(retired, exception.Reference);
        Assert.Null(await repository.GetAsync(profile.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Retired_backend_matching_is_case_insensitive_like_credential_routing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database);
        await fixture.ExecuteAsync("""
            INSERT INTO retired_credential_references(backend, credential_key, retired_utc)
            VALUES ('vault', 'retired/case', '2026-08-19T00:00:00.0000000+00:00');
            """);
        var reference = CredentialReference.Create("VAULT", "retired/case");
        var profile = ConnectionProfile.Create(
                Guid.NewGuid(), "Case Mac", "case.local", 5900, "alex")
            .WithCredential(reference);

        var exception = await Assert.ThrowsAsync<RetiredCredentialReferenceException>(() =>
            repository.SaveAsync(profile, CancellationToken.None));

        Assert.Equal(reference, exception.Reference);
    }

    [Theory]
    [InlineData(RetiredCredentialSlot.Device)]
    [InlineData(RetiredCredentialSlot.SshPassword)]
    [InlineData(RetiredCredentialSlot.SshPassphrase)]
    public async Task Fresh_generation_can_replace_a_retired_purpose_reference(
        RetiredCredentialSlot slot)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database);
        var id = Guid.NewGuid();
        var purpose = slot switch
        {
            RetiredCredentialSlot.Device => "mac",
            RetiredCredentialSlot.SshPassword => "ssh-password",
            RetiredCredentialSlot.SshPassphrase => "ssh-passphrase",
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
        var retiredKey = $"profile/{id:D}/{purpose}";
        await fixture.ExecuteAsync($"""
            INSERT INTO retired_credential_references(backend, credential_key, retired_utc)
            VALUES ('windows', '{retiredKey}', '2026-08-19T00:00:00.0000000+00:00');
            """);
        var fresh = CredentialReference.Create(
            "windows", $"profile/{id:D}/{purpose}/{Guid.NewGuid():N}");
        var profile = ConnectionProfile.Create(id, "Fresh", "fresh.local", 5900, "alex");
        if (slot == RetiredCredentialSlot.Device)
        {
            profile = profile.WithCredential(fresh);
        }
        else
        {
            profile = profile.WithSsh(SshProfile.Create(
                    "jump.local", 22, "alex",
                    slot == RetiredCredentialSlot.SshPassphrase ? "C:\\keys\\id_ed25519" : null,
                    "target.local", 5900, null, null, null)
                .WithAuthenticationCredentials(
                    slot == RetiredCredentialSlot.SshPassword ? fresh : null,
                    slot == RetiredCredentialSlot.SshPassphrase ? fresh : null));
        }

        await repository.SaveAsync(profile, CancellationToken.None);

        Assert.Equal(profile, await repository.GetAsync(id, CancellationToken.None));
    }

    [Theory]
    [InlineData("quality_preset", "99")]
    [InlineData("quality_color", "99")]
    [InlineData("quality_scale", "99")]
    [InlineData("quality_bandwidth_bps", "0")]
    [InlineData("quality_bandwidth_bps", "-1")]
    [InlineData("quality_bandwidth_bps", "1099511627777")]
    [InlineData("quality_allow_gray", "2")]
    [InlineData("quality_bandwidth_locked", "-1")]
    [InlineData("quality_color_locked", "2")]
    [InlineData("quality_scale_locked", "2")]
    [InlineData("quality_refresh_locked", "2")]
    public async Task Invalid_persisted_quality_scalar_is_rejected_on_read(string column, string value)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database);
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "alex");
        await repository.SaveAsync(profile, CancellationToken.None);
        await fixture.ExecuteAsync(
            $"PRAGMA ignore_check_constraints=ON; UPDATE devices SET {column}={value} WHERE id='{profile.Id:D}';");

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(profile.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_persisted_quality_combination_is_rejected_on_read()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database);
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "alex");
        await repository.SaveAsync(profile, CancellationToken.None);
        await fixture.ExecuteAsync(
            $"PRAGMA ignore_check_constraints=ON; UPDATE devices SET quality_preset={(int)QualityPreset.Custom}, quality_color={(int)QualityColor.Full32}, quality_color_locked=1, quality_allow_gray=1 WHERE id='{profile.Id:D}';");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.GetAsync(profile.Id, CancellationToken.None));
        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Theory]
    [InlineData("quality_bandwidth_bps", "4194304")]
    [InlineData("quality_color", "1")]
    [InlineData("quality_scale_locked", "1")]
    public async Task Named_preset_with_inconsistent_persisted_fields_is_rejected(string column, string value)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database);
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "alex");
        await repository.SaveAsync(profile, CancellationToken.None);
        await fixture.ExecuteAsync(
            $"PRAGMA ignore_check_constraints=ON; UPDATE devices SET {column}={value} WHERE id='{profile.Id:D}';");

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(profile.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData((int)QualityPreset.Automatic, (int)FrameRefreshMode.Fixed, 60)]
    [InlineData((int)QualityPreset.Original, (int)FrameRefreshMode.Automatic, null)]
    [InlineData((int)QualityPreset.Balanced, (int)FrameRefreshMode.Unlimited, null)]
    [InlineData((int)QualityPreset.Smooth, (int)FrameRefreshMode.Fixed, 90)]
    public async Task Named_preset_with_nonstandard_refresh_is_rejected(
        int preset,
        int refreshMode,
        int? refreshFramesPerSecond)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database);
        var named = preset switch
        {
            (int)QualityPreset.Automatic => QualityProfile.Automatic,
            (int)QualityPreset.Original => QualityProfile.Original,
            (int)QualityPreset.Balanced => QualityProfile.Balanced,
            (int)QualityPreset.Smooth => QualityProfile.Smooth,
            _ => throw new InvalidOperationException(),
        };
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "alex")
            .WithQualityProfile(named);
        await repository.SaveAsync(profile, CancellationToken.None);
        var persistedFramesPerSecond = refreshFramesPerSecond?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
        await fixture.ExecuteAsync(
            $"PRAGMA ignore_check_constraints=ON; UPDATE devices SET refresh_mode={refreshMode}, refresh_fps={persistedFramesPerSecond} WHERE id='{profile.Id:D}';");

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(profile.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Persisted_transport_mode_must_match_the_ssh_row()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var repository = new SqliteDeviceRepository(fixture.Database, new FixedTimeProvider());
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "alex")
            .WithSsh(SshProfile.Create("jump.local", 22, "jump", null, "mac.local", 5900, null, null, null));
        await repository.SaveAsync(profile, CancellationToken.None);
        await fixture.ExecuteAsync($"UPDATE devices SET transport_mode=0 WHERE id='{profile.Id:D}';");

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(profile.Id, CancellationToken.None));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 26, 1, 2, 3, TimeSpan.Zero);
    }

    private static bool ContainsSequence(byte[] source, byte[] value) =>
        source.AsSpan().IndexOf(value) >= 0;

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private DatabaseFixture(string directory, string path, WinArdDatabase database)
        {
            Directory = directory;
            Path = path;
            Database = database;
        }

        public string Directory { get; }
        public string Path { get; }
        public WinArdDatabase Database { get; }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"));
            var path = System.IO.Path.Combine(directory, "winard.db");
            var database = new WinArdDatabase(path);
            await database.InitializeAsync(CancellationToken.None);
            return new DatabaseFixture(directory, path, database);
        }

        public IEnumerable<byte[]> ReadRawDatabaseFiles()
        {
            foreach (var path in new[] { Path, Path + "-wal", Path + "-shm" })
            {
                if (File.Exists(path))
                {
                    yield return File.ReadAllBytes(path);
                }
            }
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
