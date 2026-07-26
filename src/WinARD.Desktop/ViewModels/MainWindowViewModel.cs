using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinARD.Application.Ports;
using WinARD.Desktop.Threading;

namespace WinARD.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly IDeviceRepository _repository;
    private readonly IDeviceDiscovery _discovery;
    private readonly ICredentialStore _credentialStore;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<DeviceItemViewModel> _allSaved = [];
    private Task? _initializeTask;
    private Task? _disposeTask;
    private string _searchText = string.Empty;
    private DeviceItemViewModel? _selectedDevice;
    private string _statusMessage = string.Empty;
    private bool _initialized;
    private bool _disposed;

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
        AddDeviceCommand = new RelayCommand(() => StatusMessage = "添加设备向导将在下一阶段提供。");
    }

    public ObservableCollection<DeviceItemViewModel> SavedDevices { get; } = [];
    public ObservableCollection<DeviceItemViewModel> DiscoveredDevices { get; } = [];
    public IRelayCommand AddDeviceCommand { get; }

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
        set => SetProperty(ref _selectedDevice, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasNoDevices => SavedDevices.Count == 0 && DiscoveredDevices.Count == 0;

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
        var credentialReferences = deleteCredential.Value
            ? GetCredentialReferences(profile)
                .Except(
                    _allSaved
                        .Where(saved => saved.Profile?.Id != profile.Id)
                        .SelectMany(saved => GetCredentialReferences(saved.Profile!)))
                .ToArray()
            : [];
        var credentialCleanupFailed = false;
        try
        {
            await Task.Run(
                () => _repository.DeleteAsync(profile.Id, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            await _dispatcher.InvokeAsync(
                () => StatusMessage = "无法删除设备。请重试。",
                CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        foreach (var reference in credentialReferences)
        {
            try
            {
                await Task.Run(
                    async () => await _credentialStore.DeleteAsync(reference, CancellationToken.None).ConfigureAwait(false),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                credentialCleanupFailed = true;
            }
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _allSaved.RemoveAll(saved => saved.Profile?.Id == profile.Id);
            SavedDevices.Remove(item);
            if (ReferenceEquals(SelectedDevice, item))
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
            var discovered = _discovery.Current.Select(DeviceItemViewModel.FromDiscovery).ToArray();
            await _dispatcher.InvokeAsync(() =>
            {
                _allSaved.Clear();
                _allSaved.AddRange(saved);
                ReplaceSavedDevices();
                Replace(DiscoveredDevices, discovered.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase));
                OnPropertyChanged(nameof(HasNoDevices));
            }, cancellationToken).ConfigureAwait(false);

            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
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
        if (initialize is not null)
        {
            try
            {
                await initialize.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await _discovery.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _discovery.DisposeAsync().ConfigureAwait(false);
            await _repository.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
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
