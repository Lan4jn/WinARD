using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinARD.Application.Ports;
using WinARD.Desktop.Threading;
using WinARD.Domain.Connections;

namespace WinARD.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly IDeviceRepository _repository;
    private readonly IDeviceDiscovery _discovery;
    private readonly ICredentialStore _credentialStore;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _deleteGate = new(1, 1);
    private readonly List<DeviceItemViewModel> _allSaved = [];
    private readonly HashSet<Guid> _deletedIds = [];
    private Task? _initializeTask;
    private Task? _disposeTask;
    private string _searchText = string.Empty;
    private DeviceItemViewModel? _selectedDevice;
    private string _statusMessage = string.Empty;
    private bool _initialized;
    private bool _disposed;
    private bool _isDeleting;

    public MainWindowViewModel(
        IDeviceRepository repository,
        IDeviceDiscovery discovery,
        ICredentialStore credentialStore,
        IUiDispatcher dispatcher)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _discovery.Changed += OnDiscoveryChanged;
        AddDeviceCommand = new RelayCommand(() => AddDeviceRequested?.Invoke(null));
        ActivateSelectedCommand = new RelayCommand(ActivateSelected, () => SelectedDevice is not null);
        EditDeviceCommand = new RelayCommand(
            () => EditDeviceRequested?.Invoke(SelectedDevice!.Profile!),
            () => SelectedDevice?.Profile is not null);
    }

    public ObservableCollection<DeviceItemViewModel> SavedDevices { get; } = [];
    public ObservableCollection<DeviceItemViewModel> DiscoveredDevices { get; } = [];
    public IRelayCommand AddDeviceCommand { get; }
    public IRelayCommand EditDeviceCommand { get; }
    public IRelayCommand ActivateSelectedCommand { get; }

    public event Action<ConnectionEditorDraft?>? AddDeviceRequested;

    public event Action<ConnectionProfile>? ConnectRequested;

    public event Action<WinARD.Domain.Connections.ConnectionProfile>? EditDeviceRequested;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty) && _initialized)
            {
                _ = RefreshSavedDevicesAsync();
            }
        }
    }

    public DeviceItemViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                EditDeviceCommand.NotifyCanExecuteChanged();
                ActivateSelectedCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void ActivateSelected()
    {
        if (SelectedDevice is not { } item)
        {
            return;
        }

        if (item.Profile is { } profile)
        {
            ConnectRequested?.Invoke(profile);
            return;
        }

        AddDeviceRequested?.Invoke(new ConnectionEditorDraft(
            item.DisplayName,
            item.Host,
            item.Port,
            string.Empty));
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasNoDevices => SavedDevices.Count == 0 && DiscoveredDevices.Count == 0;

    public bool IsDeleting
    {
        get => _isDeleting;
        private set => SetProperty(ref _isDeleting, value);
    }

    public Task ApplySavedProfileAsync(
        WinARD.Domain.Connections.ConnectionProfile profile,
        CancellationToken cancellationToken) =>
        ApplySavedProfileAsync(profile, warning: null, cancellationToken);

    public Task ApplySavedProfileAsync(
        WinARD.Domain.Connections.ConnectionProfile profile,
        string? warning,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _dispatcher.InvokeAsync(() =>
        {
            var item = DeviceItemViewModel.FromProfile(profile);
            _allSaved.RemoveAll(existing => existing.Profile?.Id == profile.Id);
            _allSaved.Add(item);
            ReplaceSavedDevices();
            SelectedDevice = SavedDevices.FirstOrDefault(existing => existing.Profile?.Id == profile.Id) ?? item;
            StatusMessage = warning ?? $"已保存“{profile.DisplayName}”。";
        }, cancellationToken);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
            {
                return Task.CompletedTask;
            }

            _initializeTask ??= InitializeCoreAsync(cancellationToken);
            return _initializeTask;
        }
    }

    public async Task<bool> DeleteSelectedAsync(bool? deleteCredential, CancellationToken cancellationToken)
    {
        var item = SelectedDevice;
        if (deleteCredential is null || item?.Profile is null)
        {
            return false;
        }

        var profile = item.Profile;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var operationToken = linked.Token;
        await _deleteGate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            operationToken.ThrowIfCancellationRequested();
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            if (_deletedIds.Contains(profile.Id))
            {
                return false;
            }

            await _dispatcher.InvokeAsync(() => IsDeleting = true, operationToken).ConfigureAwait(false);
            // DisposeAsync waits for this gate, so shutdown must not interrupt an in-flight persistence commit.
            var commitToken = CancellationToken.None;
            var credentialCleanupFailed = false;
            try
            {
                await Task.Run(
                    () => _repository.DeleteAsync(profile.Id, commitToken),
                    commitToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await _dispatcher.InvokeAsync(
                    () => StatusMessage = "无法删除设备。请重试。",
                    CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            _deletedIds.Add(profile.Id);
            if (deleteCredential.Value)
            {
                IReadOnlyList<WinARD.Domain.Connections.ConnectionProfile>? remainingProfiles = null;
                try
                {
                    remainingProfiles = await Task.Run(
                        () => _repository.GetAllAsync(commitToken),
                        commitToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    credentialCleanupFailed = true;
                }

                if (remainingProfiles is not null)
                {
                    var retainedReferences = remainingProfiles.SelectMany(GetCredentialReferences).ToHashSet();
                    foreach (var reference in GetCredentialReferences(profile).Except(retainedReferences))
                    {
                        try
                        {
                            await Task.Run(
                                async () => await _credentialStore.DeleteAsync(reference, commitToken).ConfigureAwait(false),
                                commitToken).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            credentialCleanupFailed = true;
                        }
                    }
                }
            }

            await _dispatcher.InvokeAsync(() =>
            {
                _allSaved.RemoveAll(saved => saved.Profile?.Id == profile.Id);
                var visible = SavedDevices.FirstOrDefault(saved => saved.Profile?.Id == profile.Id);
                if (visible is not null)
                {
                    SavedDevices.Remove(visible);
                }

                if (SelectedDevice?.Profile?.Id == profile.Id)
                {
                    SelectedDevice = null;
                }

                OnPropertyChanged(nameof(HasNoDevices));
                StatusMessage = credentialCleanupFailed
                    ? "设备已删除，但部分关联凭据未能删除。"
                    : $"已删除“{profile.DisplayName}”。";
            }, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        finally
        {
            try
            {
                await _dispatcher.InvokeAsync(() => IsDeleting = false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) when (_lifetime.IsCancellationRequested)
            {
            }

            _deleteGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task InitializeCoreAsync(CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
        var cancellationToken = linked.Token;
        try
        {
            var profiles = await Task.Run(
                () => _repository.GetAllAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await Task.Run(
                () => _discovery.StartAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var saved = profiles.Select(DeviceItemViewModel.FromProfile).ToArray();
            await _dispatcher.InvokeAsync(() =>
            {
                var discovered = _discovery.Current
                    .Select(DeviceItemViewModel.FromDiscovery)
                    .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                _allSaved.Clear();
                _allSaved.AddRange(saved);
                ReplaceSavedDevices();
                Replace(DiscoveredDevices, discovered);
                if (SelectedDevice?.Profile is null && SelectedDevice is not null)
                {
                    SelectedDevice = discovered.FirstOrDefault(item => item.Identity == SelectedDevice.Identity);
                }

                OnPropertyChanged(nameof(HasNoDevices));
            }, cancellationToken).ConfigureAwait(false);

            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    throw new OperationCanceledException(_lifetime.Token);
                }

                _initialized = true;
            }
        }
        finally
        {
            lock (_lifecycleGate)
            {
                if (!_initialized)
                {
                    _initializeTask = null;
                }
            }
        }
    }

    private Task RefreshSavedDevicesAsync() =>
        _dispatcher.InvokeAsync(ReplaceSavedDevices, _lifetime.Token);

    private void ReplaceSavedDevices()
    {
        var query = _searchText.Trim();
        var visible = _allSaved
            .Where(item => query.Length == 0 ||
                item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Host.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        Replace(SavedDevices, visible);
        OnPropertyChanged(nameof(HasNoDevices));
    }

    private void OnDiscoveryChanged(object? sender, DiscoveryChange change)
    {
        if (_disposed)
        {
            return;
        }

        _ = ApplyDiscoveryChangeAsync(change);
    }

    private async Task ApplyDiscoveryChangeAsync(DiscoveryChange change)
    {
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                var existing = DiscoveredDevices.FirstOrDefault(item => item.Identity == change.Device.Identity);
                if (existing is not null)
                {
                    DiscoveredDevices.Remove(existing);
                }

                var selectionMatches = SelectedDevice?.Profile is null &&
                    SelectedDevice?.Identity == change.Device.Identity;
                if (change.Kind != DiscoveryChangeKind.Removed)
                {
                    var added = DeviceItemViewModel.FromDiscovery(change.Device);
                    var index = 0;
                    while (index < DiscoveredDevices.Count &&
                        StringComparer.CurrentCultureIgnoreCase.Compare(DiscoveredDevices[index].DisplayName, added.DisplayName) <= 0)
                    {
                        index++;
                    }

                    DiscoveredDevices.Insert(index, added);
                    if (selectionMatches)
                    {
                        SelectedDevice = added;
                    }
                }
                else if (selectionMatches)
                {
                    SelectedDevice = null;
                }

                OnPropertyChanged(nameof(HasNoDevices));
            }, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task? initialize;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            initialize = _initializeTask;
        }

        _discovery.Changed -= OnDiscoveryChanged;
        var errors = new List<Exception>();
        if (initialize is not null)
        {
            try
            {
                await initialize.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        await _deleteGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _deleteGate.Release();
        await CaptureCleanupAsync(
            () => _discovery.StopAsync(CancellationToken.None), errors).ConfigureAwait(false);
        await CaptureCleanupAsync(
            async () => await _discovery.DisposeAsync().ConfigureAwait(false), errors).ConfigureAwait(false);
        await CaptureCleanupAsync(
            async () => await _repository.DisposeAsync().ConfigureAwait(false), errors).ConfigureAwait(false);
        if (_credentialStore is IAsyncDisposable asyncCredentialStore)
        {
            await CaptureCleanupAsync(
                async () => await asyncCredentialStore.DisposeAsync().ConfigureAwait(false), errors).ConfigureAwait(false);
        }
        else if (_credentialStore is IDisposable credentialStore)
        {
            await CaptureCleanupAsync(() =>
            {
                credentialStore.Dispose();
                return Task.CompletedTask;
            }, errors).ConfigureAwait(false);
        }

        _deleteGate.Dispose();
        _lifetime.Dispose();
        if (errors.Count > 0)
        {
            throw new AggregateException(errors);
        }
    }

    private static async Task CaptureCleanupAsync(Func<Task> cleanup, List<Exception> errors)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static void Replace(
        ObservableCollection<DeviceItemViewModel> collection,
        IEnumerable<DeviceItemViewModel> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private static IEnumerable<WinARD.Domain.Security.CredentialReference> GetCredentialReferences(
        WinARD.Domain.Connections.ConnectionProfile profile)
    {
        if (profile.CredentialReference is not null)
        {
            yield return profile.CredentialReference;
        }

        if (profile.SshProfile?.PasswordCredentialReference is not null)
        {
            yield return profile.SshProfile.PasswordCredentialReference;
        }

        if (profile.SshProfile?.PrivateKeyPassphraseCredentialReference is not null)
        {
            yield return profile.SshProfile.PrivateKeyPassphraseCredentialReference;
        }
    }
}
