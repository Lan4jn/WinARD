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
