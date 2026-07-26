using System.Text;
using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.Services;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Security.Secrets;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class ConnectionEditorServiceTests
{
    [Fact]
    public async Task AskEveryTimeSavesReferenceWithoutPersistingSecret()
    {
        var repository = new FakeRepository();
        using var store = new TransientCredentialStore();
        var sut = CreateService(repository, store);
        var profile = Profile();
        using var secret = Secret("must-not-be-persisted");

        var saved = await sut.SaveAsync(
            profile,
            CredentialSaveMode.AskEveryTime,
            secret,
            CancellationToken.None);

        Assert.Equal("ask", saved.CredentialReference!.Store);
        Assert.Equal(saved, repository.Saved);
        Assert.Null(await store.ReadAsync(saved.CredentialReference, CancellationToken.None));
    }

    [Fact]
    public async Task RepositoryFailureRollsBackNewCredential()
    {
        var repository = new FakeRepository { FailSave = true };
        using var store = new TransientCredentialStore();
        var sut = CreateService(repository, store);
        var profile = Profile();
        using var secret = Secret("temporary-password");

        await Assert.ThrowsAsync<IOException>(() => sut.SaveAsync(
            profile,
            CredentialSaveMode.WindowsCredentialManager,
            secret,
            CancellationToken.None));

        var reference = WinARD.Domain.Security.CredentialReference.Create(
            "windows",
            $"profile/{profile.Id:D}/mac");
        Assert.Null(await store.ReadAsync(reference, CancellationToken.None));
    }

    private static ConnectionEditorService CreateService(
        IDeviceRepository repository,
        TransientCredentialStore store) => new(
            repository,
            store,
            store,
            new ConnectDeviceHandler(
                new UnusedTransport(),
                new UnusedSecretProvider(),
                new RfbClientFactory(),
                new ErrorMapper()));

    private static ConnectionProfile Profile() => ConnectionProfile.Create(
        Guid.NewGuid(),
        "Studio Mac",
        "studio.local",
        5900,
        "operator");

    private static SecretBuffer Secret(string value) =>
        SecretBuffer.CopyFrom(Encoding.UTF8.GetBytes(value));

    private sealed class FakeRepository : IDeviceRepository
    {
        public bool FailSave { get; init; }

        public ConnectionProfile? Saved { get; private set; }

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSave)
            {
                throw new IOException("repository failure");
            }

            Saved = profile;
            return Task.CompletedTask;
        }

        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<ConnectionProfile?>(null);

        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>([]);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedTransport : IRemoteTransportFactory
    {
        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by save tests.");
    }

    private sealed class UnusedSecretProvider : IConnectionSecretProvider
    {
        public ValueTask<ISecret> GetSecretAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by save tests.");
    }
}
