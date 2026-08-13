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
    public async Task Activating_discovered_device_requests_prefilled_unsaved_draft()
    {
        var repository = new FakeRepository();
        var discovery = new FakeDiscovery(Discovered("nearby", "Nearby MacBook", "nearby.local"));
        await using var viewModel = Create(repository, discovery);
        await viewModel.InitializeAsync(CancellationToken.None);
        ConnectionEditorDraft? requested = null;
        viewModel.AddDeviceRequested += draft => requested = draft;
        viewModel.SelectedDevice = Assert.Single(viewModel.DiscoveredDevices);

        viewModel.ActivateSelectedCommand.Execute(null);

        Assert.Equal("Nearby MacBook", requested!.DisplayName);
        Assert.Equal("nearby.local", requested.Host);
        Assert.Equal(5900, requested.Port);
        Assert.Empty(requested.MacUsername);
    }

    [Fact]
    public async Task Activating_saved_device_requests_connection_but_selection_alone_does_not()
    {
        var profile = Profile(StudioId, "Studio Mac", "studio.local");
        await using var viewModel = Create(new FakeRepository(profile));
        await viewModel.InitializeAsync(CancellationToken.None);
        ConnectionProfile? requested = null;
        viewModel.ConnectRequested += candidate => requested = candidate;

        viewModel.SelectedDevice = Assert.Single(viewModel.SavedDevices);
        Assert.Null(requested);
        viewModel.ActivateSelectedCommand.Execute(null);

        Assert.Equal(profile, requested);
    }

    [Fact]
    public async Task Activating_hit_item_selects_and_activates_it_instead_of_stale_selection()
    {
        var studio = Profile(StudioId, "Studio Mac", "studio.local");
        var office = Profile(OfficeId, "Office Mini", "office.local");
        await using var viewModel = Create(new FakeRepository(studio, office));
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = viewModel.SavedDevices.Single(item => item.Profile == studio);
        ConnectionProfile? requested = null;
        viewModel.ConnectRequested += candidate => requested = candidate;
        var hit = viewModel.SavedDevices.Single(item => item.Profile == office);

        viewModel.ActivateDeviceCommand.Execute(hit);

        Assert.Same(hit, viewModel.SelectedDevice);
        Assert.Equal(office, requested);
    }

    [Fact]
    public async Task Explicit_add_requests_blank_draft()
    {
        await using var viewModel = Create(new FakeRepository());
        ConnectionEditorDraft? requested = new("sentinel", "sentinel", 1, "sentinel");
        viewModel.AddDeviceRequested += draft => requested = draft;

        viewModel.AddDeviceCommand.Execute(null);

        Assert.Null(requested);
    }

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
    public async Task ApplySavedProfileKeepsSanitizedCredentialCleanupWarning()
    {
        var repository = new FakeRepository();
        await using var viewModel = Create(repository);
        await viewModel.InitializeAsync(CancellationToken.None);
        var profile = Profile(StudioId, "Studio Mac", "studio.local");
        const string warning = "连接已保存，但旧凭据未能清理。";

        await viewModel.ApplySavedProfileAsync(
            profile,
            warning,
            CancellationToken.None);

        Assert.Equal(profile, Assert.Single(viewModel.SavedDevices).Profile);
        Assert.Equal(profile, viewModel.SelectedDevice!.Profile);
        Assert.Equal(warning, viewModel.StatusMessage);
    }

    [Fact]
    public async Task ApplySavedProfileWithoutWarningReportsOrdinarySaveStatus()
    {
        var repository = new FakeRepository();
        await using var viewModel = Create(repository);
        await viewModel.InitializeAsync(CancellationToken.None);
        var profile = Profile(StudioId, "Studio Mac", "studio.local");

        await viewModel.ApplySavedProfileAsync(
            profile,
            warning: null,
            CancellationToken.None);

        Assert.Equal("已保存“Studio Mac”。", viewModel.StatusMessage);
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
    public async Task Initialize_is_shared_and_background_work_stays_off_the_ui_thread()
    {
        await using var uiThread = new DedicatedUiThreadDispatcher();
        var repository = new FakeRepository(Profile(StudioId, "Studio Mac", "studio.local")) { BlockLoad = true };
        var discovery = new FakeDiscovery();
        await using var viewModel = Create(repository, discovery, dispatcher: uiThread);

        await uiThread.RunAsync(async () =>
        {
            Assert.Equal(uiThread.ThreadId, Environment.CurrentManagedThreadId);

            var first = viewModel.InitializeAsync(CancellationToken.None);
            await repository.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(uiThread.ThreadId, Environment.CurrentManagedThreadId);
            var second = viewModel.InitializeAsync(CancellationToken.None);
            repository.ReleaseLoad.SetResult();
            await Task.WhenAll(first, second);
        });

        Assert.Equal(1, repository.LoadCount);
        Assert.Equal(1, discovery.StartCount);
        Assert.NotEqual(uiThread.ThreadId, repository.LoadThreadId);
        Assert.NotEqual(uiThread.ThreadId, discovery.StartThreadId);
        Assert.NotEmpty(uiThread.InvocationThreadIds);
        Assert.All(uiThread.InvocationThreadIds, threadId => Assert.Equal(uiThread.ThreadId, threadId));
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
    public async Task Dispose_during_initialize_after_ui_mutation_cancels_without_object_disposed_error()
    {
        var repository = new FakeRepository(Profile(StudioId, "Studio Mac", "studio.local"));
        var dispatcher = new MutationBlockingDispatcher();
        var viewModel = Create(repository, dispatcher: dispatcher);
        var initialize = viewModel.InitializeAsync(CancellationToken.None);
        await dispatcher.MutationApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = viewModel.DisposeAsync().AsTask();
        dispatcher.ReleaseReturn.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
        await dispose;
        Assert.Equal(1, repository.DisposeCount);
    }

    [Fact]
    public async Task Dispose_continues_after_resource_failures_and_aggregates_them()
    {
        var repository = new FakeRepository { DisposeException = new IOException("repository dispose") };
        var discovery = new FakeDiscovery { DisposeException = new IOException("discovery dispose") };
        var credentialStore = new FakeCredentialStore { DisposeException = new IOException("credential dispose") };
        var viewModel = Create(repository, discovery, credentialStore);

        var exception = await Assert.ThrowsAsync<AggregateException>(() => viewModel.DisposeAsync().AsTask());

        Assert.Equal(3, exception.InnerExceptions.Count);
        Assert.Equal(1, discovery.DisposeCount);
        Assert.Equal(1, repository.DisposeCount);
        Assert.Equal(1, credentialStore.DisposeCount);
    }

    [Fact]
    public async Task Concurrent_deletes_remove_a_shared_credential_once_after_the_last_profile()
    {
        var shared = CredentialReference.Create("windows", "shared-concurrent");
        var repository = new BlockingDeleteRepository(
            Profile(StudioId, "Studio Mac", "studio.local").WithCredential(shared),
            Profile(OfficeId, "Office Mini", "office.local").WithCredential(shared));
        var credentialStore = new FakeCredentialStore();
        await using var viewModel = new MainWindowViewModel(
            repository, new FakeDiscovery(), credentialStore, new RecordingDispatcher());
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = viewModel.SavedDevices.Single(item => item.Profile?.Id == StudioId);
        var first = viewModel.DeleteSelectedAsync(deleteCredential: true, CancellationToken.None);
        await repository.FirstDeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.SelectedDevice = viewModel.SavedDevices.Single(item => item.Profile?.Id == OfficeId);
        var second = viewModel.DeleteSelectedAsync(deleteCredential: true, CancellationToken.None);

        repository.ReleaseFirstDelete.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal([StudioId, OfficeId], repository.DeletedIds.Order());
        Assert.Equal([shared], credentialStore.DeletedReferences);
        Assert.Empty(viewModel.SavedDevices);
    }

    [Fact]
    public async Task Dispose_does_not_cancel_an_entered_delete_and_waits_for_credential_cleanup()
    {
        var credential = CredentialReference.Create("windows", "shutdown-commit");
        var repository = new BlockingDeleteRepository(
            Profile(StudioId, "Studio Mac", "studio.local").WithCredential(credential));
        var credentialStore = new FakeCredentialStore();
        using var shutdown = new CancellationTokenSource();
        var viewModel = new MainWindowViewModel(
            repository, new FakeDiscovery(), credentialStore, new RecordingDispatcher());
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = Assert.Single(viewModel.SavedDevices);
        var delete = viewModel.DeleteSelectedAsync(deleteCredential: true, shutdown.Token);
        await repository.FirstDeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        shutdown.Cancel();
        var dispose = viewModel.DisposeAsync().AsTask();

        Assert.False(delete.IsCompleted);
        Assert.False(repository.DisposeCalled.Task.IsCompleted);
        repository.ReleaseFirstDelete.TrySetResult();
        Assert.True(await delete);
        await dispose;
        Assert.False(repository.FirstDeleteToken.IsCancellationRequested);
        Assert.Equal([credential], credentialStore.DeletedReferences);
        Assert.Equal(1, credentialStore.DisposeCount);
        Assert.True(repository.DisposeCalled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Shutdown_cancels_a_queued_delete_without_starting_new_persistence()
    {
        var repository = new BlockingDeleteRepository(
            Profile(StudioId, "Studio Mac", "studio.local"),
            Profile(OfficeId, "Office Mini", "office.local"));
        using var shutdown = new CancellationTokenSource();
        var viewModel = new MainWindowViewModel(
            repository, new FakeDiscovery(), new FakeCredentialStore(), new RecordingDispatcher());
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = viewModel.SavedDevices.Single(item => item.Profile?.Id == StudioId);
        var entered = viewModel.DeleteSelectedAsync(deleteCredential: false, shutdown.Token);
        await repository.FirstDeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.SelectedDevice = viewModel.SavedDevices.Single(item => item.Profile?.Id == OfficeId);
        var queued = viewModel.DeleteSelectedAsync(deleteCredential: false, shutdown.Token);

        shutdown.Cancel();
        var dispose = viewModel.DisposeAsync().AsTask();
        repository.ReleaseFirstDelete.TrySetResult();

        Assert.True(await entered);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await dispose;
        Assert.Equal([StudioId], repository.DeleteEnteredIds);
        Assert.Equal([StudioId], repository.DeletedIds);
    }

    [Fact]
    public async Task Entered_delete_operation_cancellation_is_reported_and_dispose_still_cleans_up_resources()
    {
        var repository = new BlockingDeleteRepository(Profile(StudioId, "Studio Mac", "studio.local"))
        {
            DeleteException = new OperationCanceledException("storage failure"),
        };
        var credentialStore = new FakeCredentialStore();
        using var shutdown = new CancellationTokenSource();
        var viewModel = new MainWindowViewModel(
            repository, new FakeDiscovery(), credentialStore, new RecordingDispatcher());
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedDevice = Assert.Single(viewModel.SavedDevices);
        var delete = viewModel.DeleteSelectedAsync(deleteCredential: false, shutdown.Token);
        await repository.FirstDeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        shutdown.Cancel();
        var dispose = viewModel.DisposeAsync().AsTask();
        Assert.False(repository.DisposeCalled.Task.IsCompleted);
        repository.ReleaseFirstDelete.TrySetResult();

        Assert.False(await delete);
        Assert.Contains("删除设备", viewModel.StatusMessage, StringComparison.Ordinal);
        await dispose;
        Assert.Equal(1, credentialStore.DisposeCount);
        Assert.True(repository.DisposeCalled.Task.IsCompletedSuccessfully);
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

    [Fact]
    public async Task Initialize_converges_when_discovery_adds_after_snapshot_before_replace()
    {
        var discovery = new FakeDiscovery { BlockCurrentRead = true };
        var dispatcher = new SerialDispatcher();
        await using var viewModel = Create(new FakeRepository(), discovery, dispatcher: dispatcher);

        var initialize = viewModel.InitializeAsync(CancellationToken.None);
        await Task.WhenAny(discovery.CurrentReadEntered.Task, dispatcher.FirstEnqueued.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var added = Discovered("added", "Added Mac", "added.local");
        discovery.Publish(DiscoveryChangeKind.Added, added);
        discovery.ReleaseCurrentRead.Set();
        await dispatcher.SecondEnqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatcher.RunAll();
        await initialize;

        Assert.Equal(discovery.Current.Select(device => device.Identity), viewModel.DiscoveredDevices.Select(device => device.Identity));
    }

    [Fact]
    public async Task Initialize_converges_and_clears_selection_when_discovery_removes_after_snapshot_before_replace()
    {
        var removed = Discovered("removed", "Removed Mac", "removed.local");
        var discovery = new FakeDiscovery(removed) { BlockCurrentRead = true };
        var dispatcher = new SerialDispatcher();
        await using var viewModel = Create(new FakeRepository(), discovery, dispatcher: dispatcher);
        viewModel.SelectedDevice = DeviceItemViewModel.FromDiscovery(removed);

        var initialize = viewModel.InitializeAsync(CancellationToken.None);
        await Task.WhenAny(discovery.CurrentReadEntered.Task, dispatcher.FirstEnqueued.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        discovery.Publish(DiscoveryChangeKind.Removed, removed);
        discovery.ReleaseCurrentRead.Set();
        await dispatcher.SecondEnqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatcher.RunAll();
        await initialize;

        Assert.Empty(discovery.Current);
        Assert.Empty(viewModel.DiscoveredDevices);
        Assert.Null(viewModel.SelectedDevice);
    }

    private static MainWindowViewModel Create(
        FakeRepository repository,
        FakeDiscovery? discovery = null,
        FakeCredentialStore? credentialStore = null,
        IUiDispatcher? dispatcher = null) =>
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

    private sealed class DedicatedUiThreadDispatcher : IUiDispatcher, IAsyncDisposable
    {
        private readonly BlockingCollection<Action> _queue = [];
        private readonly TaskCompletionSource<int> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        public DedicatedUiThreadDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "WinARD test UI thread",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_started.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The dedicated UI test thread did not start.");
            }

            ThreadId = _started.Task.GetAwaiter().GetResult();
        }

        public int ThreadId { get; }
        public ConcurrentQueue<int> InvocationThreadIds { get; } = new();

        public Task RunAsync(Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                Task task;
                try
                {
                    task = action();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                    return;
                }

                _ = task.ContinueWith(
                    completed => Complete(completed, completion),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            });
            return completion.Task;
        }

        public async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            cancellationToken.ThrowIfCancellationRequested();
            if (Environment.CurrentManagedThreadId == ThreadId)
            {
                action();
                InvocationThreadIds.Enqueue(Environment.CurrentManagedThreadId);
                return;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            _queue.Add(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    action();
                    InvocationThreadIds.Enqueue(Environment.CurrentManagedThreadId);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }, cancellationToken);
            await completion.Task.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _queue.CompleteAdding();
            var joined = await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(5)));
            if (!joined)
            {
                throw new TimeoutException("The dedicated UI test thread did not stop.");
            }

            _queue.Dispose();
        }

        private static void Complete(Task task, TaskCompletionSource completion)
        {
            if (task.IsCanceled)
            {
                completion.TrySetCanceled();
            }
            else if (task.Exception is not null)
            {
                completion.TrySetException(task.Exception.InnerExceptions);
            }
            else
            {
                completion.TrySetResult();
            }
        }

        private void Run()
        {
            SynchronizationContext.SetSynchronizationContext(new QueueSynchronizationContext(_queue));
            _started.TrySetResult(Environment.CurrentManagedThreadId);
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        }

        private sealed class QueueSynchronizationContext(BlockingCollection<Action> queue) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object? state) =>
                queue.Add(() => callback(state));
        }
    }

    private sealed class SerialDispatcher : IUiDispatcher
    {
        private readonly Queue<(Action Action, TaskCompletionSource Completion)> _pending = new();
        private int _enqueueCount;

        public TaskCompletionSource FirstEnqueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEnqueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending)
            {
                _pending.Enqueue((action, completion));
                var count = ++_enqueueCount;
                if (count >= 1)
                {
                    FirstEnqueued.TrySetResult();
                }

                if (count >= 2)
                {
                    SecondEnqueued.TrySetResult();
                }
            }

            return completion.Task;
        }

        public void RunAll()
        {
            while (true)
            {
                (Action Action, TaskCompletionSource Completion) work;
                lock (_pending)
                {
                    if (_pending.Count == 0)
                    {
                        return;
                    }

                    work = _pending.Dequeue();
                }

                try
                {
                    work.Action();
                    work.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    work.Completion.TrySetException(exception);
                }
            }
        }
    }

    private sealed class MutationBlockingDispatcher : IUiDispatcher
    {
        public TaskCompletionSource MutationApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            MutationApplied.TrySetResult();
            await ReleaseReturn.Task.ConfigureAwait(false);
        }
    }

    private sealed class BlockingDeleteRepository(params ConnectionProfile[] profiles) : IDeviceRepository
    {
        private readonly object _gate = new();
        private readonly List<ConnectionProfile> _profiles = [.. profiles];
        private int _deleteCount;

        public TaskCompletionSource FirstDeleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstDelete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken FirstDeleteToken { get; private set; }
        public Exception? DeleteException { get; init; }
        public List<Guid> DeleteEnteredIds { get; } = [];
        public List<Guid> DeletedIds { get; } = [];

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(GetSnapshot().SingleOrDefault(profile => profile.Id == id));
        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(GetSnapshot());

        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            var deleteCount = Interlocked.Increment(ref _deleteCount);
            lock (_gate)
            {
                DeleteEnteredIds.Add(id);
            }

            if (deleteCount == 1)
            {
                FirstDeleteToken = cancellationToken;
                FirstDeleteEntered.TrySetResult();
                await ReleaseFirstDelete.Task.WaitAsync(cancellationToken);
            }

            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            lock (_gate)
            {
                DeletedIds.Add(id);
                _profiles.RemoveAll(profile => profile.Id == id);
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalled.TrySetResult();
            return ValueTask.CompletedTask;
        }

        private ConnectionProfile[] GetSnapshot()
        {
            lock (_gate)
            {
                return _profiles.ToArray();
            }
        }
    }

    private sealed class FakeRepository(params ConnectionProfile[] profiles) : IDeviceRepository
    {
        private readonly List<ConnectionProfile> _profiles = [.. profiles];

        public bool BlockLoad { get; set; }
        public int LoadCount { get; private set; }
        public int LoadThreadId { get; private set; }
        public int DisposeCount { get; private set; }
        public Exception? DeleteException { get; init; }
        public Exception? DisposeException { get; init; }
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
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }

    private sealed class FakeDiscovery(params DiscoveredDevice[] devices) : IDeviceDiscovery
    {
        private readonly List<DiscoveredDevice> _current = [.. devices];

        public event EventHandler<DiscoveryChange>? Changed;
        public IReadOnlyList<DiscoveredDevice> Current
        {
            get
            {
                var snapshot = _current.ToArray();
                CurrentReadEntered.TrySetResult();
                if (BlockCurrentRead)
                {
                    ReleaseCurrentRead.Wait(TimeSpan.FromSeconds(5));
                }

                return snapshot;
            }
        }

        public bool BlockCurrentRead { get; init; }
        public TaskCompletionSource CurrentReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseCurrentRead { get; } = new(initialState: false);
        public int StartCount { get; private set; }
        public int StartThreadId { get; private set; }
        public int DisposeCount { get; private set; }
        public Exception? DisposeException { get; init; }

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
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore, IAsyncDisposable
    {
        public List<CredentialReference> DeletedReferences { get; } = [];
        public Exception? DeleteException { get; init; }
        public Exception? DisposeException { get; init; }
        public int DisposeCount { get; private set; }

        public ValueTask SaveAsync(CredentialReference reference, ISecret secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ISecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CredentialStoreSnapshot?> ReadSnapshotAsync(CredentialReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CredentialStoreCompareExchangeResult> CompareExchangeAsync(CredentialReference reference, CredentialStoreVersion? expectedVersion, ISecret? replacement, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CredentialStoreWriteResult> CompareExchangeWithVersionAsync(CredentialReference reference, CredentialStoreVersion? expectedVersion, ISecret? replacement, CancellationToken cancellationToken) => throw new NotSupportedException();

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

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }
}
