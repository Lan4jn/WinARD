using System.Collections.Concurrent;
using WinARD.Application.Ports;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using WinARD.Domain.Security;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    private static readonly Guid StudioId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OfficeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Initialize_filters_only_saved_devices_and_keeps_discovery_updates_visible()
    {
        var repository = new FakeRepository(
            Profile(StudioId, "Studio Mac", "studio.local"),
            Profile(OfficeId, "Office Mini", "office.local"));
        var nearby = Discovered("nearby", "Nearby MacBook", "nearby.local");
        var discovery = new FakeDiscovery(nearby);
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainWindowViewModel(repository, discovery, new FakeCredentialStore(), dispatcher)
        {
            SearchText = "studio",
        };

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal("studio", viewModel.SearchText);
        Assert.Equal("Studio Mac", Assert.Single(viewModel.SavedDevices).DisplayName);
        Assert.Equal("Nearby MacBook", Assert.Single(viewModel.DiscoveredDevices).DisplayName);

        discovery.Publish(DiscoveryChangeKind.Added, Discovered("new", "Kitchen Mac", "kitchen.local"));
        await dispatcher.WhenIdleAsync();

        Assert.Equal(["Kitchen Mac", "Nearby MacBook"], viewModel.DiscoveredDevices.Select(item => item.DisplayName).Order());
        Assert.Equal("Studio Mac", Assert.Single(viewModel.SavedDevices).DisplayName);
    }

    [Fact]
    public async Task Delete_without_credential_choice_removes_only_the_profile()
    {
        var credential = CredentialReference.Create("windows", "studio");
        var repository = new FakeRepository(Profile(StudioId, "Studio Mac", "studio.local").WithCredential(credential));
        var credentialStore = new FakeCredentialStore();
        await using var viewModel = Create(repository, credentialStore: credentialStore);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = Assert.Single(viewModel.SavedDevices);

        var deleted = await viewModel.DeleteSelectedAsync(deleteCredential: false, CancellationToken.None);

        Assert.True(deleted);
        Assert.Equal([StudioId], repository.DeletedIds);
        Assert.Empty(credentialStore.DeletedReferences);
        Assert.Empty(viewModel.SavedDevices);
        Assert.Null(viewModel.SelectedDevice);
    }

    [Fact]
    public async Task Delete_with_credential_choice_removes_profile_and_credential()
    {
        var credential = CredentialReference.Create("windows", "office");
        var repository = new FakeRepository(Profile(OfficeId, "Office Mini", "office.local").WithCredential(credential));
        var credentialStore = new FakeCredentialStore();
        await using var viewModel = Create(repository, credentialStore: credentialStore);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = Assert.Single(viewModel.SavedDevices);

        var deleted = await viewModel.DeleteSelectedAsync(deleteCredential: true, CancellationToken.None);

        Assert.True(deleted);
        Assert.Equal([OfficeId], repository.DeletedIds);
        Assert.Equal([credential], credentialStore.DeletedReferences);
    }

    [Fact]
    public async Task Delete_with_credential_choice_removes_unshared_primary_and_ssh_credentials()
    {
        var primary = CredentialReference.Create("windows", "primary");
        var sshPassword = CredentialReference.Create("windows", "ssh-password");
        var keyPassphrase = CredentialReference.Create("windows", "key-passphrase");
        var profile = Profile(StudioId, "Studio Mac", "studio.local")
            .WithCredential(primary)
            .WithSsh(SshProfile.Create(
                    "jump.local", 22, "alex", "id_ed25519", "studio.local", 5900, null, null, null)
                .WithAuthenticationCredentials(sshPassword, keyPassphrase));
        var repository = new FakeRepository(profile);
        var credentialStore = new FakeCredentialStore();
        await using var viewModel = Create(repository, credentialStore: credentialStore);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = Assert.Single(viewModel.SavedDevices);

        Assert.True(await viewModel.DeleteSelectedAsync(deleteCredential: true, CancellationToken.None));

        Assert.Equal(
            [keyPassphrase, primary, sshPassword],
            credentialStore.DeletedReferences.OrderBy(reference => reference.Key));
    }

    [Fact]
    public async Task Delete_with_credential_choice_preserves_a_reference_shared_by_another_profile()
    {
        var shared = CredentialReference.Create("windows", "shared");
        var repository = new FakeRepository(
            Profile(StudioId, "Studio Mac", "studio.local").WithCredential(shared),
            Profile(OfficeId, "Office Mini", "office.local").WithCredential(shared));
        var credentialStore = new FakeCredentialStore();
        await using var viewModel = Create(repository, credentialStore: credentialStore);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = viewModel.SavedDevices.Single(item => item.Profile?.Id == StudioId);

        Assert.True(await viewModel.DeleteSelectedAsync(deleteCredential: true, CancellationToken.None));

        Assert.Empty(credentialStore.DeletedReferences);
        Assert.Equal("Office Mini", Assert.Single(viewModel.SavedDevices).DisplayName);
    }

    [Fact]
    public async Task Credential_delete_failure_still_removes_the_deleted_profile_from_the_view()
    {
        var credential = CredentialReference.Create("windows", "studio");
        var repository = new FakeRepository(Profile(StudioId, "Studio Mac", "studio.local").WithCredential(credential));
        var credentialStore = new FakeCredentialStore { DeleteException = new InvalidOperationException("credential failure") };
        await using var viewModel = Create(repository, credentialStore: credentialStore);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = Assert.Single(viewModel.SavedDevices);

        Assert.True(await viewModel.DeleteSelectedAsync(deleteCredential: true, CancellationToken.None));

        Assert.Empty(viewModel.SavedDevices);
        Assert.Null(viewModel.SelectedDevice);
        Assert.Contains("凭据", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_delete_failure_keeps_the_profile_and_reports_the_error()
    {
        var repository = new FakeRepository(Profile(StudioId, "Studio Mac", "studio.local"))
        {
            DeleteException = new InvalidOperationException("database failure"),
        };
        await using var viewModel = Create(repository);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = Assert.Single(viewModel.SavedDevices);

        Assert.False(await viewModel.DeleteSelectedAsync(deleteCredential: false, CancellationToken.None));

        Assert.Single(viewModel.SavedDevices);
        Assert.NotNull(viewModel.SelectedDevice);
        Assert.Contains("删除设备", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_is_cancelled_for_null_choice_or_no_selection()
    {
        var repository = new FakeRepository(Profile(StudioId, "Studio Mac", "studio.local"));
        await using var viewModel = Create(repository);
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(await viewModel.DeleteSelectedAsync(deleteCredential: true, CancellationToken.None));
        viewModel.SelectedDevice = Assert.Single(viewModel.SavedDevices);
        Assert.False(await viewModel.DeleteSelectedAsync(deleteCredential: null, CancellationToken.None));
        Assert.Empty(repository.DeletedIds);
        Assert.Single(viewModel.SavedDevices);
    }

    [Fact]
    public async Task Initialize_is_shared_and_database_and_discovery_start_off_the_calling_thread()
    {
        var repository = new FakeRepository(Profile(StudioId, "Studio Mac", "studio.local")) { BlockLoad = true };
        var discovery = new FakeDiscovery();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = Create(repository, discovery, dispatcher: dispatcher);
        var callingThread = Environment.CurrentManagedThreadId;

        var first = viewModel.InitializeAsync(CancellationToken.None);
        await repository.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = viewModel.InitializeAsync(CancellationToken.None);
        repository.ReleaseLoad.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, repository.LoadCount);
        Assert.Equal(1, discovery.StartCount);
        Assert.NotEqual(callingThread, repository.LoadThreadId);
        Assert.NotEqual(callingThread, discovery.StartThreadId);
        Assert.True(dispatcher.InvocationCount >= 1);
    }

    [Fact]
    public async Task Cancelled_initialize_can_be_retried()
    {
        var repository = new FakeRepository(Profile(StudioId, "Studio Mac", "studio.local")) { BlockLoad = true };
        await using var viewModel = Create(repository);
        using var cancellation = new CancellationTokenSource();
        var cancelled = viewModel.InitializeAsync(cancellation.Token);
        await repository.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        repository.BlockLoad = false;
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(2, repository.LoadCount);
        Assert.Equal("Studio Mac", Assert.Single(viewModel.SavedDevices).DisplayName);
    }

    [Fact]
    public async Task Dispose_cancels_initialization_and_ignores_late_discovery_events()
    {
        var repository = new FakeRepository { BlockLoad = true };
        var discovery = new FakeDiscovery();
        var dispatcher = new RecordingDispatcher();
        var viewModel = Create(repository, discovery, dispatcher: dispatcher);
        var initialize = viewModel.InitializeAsync(CancellationToken.None);
        await repository.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
        discovery.Publish(DiscoveryChangeKind.Added, Discovered("late", "Late Mac", "late.local"));
        await dispatcher.WhenIdleAsync();

        Assert.Equal(1, discovery.DisposeCount);
        Assert.Equal(1, repository.DisposeCount);
        Assert.Empty(viewModel.DiscoveredDevices);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => viewModel.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Removing_the_selected_discovery_item_clears_the_selection()
    {
        var nearby = Discovered("nearby", "Nearby MacBook", "nearby.local");
        var discovery = new FakeDiscovery(nearby);
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = Create(new FakeRepository(), discovery, dispatcher: dispatcher);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = Assert.Single(viewModel.DiscoveredDevices);

        discovery.Publish(DiscoveryChangeKind.Removed, nearby);
        await dispatcher.WhenIdleAsync();

        Assert.Null(viewModel.SelectedDevice);
        Assert.Empty(viewModel.DiscoveredDevices);
    }

    [Fact]
    public async Task Updating_the_selected_discovery_item_replaces_the_selection()
    {
        var nearby = Discovered("nearby", "Nearby MacBook", "nearby.local");
        var discovery = new FakeDiscovery(nearby);
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = Create(new FakeRepository(), discovery, dispatcher: dispatcher);
        await viewModel.InitializeAsync(CancellationToken.None);
        var original = Assert.Single(viewModel.DiscoveredDevices);
        viewModel.SelectedDevice = original;

        discovery.Publish(DiscoveryChangeKind.Updated, Discovered("nearby", "Renamed MacBook", "renamed.local"));
        await dispatcher.WhenIdleAsync();

        Assert.NotSame(original, viewModel.SelectedDevice);
        Assert.Equal("Renamed MacBook", viewModel.SelectedDevice?.DisplayName);
        Assert.Equal("renamed.local", viewModel.SelectedDevice?.Host);
    }

    private static MainWindowViewModel Create(
        FakeRepository repository,
        FakeDiscovery? discovery = null,
        FakeCredentialStore? credentialStore = null,
        RecordingDispatcher? dispatcher = null) =>
        new(repository, discovery ?? new FakeDiscovery(), credentialStore ?? new FakeCredentialStore(), dispatcher ?? new RecordingDispatcher());

    private static ConnectionProfile Profile(Guid id, string name, string host) =>
        ConnectionProfile.Create(id, name, host, 5900, "alex");

    private static DiscoveredDevice Discovered(string identity, string name, string host) =>
        new(identity, name, host, 5900, new Dictionary<string, string>(), DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        private readonly ConcurrentBag<Task> _pending = [];

        public int InvocationCount { get; private set; }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            var task = Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
            }, cancellationToken);
            _pending.Add(task);
            return task;
        }

        public Task WhenIdleAsync() => Task.WhenAll(_pending.ToArray());
    }

    private sealed class FakeRepository(params ConnectionProfile[] profiles) : IDeviceRepository
    {
        private readonly List<ConnectionProfile> _profiles = [.. profiles];

        public bool BlockLoad { get; set; }
        public int LoadCount { get; private set; }
        public int LoadThreadId { get; private set; }
        public int DisposeCount { get; private set; }
        public Exception? DeleteException { get; init; }
        public List<Guid> DeletedIds { get; } = [];
        public TaskCompletionSource LoadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_profiles.SingleOrDefault(profile => profile.Id == id));

        public async Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            LoadThreadId = Environment.CurrentManagedThreadId;
            LoadEntered.TrySetResult();
            if (BlockLoad)
            {
                await ReleaseLoad.Task.WaitAsync(cancellationToken);
            }

            return _profiles.ToArray();
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DeleteException is not null)
            {
                return Task.FromException(DeleteException);
            }

            DeletedIds.Add(id);
            _profiles.RemoveAll(profile => profile.Id == id);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDiscovery(params DiscoveredDevice[] devices) : IDeviceDiscovery
    {
        private readonly List<DiscoveredDevice> _current = [.. devices];

        public event EventHandler<DiscoveryChange>? Changed;
        public IReadOnlyList<DiscoveredDevice> Current => _current.ToArray();
        public int StartCount { get; private set; }
        public int StartThreadId { get; private set; }
        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            StartThreadId = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Publish(DiscoveryChangeKind kind, DiscoveredDevice device)
        {
            if (kind == DiscoveryChangeKind.Removed)
            {
                _current.RemoveAll(item => item.Identity == device.Identity);
            }
            else
            {
                _current.RemoveAll(item => item.Identity == device.Identity);
                _current.Add(device);
            }

            Changed?.Invoke(this, new DiscoveryChange(kind, device));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public List<CredentialReference> DeletedReferences { get; } = [];
        public Exception? DeleteException { get; init; }

        public ValueTask SaveAsync(CredentialReference reference, ISecret secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ISecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(CredentialReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(CredentialReference reference, CredentialStoreVersion? expectedVersion, ISecret? replacement, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DeleteAsync(CredentialReference reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DeleteException is not null)
            {
                return ValueTask.FromException(DeleteException);
            }

            DeletedReferences.Add(reference);
            return ValueTask.CompletedTask;
        }
    }
}
